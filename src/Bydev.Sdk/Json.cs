using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bydev;

/// <summary>JSON 쓰기 도우미 — 리플렉션 직렬화를 쓰지 않는다(AOT · 트리밍).</summary>
internal static class Json
{
    /// <summary>한글 등을 \uXXXX로 바꾸지 않는다 — 다른 SDK와 같은 모양(HTML에 그대로 넣는 문자열이 아니다).</summary>
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, Options)) write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());
    }

    public static void Str(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    public static void Int(Utf8JsonWriter w, string name, long? value)
    {
        if (value is long v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    public static void Element(Utf8JsonWriter w, string name, JsonElement? value)
    {
        w.WritePropertyName(name);
        if (value is JsonElement e) e.WriteTo(w);
        else w.WriteNullValue();
    }

    public static JsonElement? Parse(string text)
    {
        if (text.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>관대한 읽기(명세 0절 5) — 필요한 필드는 확인하고, 모르는 필드는 버리고, 모르는 열거 값은 문자열 그대로. 어긋나면 PROTOCOL_ERROR.</summary>
internal sealed class Reader
{
    private BydevException? _error;

    private void Fail(string path, string want, JsonElement got) =>
        _error ??= BydevException.Sdk(Codes.ProtocolError,
            got.ValueKind == JsonValueKind.Undefined ? $"응답에 {path}가 없다" : $"응답의 {path}가 {want}가 아니다 ({got.GetRawText()})");

    public JsonElement Object(JsonElement v, string path)
    {
        if (v.ValueKind == JsonValueKind.Object) return v;
        Fail(path, "객체", v);
        return default;
    }

    public static JsonElement Field(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? v : default;

    public string Str(JsonElement obj, string key, string path)
    {
        var v = Field(obj, key);
        if (v.ValueKind == JsonValueKind.String) return v.GetString()!;
        Fail(path, "문자열", v);
        return "";
    }

    public long Int(JsonElement obj, string key, string path)
    {
        var v = Field(obj, key);
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var i)) return i;
            if (v.TryGetDouble(out var d) && Math.Floor(d) == d && Math.Abs(d) < 9e15) return (long)d;
        }
        Fail(path, "정수", v);
        return 0;
    }

    public bool Bool(JsonElement obj, string key, string path)
    {
        var v = Field(obj, key);
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        Fail(path, "참/거짓", v);
        return false;
    }

    public JsonElement.ArrayEnumerator Array(JsonElement obj, string key, string path)
    {
        var v = Field(obj, key);
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray();
        Fail(path, "배열", v);
        return default;
    }

    public static bool IsNull(JsonElement obj, string key) =>
        Field(obj, key).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    public string? OptStr(JsonElement obj, string key, string path) => IsNull(obj, key) ? null : Str(obj, key, path);

    public long? OptInt(JsonElement obj, string key, string path) => IsNull(obj, key) ? null : Int(obj, key, path);

    public T Finish<T>(T value) => _error is null ? value : throw _error;
}

internal static class Encoding64
{
    public static byte[]? FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string Sha256Hex(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }
}

internal static class Time
{
    /// <summary>SDK가 만드는 시각 — 밀리초가 0이면 떼는 UTC ISO 8601.</summary>
    public static string Iso(long ms)
    {
        var s = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return s.EndsWith(".000Z", StringComparison.Ordinal) ? s.Substring(0, s.Length - 5) + "Z" : s;
    }

    public static long? Parse(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUnixTimeMilliseconds()
            : null;
}

internal static class Timing
{
    /// <summary><c>floor(base × (min + (max − min) × r) + 0.5)</c> 밀리초(명세 2절).</summary>
    public static TimeSpan Jittered(Resolved ports, double baseMs, double min, double max) =>
        TimeSpan.FromMilliseconds(Math.Floor(baseMs * (min + (max - min) * ports.Random.NextDouble()) + 0.5));

    public static TimeSpan Seconds(double sec) => TimeSpan.FromMilliseconds(Math.Floor(sec * 1000 + 0.5));

    /// <summary>스케줄러로 기다린다 — SDK는 스스로 타이머를 만들지 않는다(명세 0절 3).</summary>
    public static Task Delay(Resolved ports, TimeSpan delay)
    {
        // 이어지는 코드가 완료하는 쪽에서 곧바로 돈다 — 적합성 하네스의 정착 판정이 이것에 기댄다
        var done = new TaskCompletionSource<bool>();
        ports.Scheduler.Schedule(delay, () =>
        {
            done.TrySetResult(true);
            return Task.CompletedTask;
        });
        return done.Task;
    }
}
