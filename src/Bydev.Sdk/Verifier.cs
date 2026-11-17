using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Bydev;

/// <summary>명세 9절 — 토큰 검증 · JWKS 캐시 · 세대번호 단조 확인.</summary>
internal sealed class Verifier
{
    private readonly Api _api;
    private readonly string _clientId;
    private readonly bool _trackGenerations;
    private readonly SingleFlight<KeySet> _fetching = new();
    private readonly Dictionary<string, long> _generations = new();
    private readonly object _gate = new();
    private KeySet? _set;

    private sealed record KeySet(IReadOnlyDictionary<string, byte[]> Keys, long FetchedAtMs, long ExpiresAtMs);

    internal sealed record Expectation(string Sid, string DeviceId, long Generation);

    public Verifier(Api api, string clientId, bool trackGenerations)
    {
        _api = api;
        _clientId = clientId;
        _trackGenerations = trackGenerations;
    }

    private static BydevException Invalid(string message) => BydevException.Sdk(Codes.TokenInvalid, message);

    private static BydevException Regressed(long got, long seen) =>
        BydevException.Sdk(Codes.GenerationRegressed, $"세대번호 {got} < 이미 본 {seen}");

    /// <summary>9절 1 ~ 4. 페이로드를 돌려준다.</summary>
    private async Task<JsonElement> VerifySignedAsync(string token, string typ)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) throw Invalid("JWT 형식이 아니다");
        var head = Encoding64.FromBase64Url(parts[0]);
        var body = Encoding64.FromBase64Url(parts[1]);
        var sig = Encoding64.FromBase64Url(parts[2]);
        var header = head is null ? null : Json.Parse(Encoding.UTF8.GetString(head));
        var payload = body is null ? null : Json.Parse(Encoding.UTF8.GetString(body));
        if (sig is null || header is not { ValueKind: JsonValueKind.Object } h || payload is not { ValueKind: JsonValueKind.Object } p)
            throw Invalid("JWT를 읽지 못했다");
        string? Text(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Text(h, "alg") != "EdDSA") throw Invalid($"alg {Text(h, "alg")}");
        if (Text(h, "typ") != typ) throw Invalid($"typ {Text(h, "typ")}");
        var kid = Text(h, "kid") ?? throw Invalid("kid가 없다");
        var key = await KeyForAsync(kid).ConfigureAwait(false);
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (sig.Length != Ed25519.SignatureSize || !Ed25519.Verify(sig, 0, key, 0, signed, 0, signed.Length))
            throw Invalid("서명이 맞지 않는다");
        var doc = await _api.DiscoveryAsync().ConfigureAwait(false);
        if (Text(p, "iss") != doc.Issuer) throw Invalid("iss가 다르다");
        if (Text(p, "aud") != _clientId) throw Invalid("aud가 이 client_id가 아니다");
        var nowSec = _api.Ports.Clock.NowMs() / 1000.0;
        if (!p.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number
            || exp.GetDouble() <= nowSec - Rules.TokenClockLeewaySec)
            throw Invalid("만료된 토큰");
        return p;
    }

    public async Task<LicenseClaims> VerifyLicenseAsync(string token, Expectation? expect, long minGeneration)
    {
        var payload = await VerifySignedAsync(token, "license+jwt").ConfigureAwait(false);
        LicenseClaims claims;
        try
        {
            claims = LicenseClaims.Read(payload);
        }
        catch (BydevException)
        {
            throw Invalid("클레임 모양이 다르다");
        }
        if (expect is not null)
        {
            if (claims.Sid != expect.Sid) throw Invalid("sid가 세션과 다르다");
            if (claims.DeviceId != expect.DeviceId) throw Invalid("device_id가 세션과 다르다");
            if (claims.Generation != expect.Generation) throw Invalid("generation이 세션과 다르다");
        }
        if (minGeneration > 0 && claims.Generation < minGeneration) throw Regressed(claims.Generation, minGeneration);
        if (_trackGenerations)
        {
            var slot = claims.Sub + "|" + claims.DeviceId;
            lock (_gate)
            {
                if (_generations.TryGetValue(slot, out var seen) && claims.Generation < seen) throw Regressed(claims.Generation, seen);
                if (!_generations.ContainsKey(slot) || claims.Generation > seen) _generations[slot] = claims.Generation;
            }
        }
        return claims;
    }

    public async Task<AccessClaims> VerifyAccessAsync(string token)
    {
        var payload = await VerifySignedAsync(token, "at+jwt").ConfigureAwait(false);
        try
        {
            return AccessClaims.Read(payload);
        }
        catch (BydevException)
        {
            throw Invalid("클레임 모양이 다르다");
        }
    }

    /// <summary>9.1절 — 지난 캐시는 쓰지 않는다 · 모르는 kid는 1분에 한 번만 다시 받는다.</summary>
    private async Task<byte[]> KeyForAsync(string kid)
    {
        var now = _api.Ports.Clock.NowMs();
        KeySet? set;
        lock (_gate) set = _set;
        if (set is null || now >= set.ExpiresAtMs) set = await FetchAsync().ConfigureAwait(false);
        if (set.Keys.TryGetValue(kid, out var key)) return key;
        if (now - set.FetchedAtMs < Rules.JwksUnknownKidRefetchMinIntervalSec * 1000L) throw Invalid("모르는 kid " + kid);
        set = await FetchAsync().ConfigureAwait(false);
        return set.Keys.TryGetValue(kid, out key) ? key : throw Invalid("모르는 kid " + kid);
    }

    private Task<KeySet> FetchAsync() => _fetching.Run(async () =>
    {
        var doc = await _api.DiscoveryAsync().ConfigureAwait(false);
        var res = await _api.SendAsync(new Req("GET", doc.JwksUri)).ConfigureAwait(false);
        var r = new Reader();
        var keys = new Dictionary<string, byte[]>();
        foreach (var item in r.Array(r.Object(res.Json, "$"), "keys", "keys"))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? Text(string k) => item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Text("kty") != "OKP" || Text("crv") != "Ed25519") continue;
            var kid = Text("kid");
            var raw = Text("x") is string x ? Encoding64.FromBase64Url(x) : null;
            if (string.IsNullOrEmpty(kid) || raw is null || raw.Length != Ed25519.PublicKeySize)
            {
                _api.Ports.Logger.Warn("JWKS 키를 읽지 못했다");
                continue;
            }
            keys[kid!] = raw;
        }
        r.Finish(0);
        var age = (long)Rules.JwksDefaultMaxAgeSec;
        if (res.Headers.TryGetValue("cache-control", out var cc))
        {
            foreach (var part in cc.Split(',').Select(s => s.Trim()))
                if (part.StartsWith("max-age=", StringComparison.Ordinal) && long.TryParse(part.Substring(8), out var n)) age = n;
        }
        var fetched = _api.Ports.Clock.NowMs();
        var set = new KeySet(keys, fetched, fetched + age * 1000);
        lock (_gate) _set = set;
        return set;
    });
}
