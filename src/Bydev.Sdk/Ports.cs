using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bydev;

// 명세 2절 — 바꿔 끼울 수 있는 포트. SDK 안에서 시계 · 타이머 · 난수를 직접 읽지 않는다.

/// <summary>전송 포트가 보낼 요청이다. 헤더 이름은 소문자, <see cref="Body"/>가 null이면 본문이 없다.</summary>
public sealed record HttpRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>받은 응답이다. 헤더 이름은 소문자다.</summary>
public sealed record HttpResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>요청을 보낸다. 연결 실패 · 시간 초과는 <c>NETWORK_ERROR</c> <see cref="BydevException"/>로 던진다(다른 예외도 감싼다). 3xx를 따라가지 않는다.</summary>
public interface ITransport
{
    Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

/// <summary>보안 저장소에 두는 기록이다(명세 3.6). access_token은 두지 않는다.</summary>
public sealed record CredentialRecord(int Version, string RefreshToken, string Scope, string SavedAt);

/// <summary>로그인 기록을 둔다. 없으면 <see cref="LoadAsync"/>가 null. 실패는 SDK가 <c>STORAGE_FAILED</c>로 감싼다.</summary>
public interface ICredentialStore
{
    Task<CredentialRecord?> LoadAsync();
    Task SaveAsync(CredentialRecord record);
    Task ClearAsync();
}

/// <summary>지금 시각(Unix 밀리초).</summary>
public interface IClock
{
    long NowMs();
}

/// <summary>나중 작업. 돌려준 것을 Dispose하면 취소한다. 작업은 비동기다(명세 2절).</summary>
public interface IScheduler
{
    IDisposable Schedule(TimeSpan delay, Func<Task> task);
}

/// <summary><c>[0, 1)</c> 실수와 UUID v4.</summary>
public interface IRandom
{
    double NextDouble();
    string NewUuid();
}

/// <summary>운영체제의 기기 식별자다. 없으면 null — 원본은 SDK 밖으로 나가지 않는다(해시만, 명세 10절).</summary>
public interface IFingerprintSource
{
    Task<string?> ReadAsync();
}

/// <summary>토큰 · 지문 출처 값은 넘어오지 않는다.</summary>
public interface IBydevLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
}

/// <summary>클라이언트가 쓰는 포트 묶음이다. 비운 칸은 기본 구현이다.</summary>
public sealed class Ports
{
    public ITransport? Transport { get; init; }
    public ICredentialStore? Store { get; init; }
    public IClock? Clock { get; init; }
    public IScheduler? Scheduler { get; init; }
    public IRandom? Random { get; init; }
    public IFingerprintSource? Fingerprint { get; init; }
    public IBydevLogger? Logger { get; init; }

    internal Resolved Resolve() => new(
        Transport ?? new HttpClientTransport(),
        Store ?? new MemoryCredentialStore(),
        Clock ?? SystemClock.Instance,
        Scheduler ?? TimerScheduler.Instance,
        Random ?? CryptoRandom.Instance,
        Fingerprint ?? NoFingerprint.Instance,
        Logger ?? NullBydevLogger.Instance);
}

internal sealed record Resolved(ITransport Transport, ICredentialStore Store, IClock Clock, IScheduler Scheduler,
    IRandom Random, IFingerprintSource Fingerprint, IBydevLogger Logger);
