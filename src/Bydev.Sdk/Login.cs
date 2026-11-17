using System;
using System.Threading.Tasks;

namespace Bydev;

/// <summary>로그인이 끝났을 때 받은 스코프다.</summary>
public sealed record LoginResult(string Scope);

/// <summary>디바이스 플로우 하나다(명세 5절). 화면에 <see cref="UserCode"/> · <see cref="VerificationUriComplete"/>(QR)를 보이고 <see cref="WaitAsync"/>.</summary>
public sealed class LoginAttempt
{
    private readonly Api _api;
    private readonly TokenManager _tokens;
    private readonly string _clientId;
    private readonly string _deviceCode;
    private readonly long _expiresAtMs;
    private readonly TaskCompletionSource<LoginResult> _done = new();
    private readonly object _gate = new();
    private long _interval;
    private IDisposable? _timer;

    internal LoginAttempt(DeviceCode code, string clientId, Api api, TokenManager tokens)
    {
        _api = api;
        _tokens = tokens;
        _clientId = clientId;
        _deviceCode = code.Code;
        _interval = code.Interval;
        _expiresAtMs = api.Ports.Clock.NowMs() + code.ExpiresIn * 1000;
        UserCode = code.UserCode;
        VerificationUri = code.VerificationUri;
        VerificationUriComplete = code.VerificationUriComplete;
        ExpiresAt = Time.Iso(_expiresAtMs);
        SchedulePoll();
    }

    public string UserCode { get; }

    public string VerificationUri { get; }

    public string VerificationUriComplete { get; }

    /// <summary>코드가 만료되는 시각(ISO 8601).</summary>
    public string ExpiresAt { get; }

    /// <summary>로그인이 끝나기를 기다린다.</summary>
    public Task<LoginResult> WaitAsync() => _done.Task;

    /// <summary>로그인을 취소한다 — <see cref="WaitAsync"/>는 LOGIN_CANCELLED로 끝난다.</summary>
    public void Cancel() => Finish(null, BydevException.Sdk(Codes.LoginCancelled, "로그인을 취소했다"));

    private bool Settled => _done.Task.IsCompleted;

    private void Finish(LoginResult? result, BydevException? error)
    {
        IDisposable? timer;
        lock (_gate)
        {
            if (Settled) return;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        if (error is not null) _done.TrySetException(error);
        else _done.TrySetResult(result!);
    }

    /// <summary>폴링 지터는 위로만(interval보다 이르면 SLOW_DOWN이다).</summary>
    private void SchedulePoll()
    {
        long interval;
        lock (_gate) interval = _interval;
        var ports = _api.Ports;
        var delay = Timing.Jittered(ports, interval * 1000.0, Rules.LoginPollJitterMin, Rules.LoginPollJitterMax);
        if (ports.Clock.NowMs() + (long)delay.TotalMilliseconds > _expiresAtMs)
        {
            Finish(null, BydevException.Sdk(Codes.ExpiredToken, "로그인 코드가 만료됐다 — 처음부터 다시"));
            return;
        }
        var timer = ports.Scheduler.Schedule(delay, PollAsync);
        lock (_gate)
        {
            if (!Settled)
            {
                _timer = timer;
                return;
            }
        }
        timer.Dispose();
    }

    private async Task PollAsync()
    {
        if (Settled) return;
        try
        {
            var doc = await _api.DiscoveryAsync().ConfigureAwait(false);
            var body = Body.Of(("grant_type", Spec.DeviceCodeGrantType), ("client_id", _clientId), ("device_code", _deviceCode));
            var res = await _api.SendAsync(new Req("POST", doc.TokenEndpoint, body)).ConfigureAwait(false);
            if (Settled) return;
            var set = TokenSet.Read(res.Json);
            await _tokens.AdoptAsync(set).ConfigureAwait(false);
            Finish(new LoginResult(set.Scope), null);
        }
        catch (BydevException e)
        {
            if (Settled) return;
            if (e.Code == Codes.AuthorizationPending || e.IsTransient)
            {
                SchedulePoll();
            }
            else if (e.Code == Codes.SlowDown)
            {
                lock (_gate) _interval = e.RetryAfterSec is > 0 and int n ? n : _interval + Rules.LoginSlowDownIncrementSec;
                SchedulePoll();
            }
            else
            {
                Finish(null, e);
            }
        }
    }
}
