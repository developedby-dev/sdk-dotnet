using System;
using System.Linq;
using System.Text.Json;

namespace Bydev;

/// <summary>
/// SDK의 에러 하나다(명세 3.3). <see cref="Code"/>로 분기하고 <see cref="Exception.Message"/>는 보여 주기만 한다. 앱의 오류 화면에
/// <see cref="CorrelationId"/>를 보이면 문의를 바로 찾는다.
/// </summary>
public sealed class BydevException : Exception
{
    public BydevException(string code, string message, int? httpStatus = null, string? correlationId = null,
        int? retryAfterSec = null, JsonElement? details = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        HttpStatus = httpStatus;
        CorrelationId = correlationId;
        RetryAfterSec = retryAfterSec;
        Details = details;
    }

    /// <summary><see cref="Codes"/>의 값.</summary>
    public string Code { get; }

    public int? HttpStatus { get; }

    public string? CorrelationId { get; }

    public int? RetryAfterSec { get; }

    /// <summary>에러 상세(객체) — 없으면 null.</summary>
    public JsonElement? Details { get; }

    /// <summary>일시적 실패인지다(명세 3.4). 아는 코드는 그 뜻대로 — 429인 한도 초과 코드는 끝난 실패 — 모르는 코드만 HTTP 상태로 나눈다.</summary>
    public bool IsTransient
    {
        get
        {
            if (Rules.TransientCodes.Contains(Code)) return true;
            if (Codes.ApiHttpStatus.ContainsKey(Code) || Codes.Sdk.Contains(Code)) return false;
            if (HttpStatus is not int status) return false;
            return Rules.TransientUnknownCodeStatuses.Contains(status)
                || Rules.TransientUnknownCodeStatusRanges.Any(r => status >= r.Min && status <= r.Max);
        }
    }

    /// <summary><c>{"code","message","httpStatus","correlationId","retryAfterSec","details"}</c>.</summary>
    public string ToJson() => Json.Write(w => WriteTo(w));

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

    internal static BydevException Sdk(string code, string message, Exception? inner = null) =>
        new(code, message, inner: inner);
}
