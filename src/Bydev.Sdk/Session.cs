using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bydev;

/// <summary>상태에 싣는 에러다.</summary>
public sealed record ErrorView(string Code, string Message, int? HttpStatus, string? CorrelationId, int? RetryAfterSec,
    JsonElement? Details)
{
    internal static ErrorView Of(BydevException e) =>
        new(e.Code, e.Message, e.HttpStatus, e.CorrelationId, e.RetryAfterSec, e.Details);

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("code", Code);
        w.WriteString("message", Message);
        Json.Int(w, "httpStatus", HttpStatus);
        Json.Str(w, "correlationId", CorrelationId);
        Json.Int(w, "retryAfterSec", RetryAfterSec);
        Json.Element(w, "details", Details);
        w.WriteEndObject();
    }
}

/// <summary>지금 쓰는 다른 기기(인수 판단에 보인다).</summary>
public sealed record ActiveSessionInfo(string DeviceId, string DeviceName, string LastSeenAt);

/// <summary>
/// 세션 상태(판별 유니온, 명세 8.1절) — <see cref="Type"/>(<see cref="SessionStateType"/>)으로 분기하거나 패턴 매칭한다. 참/거짓 하나로 이용 가능을 알리지 않는다.
/// </summary>
public abstract record SessionState
{
    public abstract string Type { get; }

    /// <summary><c>{"type": "Active", …}</c>.</summary>
    public string ToJson() => Json.Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("type", Type);
        WriteFields(w);
        w.WriteEndObject();
    });

    internal abstract void WriteFields(Utf8JsonWriter w);

    public sealed record Starting(int Attempt, ErrorView? LastError) : SessionState
    {
        public override string Type => SessionStateType.Starting;

        internal override void WriteFields(Utf8JsonWriter w)
        {
            w.WriteNumber("attempt", Attempt);
            w.WritePropertyName("lastError");
            if (LastError is null) w.WriteNullValue();
            else LastError.WriteTo(w);
        }
    }

    /// <summary>리스를 쥔 상태 — 기능 코드는 <see cref="Claims"/>(exp · sid)를 쓴다.</summary>
    public sealed record Active(SessionInfo Session, LicenseTokenInfo LicenseToken, LicenseClaims Claims) : SessionState
    {
        public override string Type => SessionStateType.Active;

        internal override void WriteFields(Utf8JsonWriter w) => WriteHeld(w, Session, LicenseToken, Claims);
    }

    /// <summary>하트비트가 실패하는 중 — <see cref="OfflineUntil"/>까지는 계속 쓸 수 있다.</summary>
    public sealed record Degraded(SessionInfo Session, LicenseTokenInfo LicenseToken, LicenseClaims Claims,
        int ConsecutiveFailures, string OfflineUntil, ErrorView LastError) : SessionState
    {
        public override string Type => SessionStateType.Degraded;

        internal override void WriteFields(Utf8JsonWriter w)
        {
            WriteHeld(w, Session, LicenseToken, Claims);
            w.WriteNumber("consecutiveFailures", ConsecutiveFailures);
            w.WriteString("offlineUntil", OfflineUntil);
            w.WritePropertyName("lastError");
            LastError.WriteTo(w);
        }
    }

    /// <summary>사용자 판단이 필요하다 — DEVICE_IN_USE · INSTANCE_ALREADY_RUNNING은 TakeOver, DEVICE_LIMIT_EXCEEDED는 기기 해제 뒤 Retry.</summary>
    public sealed record NeedsDecision(string Reason, ActiveSessionInfo? ActiveSession, long? MaxDevices, string? CorrelationId)
        : SessionState
    {
        public override string Type => SessionStateType.NeedsDecision;

        internal override void WriteFields(Utf8JsonWriter w)
        {
            w.WriteString("reason", Reason);
            w.WritePropertyName("activeSession");
            if (ActiveSession is null)
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteStartObject();
                w.WriteString("deviceId", ActiveSession.DeviceId);
                w.WriteString("deviceName", ActiveSession.DeviceName);
                w.WriteString("lastSeenAt", ActiveSession.LastSeenAt);
                w.WriteEndObject();
            }
            Json.Int(w, "maxDevices", MaxDevices);
            Json.Str(w, "correlationId", CorrelationId);
        }
    }

    /// <summary>잠금 — 오류 화면에 <see cref="CorrelationId"/>를 보인다. <see cref="Retrying"/>이면 스스로 풀릴 수 있다(OFFLINE_EXPIRED).</summary>
    public sealed record Locked(string Code, string Message, string? CorrelationId, JsonElement? Details, bool Retrying)
        : SessionState
    {
        public override string Type => SessionStateType.Locked;

        internal override void WriteFields(Utf8JsonWriter w)
        {
            w.WriteString("code", Code);
            w.WriteString("message", Message);
            Json.Str(w, "correlationId", CorrelationId);
            Json.Element(w, "details", Details);
            w.WriteBoolean("retrying", Retrying);
        }
    }

    public sealed record Stopped : SessionState
    {
        public override string Type => SessionStateType.Stopped;

        internal override void WriteFields(Utf8JsonWriter w)
        {
        }
    }

    private static void WriteHeld(Utf8JsonWriter w, SessionInfo session, LicenseTokenInfo token, LicenseClaims claims)
    {
        w.WritePropertyName("session");
        session.WriteTo(w);
        w.WritePropertyName("licenseToken");
        token.WriteTo(w);
        w.WritePropertyName("claims");
        claims.WriteTo(w);
    }
}

