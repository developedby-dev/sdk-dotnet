using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Bydev;

/// <summary>명세 1절의 설정이다.</summary>
public sealed class LicenseClientOptions
{
    public required string BaseUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ProductId { get; init; }
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>세션을 쓰면 필요하다(<see cref="OsPorts.SystemDeviceInfo"/>).</summary>
    public DeviceInfo? Device { get; init; }

    /// <summary>실행 중인 앱의 배포 대상 · 버전이다 — 세션을 쓰면 넣는다(명세 1절 · ADR-0049). 세션 획득에 싣는다.</summary>
    public AppInfo? App { get; init; }

    public Ports Ports { get; init; } = new();
}

/// <summary>
/// 데브 라이선스 API 클라이언트다. 서버 응답 하나의 참/거짓으로 기능을 열지 않는다 — <see cref="Session"/>의 상태와 검증된 토큰의 클레임을 쓴다.
/// </summary>
public sealed class LicenseClient
{
    /// <summary>User-Agent(<c>bydev-dotnet/&lt;판&gt;</c>)에 실린다.</summary>
    public const string SdkVersion = "0.1.0";

    internal const string UserAgent = "bydev-dotnet/" + SdkVersion;

    private readonly LicenseClientOptions _options;
    private readonly Api _api;
    private readonly TokenManager _tokens;
    private readonly Verifier _verifier;
    private readonly object _gate = new();
    private LoginAttempt? _login;
    private LicenseSession? _session;

    public LicenseClient(LicenseClientOptions options)
    {
        if (options.Scopes.Count == 0) throw BydevException.Sdk(Codes.Configuration, "scopes가 비었다");
        _options = options;
        _api = new Api(options.BaseUrl, options.Ports.Resolve(), UserAgent);
        _tokens = new TokenManager(_api, options.ClientId);
        _verifier = new Verifier(_api, options.ClientId, true);
    }

    /// <summary>디바이스 로그인을 시작한다(명세 5절). 진행 중인 로그인은 취소한다.</summary>
    public async Task<LoginAttempt> StartLoginAsync()
    {
        LoginAttempt? previous;
        lock (_gate)
        {
            previous = _login;
            _login = null;
        }
        previous?.Cancel();
        var doc = await _api.DiscoveryAsync().ConfigureAwait(false);
        var body = Body.Of(("client_id", _options.ClientId), ("scope", string.Join(" ", _options.Scopes)));
        var res = await _api.SendAsync(new Req("POST", doc.DeviceAuthorizationEndpoint, body)).ConfigureAwait(false);
        var attempt = new LoginAttempt(DeviceCode.Read(res.Json), _options.ClientId, _api, _tokens);
        lock (_gate) _login = attempt;
        return attempt;
    }

    /// <summary>저장된 로그인이 있는지다 — 이용 가능 여부가 아니다.</summary>
    public async Task<bool> HasCredentialsAsync() => await _tokens.LoadAsync().ConfigureAwait(false) is not null;

    private async Task<ApiResponse> GetAsync(string url)
    {
        var res = await _tokens.AuthedAsync(bearer => _api.SendAsync(new Req("GET", url, Bearer: bearer))).ConfigureAwait(false);
        NoteLease(res);
        return res;
    }

    private void NoteLease(ApiResponse res)
    {
        LicenseSession? session;
        lock (_gate) session = _session;
        if (session is not null && res.Headers.TryGetValue("x-lease-expires-at", out var lease)) session.NoteLease(lease);
    }

    /// <summary>이 제품의 이용권이다.</summary>
    public async Task<Entitlement> EntitlementAsync() =>
        Entitlement.Read((await GetAsync(_api.Url(Routes.GetMyEntitlement.Path)).ConfigureAwait(false)).Json);

    /// <summary>등록된 기기 한 쪽이다. 다음 쪽을 저절로 따라가지 않는다.</summary>
    public async Task<DevicePage> DevicesAsync(string? cursor = null, int? limit = null)
    {
        var query = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(cursor)) query.Add(new("cursor", cursor!));
        if (limit is > 0) query.Add(new("limit", limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return DevicePage.Read((await GetAsync(_api.Url(Routes.ListDevices.Path, null, query)).ConfigureAwait(false)).Json);
    }

    /// <summary>기기를 해제한다(월 횟수 제한).</summary>
    public async Task<DeviceRelease> ReleaseDeviceAsync(string deviceId)
    {
        var key = _api.Ports.Random.NewUuid();
        var url = _api.Url(Routes.ReleaseDevice.Path, new Dictionary<string, string> { ["deviceId"] = deviceId });
        var res = await _tokens.AuthedAsync(bearer => _api.SendAsync(new Req("DELETE", url, Bearer: bearer, IdempotencyKey: key)))
            .ConfigureAwait(false);
        NoteLease(res);
        return DeviceRelease.Read(res.Json);
    }

    /// <summary>닉네임과 가명 식별자다.</summary>
    public async Task<Profile> ProfileAsync() =>
        Profile.Read((await GetAsync(_api.Url(Routes.GetProfile.Path)).ConfigureAwait(false)).Json);

    /// <summary>
    /// 업데이트 확인이다. <see cref="UpdateCheck.Entitled"/>가 있으면 최상위 대신 그 Release를 따른다. <see cref="UpdateCheck.UpdateStatus"/>가 REQUIRED면
    /// 업데이트 전까지 실행을 막는다.
    /// </summary>
    public async Task<UpdateCheck> CheckForUpdateAsync(string target, string currentVersion)
    {
        var url = _api.Url(Routes.GetProductUpdates.Path, new Dictionary<string, string> { ["id"] = _options.ProductId },
            new[] { new KeyValuePair<string, string>("target", target), new("currentVersion", currentVersion) });
        return UpdateCheck.Read((await GetAsync(url).ConfigureAwait(false)).Json);
    }

    /// <summary>내려받은 파일의 SHA-256을 확인한다 — 다르면 DOWNLOAD_INTEGRITY.</summary>
    public void VerifyDownload(byte[] data, string sha256)
    {
        using var sha = SHA256.Create();
        if (Encoding64.Hex(sha.ComputeHash(data)) != sha256.ToLowerInvariant())
            throw BydevException.Sdk(Codes.DownloadIntegrity, "내려받은 파일의 SHA-256이 다르다 — 설치하지 않는다");
    }

    /// <summary>licenseToken을 검증한다(명세 9절) — 앱이 기능 코드 여러 곳에서 다시 검증할 때.</summary>
    public Task<LicenseClaims> VerifyTokenAsync(string token) => _verifier.VerifyLicenseAsync(token, null, 0);

    /// <summary>세션을 멈추고 폐기 요청을 한 번 보낸 뒤 기록을 지운다. 서버 폐기에 성공했는지를 돌려준다.</summary>
    public async Task<bool> LogoutAsync()
    {
        LoginAttempt? login;
        LicenseSession? session;
        lock (_gate)
        {
            login = _login;
            _login = null;
            session = _session;
        }
        login?.Cancel();
        if (session is not null) await session.StopAsync().ConfigureAwait(false);
        return await _tokens.LogoutAsync().ConfigureAwait(false);
    }

    /// <summary>이 클라이언트의 세션이다(하나뿐).</summary>
    public LicenseSession Session
    {
        get
        {
            lock (_gate)
            {
                if (_session is not null) return _session;
                var device = _options.Device
                    ?? throw BydevException.Sdk(Codes.Configuration, "세션을 쓰려면 Device { Name, Platform }을 준다");
                return _session = new LicenseSession(_api, _tokens, _verifier, device, _options.App);
            }
        }
    }
}
