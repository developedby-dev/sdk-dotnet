using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bydev;

// 명세 3 · 4절 — 요청 만들기 · 응답 읽기 · 발견 문서.

internal sealed record ApiResponse(int Status, IReadOnlyDictionary<string, string> Headers, JsonElement Json);

internal sealed record Req(string Method, string Url, string? Body = null, string? Bearer = null, string? IdempotencyKey = null);

/// <summary>한 번에 하나만 도는 작업 — 진행 중이면 같은 작업을 기다린다. 끝나면 비운 뒤에 기다리던 쪽을 깨운다(TS의 finally와 같은 순서).</summary>
internal sealed class SingleFlight<T>
{
    private readonly object _gate = new();
    private Task<T>? _current;

    public Task<T> Run(Func<Task<T>> start)
    {
        TaskCompletionSource<T> done;
        lock (_gate)
        {
            if (_current is not null) return _current;
            done = new TaskCompletionSource<T>();
            _current = done.Task;
        }
        RunInto(start, done);
        return done.Task;
    }

    private async void RunInto(Func<Task<T>> start, TaskCompletionSource<T> done)
    {
        try
        {
            var value = await start().ConfigureAwait(false);
            lock (_gate) _current = null;
            done.SetResult(value);
        }
        catch (Exception e)
        {
            lock (_gate) _current = null;
            done.SetException(e);
        }
    }
}

internal sealed class Api
{
    private readonly string _userAgent;
    private readonly SingleFlight<Discovery> _discovering = new();
    private Discovery? _doc;

    public Api(string baseUrl, Resolved ports, string userAgent)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Ports = ports;
        _userAgent = userAgent;
    }

    public string BaseUrl { get; }

    public Resolved Ports { get; }

    /// <summary>경로 <c>{이름}</c>을 채우고 질의를 붙인다(이름순 — 언어마다 같은 URL).</summary>
    public string Url(string path, IReadOnlyDictionary<string, string>? parameters = null,
        IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        var sb = new StringBuilder(BaseUrl);
        var rest = path;
        while (true)
        {
            var i = rest.IndexOf('{');
            if (i < 0)
            {
                sb.Append(rest);
                break;
            }
            var j = rest.IndexOf('}', i);
            sb.Append(rest, 0, i);
            var name = rest.Substring(i + 1, j - i - 1);
            sb.Append(Uri.EscapeDataString(parameters is not null && parameters.TryGetValue(name, out var v) ? v : ""));
            rest = rest.Substring(j + 1);
        }
        var pairs = (query ?? Enumerable.Empty<KeyValuePair<string, string>>()).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}").ToList();
        if (pairs.Count > 0) sb.Append('?').Append(string.Join("&", pairs));
        return sb.ToString();
    }

    /// <summary>처음 한 번 받아 클라이언트 수명 동안 둔다. 실패하면 두지 않는다(명세 4절).</summary>
    public Task<Discovery> DiscoveryAsync()
    {
        var doc = _doc;
        if (doc is not null) return Task.FromResult(doc);
        return _discovering.Run(async () =>
        {
            var res = await SendAsync(new Req("GET", BaseUrl + Routes.GetOpenIdConfiguration.Path)).ConfigureAwait(false);
            var found = Discovery.Read(res.Json);
            if (found.Issuer.TrimEnd('/') != BaseUrl)
                throw BydevException.Sdk(Codes.Configuration, $"발견 문서의 issuer({found.Issuer})가 baseUrl({BaseUrl})과 다르다");
            _doc = found;
            return found;
        });
    }

    public async Task<ApiResponse> SendAsync(Req req)
    {
        var headers = new Dictionary<string, string>
        {
            ["accept"] = "application/json",
            ["user-agent"] = _userAgent,
        };
        if (req.Body is not null) headers["content-type"] = "application/json";
        if (req.Bearer is not null) headers["authorization"] = "Bearer " + req.Bearer;
        if (req.IdempotencyKey is not null) headers["idempotency-key"] = req.IdempotencyKey;
        HttpResponse res;
        try
        {
            res = await Ports.Transport.SendAsync(new HttpRequest(req.Method, req.Url, headers, req.Body)).ConfigureAwait(false);
        }
        catch (BydevException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw BydevException.Sdk(Codes.NetworkError, "API에 연결하지 못했다", e);
        }
        res.Headers.TryGetValue("x-correlation-id", out var correlation);
        var parsed = Json.Parse(res.Body);
        if (res.Status is >= 200 and < 300)
        {
            if (parsed is not JsonElement json)
                throw new BydevException(Codes.ProtocolError, "응답 본문이 JSON이 아니다", res.Status, correlation);
            return new ApiResponse(res.Status, res.Headers, json);
        }
        throw ErrorFrom(res, parsed, correlation);
    }

    /// <summary>3.2절 — 실패 응답 → BydevException.</summary>
    private static BydevException ErrorFrom(HttpResponse res, JsonElement? parsed, string? correlation)
    {
        int? retry = res.Headers.TryGetValue("retry-after", out var h) && int.TryParse(h.Trim(), out var n) && n > 0 ? n : null;
        if (parsed is JsonElement { ValueKind: JsonValueKind.Object } e
            && e.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
            && e.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            if (e.TryGetProperty("retryAfterSec", out var r) && r.ValueKind == JsonValueKind.Number) retry = (int)r.GetDouble();
            JsonElement? details = e.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;
            return new BydevException(code.GetString()!, message.GetString()!, res.Status, correlation, retry, details);
        }
        var fallback = res.Status >= 500 ? Codes.InternalError : res.Status == 429 ? Codes.RateLimited : Codes.ProtocolError;
        return new BydevException(fallback, $"HTTP {res.Status}", res.Status, correlation, retry);
    }
}

internal static class Body
{
    public static string Of(params (string Key, object? Value)[] fields) => Json.Write(w =>
    {
        w.WriteStartObject();
        foreach (var (key, value) in fields)
        {
            switch (value)
            {
                case null: w.WriteNull(key); break;
                case string s: w.WriteString(key, s); break;
                case bool b: w.WriteBoolean(key, b); break;
                case long l: w.WriteNumber(key, l); break;
                case int i: w.WriteNumber(key, i); break;
                case Action<Utf8JsonWriter> nested:
                    w.WritePropertyName(key);
                    nested(w);
                    break;
                default: throw new ArgumentException(key);
            }
        }
        w.WriteEndObject();
    });
}
