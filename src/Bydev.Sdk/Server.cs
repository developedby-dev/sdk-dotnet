using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Bydev;

/// <summary>검증된 웹훅의 id와 시각이다. 중복 수신은 id로 거른다.</summary>
public sealed record WebhookVerification(string Id, long Timestamp);

/// <summary>명세 12절 — 판매자 서버의 웹훅 서명 검증(안내서 11장 · Standard Webhooks v1).</summary>
public static class Webhooks
{
    /// <summary>
    /// 서명을 검증한다. <paramref name="rawBody"/>는 받은 그대로의 본문이다 — 파싱했다가 다시 만든 바이트는 서명이 맞지 않는다. 시크릿을 교체하는 중이면 둘 다
    /// 넘긴다. 헤더 이름의 대소문자는 가리지 않는다. <paramref name="clock"/>이 null이면 시스템 시계.
    /// </summary>
    public static WebhookVerification Verify(IEnumerable<string> secrets, IEnumerable<KeyValuePair<string, string>> headers,
        byte[] rawBody, IClock? clock = null)
    {
        string? Header(string name) =>
            headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        var id = Header("webhook-id");
        var timestamp = Header("webhook-timestamp");
        var signatures = Header("webhook-signature");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatures))
            throw BydevException.Sdk(Codes.WebhookInvalid, "웹훅 헤더(webhook-id · webhook-timestamp · webhook-signature)가 없다");
        var nowSec = (clock ?? SystemClock.Instance).NowMs() / 1000.0;
        if (!long.TryParse(timestamp, out var sent) || Math.Abs(nowSec - sent) > Rules.WebhookToleranceSec)
            throw BydevException.Sdk(Codes.WebhookInvalid, "웹훅 시각이 허용 범위 밖이다 — 재전송 공격일 수 있다");
        var candidates = new List<byte[]>();
        foreach (var entry in signatures!.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = entry.IndexOf(',');
            if (comma < 0 || entry.Substring(0, comma) != Rules.WebhookSignatureVersion) continue;
            try
            {
                candidates.Add(Convert.FromBase64String(entry.Substring(comma + 1)));
            }
            catch (FormatException)
            {
            }
        }
        var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.").Concat(rawBody).ToArray();
        foreach (var secret in secrets)
        {
            if (!secret.StartsWith(Rules.WebhookSecretPrefix, StringComparison.Ordinal))
                throw BydevException.Sdk(Codes.Configuration, $"웹훅 시크릿은 {Rules.WebhookSecretPrefix}로 시작한다");
            byte[] key;
            try
            {
                key = Convert.FromBase64String(secret.Substring(Rules.WebhookSecretPrefix.Length));
            }
            catch (FormatException e)
            {
                throw BydevException.Sdk(Codes.Configuration, "웹훅 시크릿을 읽지 못했다", e);
            }
            using var mac = new HMACSHA256(key);
            var expected = mac.ComputeHash(signed);
            if (candidates.Any(c => FixedEquals(c, expected))) return new WebhookVerification(id!, sent);
        }
        throw BydevException.Sdk(Codes.WebhookInvalid, "웹훅 서명이 맞지 않는다");
    }

    /// <summary>문자열 본문(UTF-8)으로.</summary>
    public static WebhookVerification Verify(IEnumerable<string> secrets, IEnumerable<KeyValuePair<string, string>> headers,
        string rawBody, IClock? clock = null) => Verify(secrets, headers, Encoding.UTF8.GetBytes(rawBody), clock);

    private static bool FixedEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

/// <summary>판매자 서버의 토큰 검증이다(안내서 09장) — JWKS를 받아 캐시한다.</summary>
public sealed class ServerVerifier
{
    private readonly Verifier _verifier;

    /// <summary><paramref name="ports"/>는 Transport · Clock만 쓴다(비우면 기본 구현).</summary>
    public ServerVerifier(string baseUrl, string clientId, Ports? ports = null)
    {
        var api = new Api(baseUrl, (ports ?? new Ports()).Resolve(), LicenseClient.UserAgent);
        _verifier = new Verifier(api, clientId, false);
    }

    /// <summary>access_token(at+jwt)을 검증한다.</summary>
    public Task<AccessClaims> VerifyAccessTokenAsync(string token) => _verifier.VerifyAccessAsync(token);

    /// <summary>licenseToken을 검증한다. minGeneration — 같은 sub · device_id에서 이미 본 가장 큰 세대번호(0이면 보지 않는다).</summary>
    public Task<LicenseClaims> VerifyLicenseTokenAsync(string token, long minGeneration = 0) =>
        _verifier.VerifyLicenseAsync(token, null, minGeneration);
}