/// <summary>세션 획득 때 보내는 기기 정보다.</summary>
public sealed record DeviceInfo(string Name, string Platform);

/// <summary>
/// 실행 중인 앱이다(명세 1절 · ADR-0049) — 배포 대상(업데이트 확인의 target과 같은 값, <see cref="DistributionTarget"/>)과 콘솔에 업로드한 버전 문자열.
/// 세션 획득에 싣는다. 업데이트 제공 기한이 있는 영구 이용권은 이 값이 없으면 획득이 ENTITLEMENT_VERSION_NOT_COVERED로 거절된다.
/// </summary>
public sealed record AppInfo(string Target, string Version);

/// <summary>라이선스 세션(실행 리스, 명세 8절). 구독 함수는 SDK의 스레드에서 불린다 — UI 스레드로 넘긴다.</summary>
public sealed class LicenseSession
{
    private readonly Api _api;
    private readonly TokenManager _tokens;
    private readonly Verifier _verifier;
    private readonly DeviceInfo _device;
    private readonly AppInfo? _app;
    private readonly object _gate = new();
    private readonly List<Action<SessionState>> _listeners = new();
    private SessionState _current = new SessionState.Stopped();
    private string _currentJson = new SessionState.Stopped().ToJson();
    private int _run;
    private string? _instanceId;
    private SessionState.Active? _held;
    private int _failures;
    private IDisposable? _heartbeat;
    private IDisposable? _offline;
    private string? _fallback;

    internal LicenseSession(Api api, TokenManager tokens, Verifier verifier, DeviceInfo device, AppInfo? app)
    {
        _api = api;
        _tokens = tokens;
        _verifier = verifier;
        _device = device;
        _app = app;
    }

    private Resolved Ports => _api.Ports;

    /// <summary>지금 상태다.</summary>
    public SessionState State
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>상태가 바뀔 때마다 부른다. Dispose하면 끊는다.</summary>
    public IDisposable Subscribe(Action<SessionState> listener)
    {
        lock (_gate) _listeners.Add(listener);
        return new Disposer(() =>
        {
            lock (_gate) _listeners.Remove(listener);
        });
    }

