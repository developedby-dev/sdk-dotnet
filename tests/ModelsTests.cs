using System.Text.Json;

namespace Bydev.Tests;

/// <summary>명세 0절 5 · 7절 — 업데이트 제공 범위(ADR-0049)의 관대한 읽기. 적합성 시나리오가 다루지 않는 빠진 필드를 본다.</summary>
public class ModelsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string EntitlementJson(string coverage) =>
        """
        {"entitlement":{"id":"ent_1","productId":"prod_1","status":"ACTIVE",
         "term":{"type":"PERPETUAL","expiresAt":null,"remainingDays":null,"remainingSeconds":null},
        """ + coverage + """
         "subscription":null,
         "policy":{"maxDevices":3,"maxConcurrent":1,"heartbeatSec":150,"offlineGraceHours":0},
         "evaluatedAt":"2026-10-06T14:00:00Z"}}
        """;

    private static void ProtocolError(string path, Action read)
    {
        var e = Assert.Throws<BydevException>(read);
        Assert.Equal(Codes.ProtocolError, e.Code);
        Assert.Contains(path, e.Message);
    }

    [Fact]
    public void UpdateCoverageIsRequired() =>
        ProtocolError("entitlement.updateCoverage", () => Entitlement.Read(Parse(EntitlementJson(""))));

    [Fact]
    public void UntilNeedsUntil() =>
        ProtocolError("entitlement.updateCoverage.until",
            () => Entitlement.Read(Parse(EntitlementJson("""
                "updateCoverage":{"type":"UNTIL"},
                """))));

    /// <summary>직접 만든 이용권은 ALL이다 — 기존 생성자를 그대로 쓴다.</summary>
    [Fact]
    public void ConstructedEntitlementDefaultsToAll()
    {
        var e = new Entitlement("ent_1", "prod_1", EntitlementStatus.Active,
            new EntitlementTerm("PERPETUAL", null, null, null), null, new LicensePolicy(3, 1, 150, 0),
            "2026-10-06T14:00:00Z");
        Assert.Equal(UpdateCoverageType.All, e.UpdateCoverage.Type);
        Assert.Contains("\"updateCoverage\":{\"type\":\"ALL\"}", e.ToJson());
    }

    [Fact]
    public void EntitledReleaseUsesTopLevelRules() =>
        ProtocolError("entitled.release.install.sha256", () => UpdateCheck.Read(Parse("""
            {"product":{"id":"prod_1"},"target":"WINDOWS_X64",
             "latest":{"version":"2.0.0","releasedAt":"2027-12-01T06:00:00Z","notes":null},"update":{"status":"AVAILABLE"},
             "install":{"mode":"FUTURE_STORE"},
             "entitled":{"coveredUntil":"2027-11-12T03:00:00Z","release":{
               "latest":{"version":"1.4.2","releasedAt":"2027-10-02T06:00:00Z","notes":null},"update":{"status":"UP_TO_DATE"},
               "install":{"mode":"HOSTED","buildId":"build_1","downloadUrl":"https://dl.example.test/a","downloadUrlExpiresAt":"2026-10-06T15:00:00Z","sizeBytes":5}}}}
            """)));
}
