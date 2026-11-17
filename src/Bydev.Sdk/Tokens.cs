using System;
using System.Threading.Tasks;

namespace Bydev;

/// <summary>명세 3.5 · 3.6 · 6절 — 기록 보관 · 갱신 · 인증 요청 감싸기 · 로그아웃.</summary>
internal sealed class TokenManager
{
    private readonly Api _api;
    private readonly string _clientId;
    private readonly SingleFlight<string> _refreshing = new();
    private readonly object _gate = new();
    private (string Token, long ExpiresAtMs)? _access;

    public TokenManager(Api api, string clientId)
    {
        _api = api;
        _clientId = clientId;
    }

    private Resolved Ports => _api.Ports;

    private void SetAccess((string, long)? access)
    {
        lock (_gate) _access = access;
    }

    public async Task<CredentialRecord?> LoadAsync()
    {
        CredentialRecord? record;
        try
        {
            record = await Ports.Store.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw BydevException.Sdk(Codes.StorageFailed, "보안 저장소를 읽지 못했다", e);
        }
        return record is not null && record.Version == Rules.StorageRecordVersion ? record : null;
    }

    /// <summary>6절 3 · 5절 — 저장한 뒤에 쓴다.</summary>
    public async Task AdoptAsync(TokenSet set)
    {
        SetAccess(null);
        var record = new CredentialRecord(1, set.RefreshToken, set.Scope, Time.Iso(Ports.Clock.NowMs()));
        try
        {
            await Ports.Store.SaveAsync(record).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw BydevException.Sdk(Codes.StorageFailed, "새 로그인을 보안 저장소에 두지 못했다 — 새 토큰을 쓰지 않는다", e);
        }
        SetAccess((set.AccessToken, Ports.Clock.NowMs() + set.ExpiresIn * 1000));
    }

    private Task<string> AccessTokenAsync()
    {
        (string Token, long ExpiresAtMs)? access;
        lock (_gate) access = _access;
        if (access is { } a && a.ExpiresAtMs - Rules.TokenRefreshSkewSec * 1000L > Ports.Clock.NowMs())
            return Task.FromResult(a.Token);
        return RefreshAsync();
    }

    /// <summary>6절 1 — 한 번에 하나.</summary>
    public Task<string> RefreshAsync()
    {
        SetAccess(null);
        return _refreshing.Run(DoRefreshAsync);
    }

    private async Task<string> DoRefreshAsync()
    {
        var record = await LoadAsync().ConfigureAwait(false)
            ?? throw BydevException.Sdk(Codes.NotSignedIn, "로그인이 없다 — 먼저 로그인한다");
        var doc = await _api.DiscoveryAsync().ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var body = Body.Of(("grant_type", "refresh_token"), ("client_id", _clientId), ("refresh_token", record.RefreshToken));
                var res = await _api.SendAsync(new Req("POST", doc.TokenEndpoint, body)).ConfigureAwait(false);
                var set = TokenSet.Read(res.Json);
                await AdoptAsync(set).ConfigureAwait(false);
                return set.AccessToken;
            }
            catch (BydevException e) when (e.Code == Codes.InvalidGrant)
            {
                await ForgetStoredAsync().ConfigureAwait(false);
                throw;
            }
            catch (BydevException e) when (e.IsTransient && attempt < Rules.TokenRefreshRetryDelaysSec.Count)
            {
                await Timing.Delay(Ports, Timing.Seconds(Rules.TokenRefreshRetryDelaysSec[attempt])).ConfigureAwait(false);
            }
        }
    }

    /// <summary>3.5절 — 만료면 한 번만 갱신하고 다시 보낸다.</summary>
    public async Task<ApiResponse> AuthedAsync(Func<string, Task<ApiResponse>> send)
    {
        var token = await AccessTokenAsync().ConfigureAwait(false);
        try
        {
            return await send(token).ConfigureAwait(false);
        }
        catch (BydevException e) when (e.Code == Codes.ExpiredToken)
        {
        }
        try
        {
            return await send(await RefreshAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (BydevException e) when (e.Code == Codes.ExpiredToken)
        {
            // 갓 받은 토큰도 거절됐다 — 다음 요청이 같은 토큰을 다시 쓰지 않게 버린다
            SetAccess(null);
            throw;
        }
    }

    private async Task ForgetStoredAsync()
    {
        SetAccess(null);
        try
        {
            await Ports.Store.ClearAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            Ports.Logger.Warn("끝난 로그인의 기록을 지우지 못했다");
        }
    }

    /// <summary>7절 — 폐기 요청은 한 번, 결과와 상관없이 지운다. 서버가 폐기했는지를 돌려준다.</summary>
    public async Task<bool> LogoutAsync()
    {
        var record = await LoadAsync().ConfigureAwait(false);
        var revoked = false;
        if (record is not null)
        {
            try
            {
                var doc = await _api.DiscoveryAsync().ConfigureAwait(false);
                var body = Body.Of(("client_id", _clientId), ("token", record.RefreshToken), ("token_type_hint", "refresh_token"));
                await _api.SendAsync(new Req("POST", doc.RevocationEndpoint, body)).ConfigureAwait(false);
                revoked = true;
            }
            catch (BydevException)
            {
                revoked = false;
            }
        }
        SetAccess(null);
        try
        {
            await Ports.Store.ClearAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw BydevException.Sdk(Codes.StorageFailed, "로그인 기록을 지우지 못했다", e);
        }
        return revoked;
    }
}
