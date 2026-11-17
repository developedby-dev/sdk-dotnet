using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bydev;

// 명세 2절 — 기본 구현. 운영체제별 저장소 · 지문 출처는 앱이 고른다(OsPorts).

/// <summary>시스템 시계 — SDK에서 시계를 직접 읽는 유일한 곳.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary><see cref="Timer"/> 기반 스케줄러.</summary>
public sealed class TimerScheduler : IScheduler
{
    public static readonly TimerScheduler Instance = new();

    public IDisposable Schedule(TimeSpan delay, Func<Task> task)
    {
        var cancelled = 0;
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            if (Interlocked.Exchange(ref cancelled, 1) == 0) _ = task();
        }, null, Timeout.Infinite, Timeout.Infinite);
        timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        return new Disposer(() =>
        {
            Interlocked.Exchange(ref cancelled, 1);
            timer.Dispose();
        });
    }
}

internal sealed class Disposer : IDisposable
{
    private Action? _dispose;

    public Disposer(Action dispose) => _dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

/// <summary>운영체제 난수.</summary>
public sealed class CryptoRandom : IRandom
{
    public static readonly CryptoRandom Instance = new();

    public double NextDouble()
    {
        var b = new byte[8];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        return (BitConverter.ToUInt64(b, 0) >> 11) / (double)(1UL << 53);
    }

    public string NewUuid() => Guid.NewGuid().ToString();
}

/// <summary><see cref="HttpClient"/> 기반 전송. 시간 초과는 <c>Rules.HttpTimeoutSec</c>, 리다이렉트를 따라가지 않는다.</summary>
public sealed class HttpClientTransport : ITransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _owned;

    public HttpClientTransport(TimeSpan? timeout = null)
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(Rules.HttpTimeoutSec),
        };
        _owned = true;
    }

    /// <summary>앱이 만든 HttpClient(프록시 · 인증서 고정)를 쓴다 — 리다이렉트를 끄는 것은 앱의 몫이다.</summary>
    public HttpClientTransport(HttpClient http) => _http = http;

    public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        string? contentType = null;
        foreach (var h in request.Headers)
        {
            if (string.Equals(h.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                contentType = h.Value;
            else
                message.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
        }
        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw BydevException.Sdk(Codes.NetworkError, "API에 연결하지 못했다", e);
        }
        using (response)
        {
            var headers = new Dictionary<string, string>();
            foreach (var h in response.Headers.Concat(response.Content.Headers))
                headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            return new HttpResponse((int)response.StatusCode, headers, body);
        }
    }

    public void Dispose()
    {
        if (_owned) _http.Dispose();
    }
}

/// <summary>메모리 저장소 — 앱을 끄면 로그인이 사라진다. 시험 · 짧은 CLI용.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private CredentialRecord? _record;

    public MemoryCredentialStore(CredentialRecord? initial = null) => _record = initial;

    public Task<CredentialRecord?> LoadAsync() => Task.FromResult(Volatile.Read(ref _record));

    public Task SaveAsync(CredentialRecord record)
    {
        Volatile.Write(ref _record, record);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        Volatile.Write(ref _record, null);
        return Task.CompletedTask;
    }
}

internal sealed class NoFingerprint : IFingerprintSource
{
    public static readonly NoFingerprint Instance = new();

    public Task<string?> ReadAsync() => Task.FromResult<string?>(null);
}

/// <summary>아무것도 하지 않는 로거.</summary>
public sealed class NullBydevLogger : IBydevLogger
{
    public static readonly NullBydevLogger Instance = new();

    public void Debug(string message) { }

    public void Info(string message) { }

    public void Warn(string message) { }
}