    /// <summary>Stopped에서만 의미가 있다(8.2절).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_current is not SessionState.Stopped) return;
            _run++;
            _instanceId = Ports.Random.NewUuid();
        }
        BeginAcquire(false, null);
    }

    /// <summary>NeedsDecision(다른 기기 · 같은 기기 실행 중) 또는 Locked(SESSION_SUPERSEDED)에서 인수한다 — 새 Idempotency-Key.</summary>
    public void TakeOver()
    {
        var allowed = State switch
        {
            SessionState.NeedsDecision n => n.Reason != NeedsDecisionReason.DeviceLimitExceeded,
            SessionState.Locked l => l.Code == Codes.SessionSuperseded,
            _ => false,
        };
        if (!allowed) return;
        lock (_gate) _run++;
        BeginAcquire(true, null);
    }

    /// <summary>NeedsDecision(기기 수) 또는 스스로 풀리지 않는 Locked에서 다시 얻는다.</summary>
    public void Retry()
    {
        var allowed = State switch
        {
            SessionState.NeedsDecision n => n.Reason == NeedsDecisionReason.DeviceLimitExceeded,
            SessionState.Locked l => !l.Retrying,
            _ => false,
        };
        if (!allowed) return;
        lock (_gate) _run++;
        BeginAcquire(false, null);
    }

    /// <summary>예약을 모두 취소하고 반납을 한 번 보낸다(8.7절).</summary>
    public async Task StopAsync()
    {
        string? sid;
        lock (_gate)
        {
            if (_current is SessionState.Stopped) return;
            _run++;
            CancelTimers();
            sid = _held?.Session.Id;
            _held = null;
            _instanceId = null;
        }
        if (sid is not null)
        {
            try
            {
                var url = _api.Url(Routes.ReleaseSession.Path, new Dictionary<string, string> { ["sid"] = sid });
                await _tokens.AuthedAsync(bearer => _api.SendAsync(new Req("DELETE", url, Bearer: bearer))).ConfigureAwait(false);
            }
            catch (BydevException)
            {
                Ports.Logger.Debug("세션 반납을 보내지 못했다 — 리스가 저절로 빈다");
            }
        }
        SetState(new SessionState.Stopped());
    }

    /// <summary>8.5절 — 다른 인증 응답의 X-Lease-Expires-At.</summary>
    internal void NoteLease(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        SessionState next;
        lock (_gate)
        {
            if (_held is null) return;
            next = _current switch
            {
                SessionState.Active a when a.Session.LeaseExpiresAt != value => a with { Session = a.Session with { LeaseExpiresAt = value! } },
                SessionState.Degraded d when d.Session.LeaseExpiresAt != value => d with { Session = d.Session with { LeaseExpiresAt = value! } },
                _ => null!,
            };
            if (next is null) return;
            _held = _held with { Session = _held.Session with { LeaseExpiresAt = value! } };
        }
        SetState(next);
    }

    private void SetState(SessionState next)
    {
        var json = next.ToJson();
        Action<SessionState>[] listeners;
        lock (_gate)
        {
            if (json == _currentJson) return;
            _current = next;
            _currentJson = json;
            listeners = _listeners.ToArray();
        }
        foreach (var listener in listeners)
        {
            try
            {
                listener(next);
            }
            catch (Exception)
            {
                Ports.Logger.Warn("세션 상태 구독자가 던졌다");
            }
        }
    }

    private bool Live(int run)
    {
        lock (_gate) return _run == run;
    }

    // 잠근 채로만 부른다
    private void CancelTimers()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        _offline?.Dispose();
        _offline = null;
    }

    private void Arm(TimeSpan delay, Func<Task> task)
    {
        var timer = Ports.Scheduler.Schedule(delay, task);
        lock (_gate) _heartbeat = timer;
    }

    private void BeginAcquire(bool force, BydevException? lastError)
    {
        int run;
        lock (_gate)
        {
            CancelTimers();
            _held = null;
            _failures = 0;
            run = _run;
        }
        SetState(new SessionState.Starting(0, lastError is null ? null : ErrorView.Of(lastError)));
        var key = Ports.Random.NewUuid();
        Arm(TimeSpan.Zero, () => AcquireAsync(run, force, key, 0));
    }

    private async Task<string> FingerprintHashAsync()
    {
        string? source;
        try
        {
            source = await Ports.Fingerprint.ReadAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            source = null;
        }
        var value = source?.Trim() ?? "";
        if (value.Length == 0)
        {
            lock (_gate)
            {
                if (_fallback is null)
                {
                    _fallback = Ports.Random.NewUuid();
                    Ports.Logger.Warn("기기 지문 출처를 읽지 못해 이번 실행 동안만 쓰는 ID를 쓴다 — 출처 포트를 준다(명세 10절)");
                }
                value = _fallback;
            }
        }
        return Encoding64.Sha256Hex(value + Rules.FingerprintSalt);
    }

    /// <summary>8.2절 획득 — 일시적 실패의 재시도는 같은 키.</summary>
    private async Task AcquireAsync(int run, bool force, string key, int attempt)
    {
        if (!Live(run)) return;
        try
        {
            var fingerprint = await FingerprintHashAsync().ConfigureAwait(false);
            string? instance;
            lock (_gate) instance = _instanceId;
            var fields = new List<(string, object?)>
            {
                ("device", (Action<Utf8JsonWriter>)(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("fingerprintHash", fingerprint);
                    w.WriteString("name", _device.Name);
                    w.WriteString("platform", _device.Platform);
                    w.WriteEndObject();
                })),
                ("instanceId", instance),
                ("force", force),
            };
            // 설정에 있을 때만 싣는다 — 인수(force) · 재획득도 같다
            if (_app is { } app)
            {
                fields.Add(("app", (Action<Utf8JsonWriter>)(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("target", app.Target);
                    w.WriteString("version", app.Version);
                    w.WriteEndObject();
                })));
            }
            var body = Body.Of(fields.ToArray());
            var url = _api.Url(Routes.AcquireSession.Path);
            var res = await _tokens.AuthedAsync(bearer => _api.SendAsync(new Req("POST", url, body, bearer, key))).ConfigureAwait(false);
            if (!Live(run)) return;
            await AdoptAsync(run, res.Json).ConfigureAwait(false);
        }
        catch (BydevException e)
        {
            if (!Live(run)) return;
            if (e.IsTransient)
            {
                var delays = Rules.SessionStartRetryDelaysSec;
                var baseSec = delays[Math.Min(attempt, delays.Count - 1)];
                SetState(new SessionState.Starting(attempt + 1, ErrorView.Of(e)));
                var delay = Timing.Jittered(Ports, Math.Floor(baseSec * 1000 + 0.5), Rules.SessionHeartbeatJitterMin,
                    Rules.SessionHeartbeatJitterMax);
                Arm(delay, () => AcquireAsync(run, force, key, attempt + 1));
            }
            else if (Rules.NeedsDecisionReasons.Contains(e.Code))
            {
                ActiveSessionInfo? active = null;
                long? maxDevices = null;
                if (e.Details is { ValueKind: JsonValueKind.Object } d)
                {
                    if (d.TryGetProperty("activeSession", out var a) && a.ValueKind == JsonValueKind.Object)
                    {
                        string S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                        active = new ActiveSessionInfo(S("deviceId"), S("deviceName"), S("lastSeenAt"));
                    }
                    if (d.TryGetProperty("maxDevices", out var m) && m.ValueKind == JsonValueKind.Number) maxDevices = (long)m.GetDouble();
                }
                SetState(new SessionState.NeedsDecision(e.Code, active, maxDevices, e.CorrelationId));
            }
            else
            {
                Lock(e);
            }
        }
    }

    /// <summary>200 응답 → 검증(9절) → Active → 다음 하트비트.</summary>
    private async Task AdoptAsync(int run, JsonElement json)
    {
        var (session, token) = SessionResponse.Read(json);
        var claims = await _verifier.VerifyLicenseAsync(token.Token,
            new Verifier.Expectation(session.Id, session.DeviceId, session.Generation), 0).ConfigureAwait(false);
        if (!Live(run)) return;
        var active = new SessionState.Active(session, token, claims);
        lock (_gate)
        {
            _held = active;
            _failures = 0;
            _offline?.Dispose();
            _offline = null;
        }
        SetState(active);
        ScheduleHeartbeat(run, session.HeartbeatSec, null);
    }

    private void ScheduleHeartbeat(int run, long heartbeatSec, int? retryAfterSec)
    {
        var delay = Timing.Jittered(Ports, heartbeatSec * 1000.0, Rules.SessionHeartbeatJitterMin, Rules.SessionHeartbeatJitterMax);
        if (retryAfterSec is int r && TimeSpan.FromSeconds(r) > delay) delay = TimeSpan.FromSeconds(r);
        Arm(delay, () => HeartbeatAsync(run));
    }

    /// <summary>8.3절.</summary>
    private async Task HeartbeatAsync(int run)
    {
        SessionState.Active held;
        lock (_gate)
        {
            if (_run != run || _held is null) return;
            held = _held;
        }
        try
        {
            var url = _api.Url(Routes.HeartbeatSession.Path, new Dictionary<string, string> { ["sid"] = held.Session.Id });
            var body = Body.Of(("generation", held.Session.Generation));
            var res = await _tokens.AuthedAsync(bearer => _api.SendAsync(new Req("POST", url, body, bearer))).ConfigureAwait(false);
            if (!Live(run)) return;
            await AdoptAsync(run, res.Json).ConfigureAwait(false);
        }
        catch (BydevException e)
        {
            if (!Live(run)) return;
            if (e.IsTransient)
            {
                int failures;
                bool holding;
                lock (_gate)
                {
                    failures = ++_failures;
                    holding = _current is SessionState.Active or SessionState.Degraded;
                }
                if (holding)
                {
                    SetState(new SessionState.Degraded(held.Session, held.LicenseToken, held.Claims, failures,
                        held.LicenseToken.ExpiresAt, ErrorView.Of(e)));
                    ArmOfflineExpiry(run, held.LicenseToken.ExpiresAt);
                }
                ScheduleHeartbeat(run, held.Session.HeartbeatSec,
                    e.Code == Codes.RateLimited || e.HttpStatus == 429 ? e.RetryAfterSec : null);
            }
            else if (e.Code == Codes.SessionRevoked)
            {
                // 리스가 끝났을 때도 이 코드다 — 한 번 다시 얻는다(같은 instanceId, 새 키)
                BeginAcquire(false, e);
            }
            else
            {
                Lock(e);
            }
        }
    }

    /// <summary>8.4절 — offlineUntil이 지나도록 성공하지 못하면 잠근다. 재시도는 그대로.</summary>
    private void ArmOfflineExpiry(int run, string offlineUntil)
    {
        lock (_gate)
        {
            if (_offline is not null) return;
        }
        var until = Time.Parse(offlineUntil);
        var delay = until is long u ? TimeSpan.FromMilliseconds(Math.Max(0, u - Ports.Clock.NowMs())) : TimeSpan.Zero;
        var timer = Ports.Scheduler.Schedule(delay, () =>
        {
            bool ok;
            lock (_gate)
            {
                _offline = null;
                ok = _run == run && _current is SessionState.Degraded;
            }
            if (ok)
            {
                SetState(new SessionState.Locked(Codes.OfflineExpired, "하트비트에 성공하지 못한 채 licenseToken이 만료됐다", null,
                    null, true));
            }
            return Task.CompletedTask;
        });
        lock (_gate) _offline = timer;
    }

    private void Lock(BydevException e)
    {
        lock (_gate) CancelTimers();
        SetState(new SessionState.Locked(e.Code, e.Message, e.CorrelationId, e.Details, false));
    }
}
