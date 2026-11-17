namespace Bydev.Tests;

/// <summary>명세 3.4 — 일시적 실패 분류.</summary>
public class ErrorsTests
{
    [Theory]
    [InlineData(Codes.RateLimited, 429, true)]
    [InlineData(Codes.InternalError, 503, true)]
    [InlineData(Codes.NetworkError, null, true)]
    // 429인 한도 초과 코드는 끝난 실패다
    [InlineData(Codes.TakeoverLimitExceeded, 429, false)]
    [InlineData(Codes.DeviceReleaseLimitExceeded, 429, false)]
    // 모르는 코드만 HTTP 상태로
    [InlineData("FUTURE_THROTTLE", 429, true)]
    [InlineData("FUTURE_OUTAGE", 502, true)]
    [InlineData("FUTURE_FORBIDDEN", 403, false)]
    [InlineData("FUTURE_NOTHING", null, false)]
    [InlineData(Codes.StorageFailed, null, false)]
    public void IsTransient(string code, int? status, bool want) =>
        Assert.Equal(want, new BydevException(code, "시험", status).IsTransient);

    /// <summary>키는 언제나 다 있다 — 없는 값은 null.</summary>
    [Fact]
    public void ToJsonCarriesFields()
    {
        var e = new BydevException(Codes.RateLimited, "잠시 뒤에", 429, "req_err0001", 30);
        Assert.Equal("""{"code":"RATE_LIMITED","message":"잠시 뒤에","httpStatus":429,"correlationId":"req_err0001","retryAfterSec":30,"details":null}""",
            e.ToJson());
    }
}
