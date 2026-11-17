using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Bydev.Tests;

internal sealed class FixedClock(long ms) : IClock
{
    public long NowMs() => ms;

    public static FixedClock AtSec(long sec) => new(sec * 1000);
}

public class WebhookTests
{
    // 안내서 11장의 서명 예시 — 문서와 SDK가 같은 값으로 맞는다(TS · Go 시험과 같은 값).
    private const string Secret = "whsec_/MU12AiKByBoT7xt+p81N/uKdp/WxQRb";
    private const string Id = "evt_01J9MC4D6E8F0G2H4J6K8M0N2P";
    private const long Sent = 1791295205;
    private const string Body =
        """{"id":"evt_01J9MC4D6E8F0G2H4J6K8M0N2P","type":"build.review_completed","occurredAt":"2026-10-06T14:00:00Z","mode":"LIVE","simulated":false,"data":{"build":{"id":"build_01J9M4D6F8H0J2K4M6N8P0R2S4","productId":"prod_01J9KZ3F7W2X8YQ4N6M5R1TBCD","version":"1.4.2","reviewedAt":"2026-10-06T13:58:00Z","result":"APPROVED"}}}""";
    private const string Signature = "v1,vmsiq2HY4gik/WNJjXJzuESzXAjJgr8Vi13SKwwhgEo=";

    private static Dictionary<string, string> Headers(string signature) => new()
    {
        ["Webhook-Id"] = Id,
        ["Webhook-Timestamp"] = Sent.ToString(),
        ["Webhook-Signature"] = signature,
    };

    private static void Rejects(string code, Action verify) => Assert.Equal(code, Assert.Throws<BydevException>(verify).Code);

    [Fact]
    public void GuideExample()
    {
        var got = Webhooks.Verify(new[] { Secret }, Headers(Signature), Body, FixedClock.AtSec(Sent + 10));
        Assert.Equal(new WebhookVerification(Id, Sent), got);
        // 바이트 본문도
        Assert.Equal(Id, Webhooks.Verify(new[] { Secret }, Headers(Signature), Encoding.UTF8.GetBytes(Body), FixedClock.AtSec(Sent)).Id);
    }

    [Fact]
    public void Rotation()
    {
        // 서명 여러 개 · 시크릿 여러 개 중 하나라도 맞으면
        Webhooks.Verify(new[] { Secret }, Headers("v1,AAAA " + Signature), Body, FixedClock.AtSec(Sent));
        Webhooks.Verify(new[] { "whsec_AAAAAAAAAAAAAAAAAAAAAAAA", Secret }, Headers(Signature), Body, FixedClock.AtSec(Sent));
    }

    [Fact]
    public void RejectsTamperingReplayAndMissingHeaders()
    {
        var secrets = new[] { Secret };
        Rejects(Codes.WebhookInvalid, () => Webhooks.Verify(secrets, Headers(Signature), Body + " ", FixedClock.AtSec(Sent)));
        Rejects(Codes.WebhookInvalid, () => Webhooks.Verify(secrets, Headers(Signature), Body, FixedClock.AtSec(Sent + 301)));
        Rejects(Codes.WebhookInvalid, () => Webhooks.Verify(secrets, Headers(Signature), Body, FixedClock.AtSec(Sent - 301)));
        Rejects(Codes.WebhookInvalid, () => Webhooks.Verify(secrets, new Dictionary<string, string> { ["webhook-id"] = Id }, Body,
            FixedClock.AtSec(Sent)));
        Rejects(Codes.WebhookInvalid, () => Webhooks.Verify(secrets, Headers("v2," + Signature[3..]), Body, FixedClock.AtSec(Sent)));
        // 접두사 없는 시크릿은 설정 실수
        Rejects(Codes.Configuration, () => Webhooks.Verify(new[] { "/MU12AiKByBoT7xt+p81N/uKdp/WxQRb" }, Headers(Signature), Body,
            FixedClock.AtSec(Sent)));
    }
}

/// <summary>판매자 서버의 토큰 검증 — 발견 문서 · JWKS를 받아 at+jwt를 검증한다(실제 소켓 · 기본 전송).</summary>
public sealed class ServerVerifierTests : IDisposable
{
    private const string ClientId = "cli_01J9M0Y6X4N3Q8ZD5R2KT7VCPW";
    private const long Now = 1791295205;
    private readonly HttpListener _listener = new();
    private readonly Ed25519PrivateKeyParameters _key = new(new SecureRandom());
    private readonly string _base;
    private int _jwksHits;

    public ServerVerifierTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _base = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
        _ = ServeAsync();
    }

    public void Dispose() => _listener.Close();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            var (status, body) = context.Request.Url!.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => (200, JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["issuer"] = _base,
                    ["jwks_uri"] = _base + "/.well-known/jwks.json",
                    ["device_authorization_endpoint"] = _base + "/v1/oauth/device/code",
                    ["token_endpoint"] = _base + "/v1/oauth/token",
                    ["revocation_endpoint"] = _base + "/v1/oauth/revoke",
                })),
                "/.well-known/jwks.json" => (200, Jwks()),
                _ => (404, """{"code":"PRODUCT_NOT_FOUND","message":"없음"}"""),
            };
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private string Jwks()
    {
        Interlocked.Increment(ref _jwksHits);
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["keys"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["kty"] = "OKP", ["crv"] = "Ed25519", ["kid"] = "srv-k1", ["use"] = "sig", ["alg"] = "EdDSA",
                    ["x"] = B64Url(_key.GeneratePublicKey().GetEncoded()),
                },
            },
        });
    }

    private static string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string Mint(string kid, string aud)
    {
        var head = JsonSerializer.Serialize(new Dictionary<string, string> { ["alg"] = "EdDSA", ["typ"] = "at+jwt", ["kid"] = kid });
        var claims = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = _base,
            ["sub"] = "usr_01J9M0Y6X4N3Q8ZD5R2KT7VCPW",
            ["aud"] = aud,
            ["client_id"] = aud,
            ["scope"] = "entitlement.read",
            ["iat"] = Now,
            ["exp"] = Now + 3600,
            ["jti"] = "at_srv0001",
        });
        var input = B64Url(Encoding.UTF8.GetBytes(head)) + "." + B64Url(Encoding.UTF8.GetBytes(claims));
        var signer = new Ed25519Signer();
        signer.Init(true, _key);
        var data = Encoding.ASCII.GetBytes(input);
        signer.BlockUpdate(data, 0, data.Length);
        return input + "." + B64Url(signer.GenerateSignature());
    }

    [Fact]
    public async Task VerifiesAccessTokens()
    {
        var verifier = new ServerVerifier(_base, ClientId, new Ports { Clock = FixedClock.AtSec(Now) });
        var got = await verifier.VerifyAccessTokenAsync(Mint("srv-k1", ClientId));
        Assert.Equal(("usr_01J9M0Y6X4N3Q8ZD5R2KT7VCPW", "entitlement.read"), (got.Sub, got.Scope));
        var other = await Assert.ThrowsAsync<BydevException>(() => verifier.VerifyAccessTokenAsync(Mint("srv-k1", "cli_other_client_00000000")));
        Assert.Equal(Codes.TokenInvalid, other.Code);
        // 모르는 kid — 1분 안에는 다시 받지 않는다
        var unknown = await Assert.ThrowsAsync<BydevException>(() => verifier.VerifyAccessTokenAsync(Mint("srv-k9", ClientId)));
        Assert.Equal(Codes.TokenInvalid, unknown.Code);
        Assert.Equal(1, _jwksHits);
    }
}
