using System.Collections.Generic;
using System.Text.Json;

namespace Bydev;

// 공개 API 응답의 SDK 모델 — JSON 이름은 contracts 그대로, 없을 수 있는 값은 null. 열거 값은 string(모르는 값도 — 아는 값은 생성된 상수 클래스).
// ToJson()은 contracts 모양 그대로 쓴다.

/// <summary>모델의 JSON.</summary>
public abstract record Model
{
    public string ToJson() => Json.Write(WriteTo);

    internal abstract void WriteTo(Utf8JsonWriter w);
}

/// <summary>기간이다. <see cref="Type"/>을 먼저 분기한다 — 영구(PERPETUAL)는 잔여 필드가 null이다.</summary>
public sealed record EntitlementTerm(string Type, string? ExpiresAt, long? RemainingDays, long? RemainingSeconds) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", Type);
        Json.Str(w, "expiresAt", ExpiresAt);
        Json.Int(w, "remainingDays", RemainingDays);
        Json.Int(w, "remainingSeconds", RemainingSeconds);
        w.WriteEndObject();
    }
}

/// <summary>구독형일 때만 있다.</summary>
public sealed record EntitlementSubscription(string Id, string Status, bool WillRenew, string? NextBillingAt, string? GraceEndsAt) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("status", Status);
        w.WriteBoolean("willRenew", WillRenew);
        Json.Str(w, "nextBillingAt", NextBillingAt);
        Json.Str(w, "graceEndsAt", GraceEndsAt);
        w.WriteEndObject();
    }
}

/// <summary>판매자가 정한 정책 수치다 — 앱에 하드코딩하지 않는다.</summary>
public sealed record LicensePolicy(long MaxDevices, long MaxConcurrent, long HeartbeatSec, long OfflineGraceHours) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("maxDevices", MaxDevices);
        w.WriteNumber("maxConcurrent", MaxConcurrent);
        w.WriteNumber("heartbeatSec", HeartbeatSec);
        w.WriteNumber("offlineGraceHours", OfflineGraceHours);
        w.WriteEndObject();
    }
}

/// <summary>
/// 업데이트 제공 범위다(ADR-0049). <see cref="Type"/>(<see cref="UpdateCoverageType"/>)을 먼저 분기한다 — ALL이면 모든 버전, UNTIL이면
/// <see cref="Until"/> 전에 출시된 버전을 실행할 수 있다. 모르는 Type도 문자열 그대로 담기고 그때 Until은 null이다.
/// </summary>
public sealed record UpdateCoverage(string Type, string? Until = null) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", Type);
        if (Type == UpdateCoverageType.Until) Json.Str(w, "until", Until);
        w.WriteEndObject();
    }

    internal static UpdateCoverage Read(Reader r, JsonElement json, string path)
    {
        var c = r.Object(json, path);
        var type = r.Str(c, "type", path + ".type");
        return type == UpdateCoverageType.Until
            ? new UpdateCoverage(type, r.Str(c, "until", path + ".until"))
            : new UpdateCoverage(type);
    }
}

/// <summary>이 제품의 이용권이다.</summary>
public sealed record Entitlement(string Id, string ProductId, string Status, EntitlementTerm Term,
    EntitlementSubscription? Subscription, LicensePolicy Policy, string EvaluatedAt) : Model
{
    /// <summary>
    /// 업데이트 제공 범위다(ADR-0049). 서버 응답에는 늘 있다. 기존 생성자 · 분해를 그대로 두려고 생성자 인수가 아닌 속성으로 둔다 — 직접 만든 값에서
    /// 주지 않으면 ALL(범위 칸이 없던 기존 구매를 읽는 값)이다.
    /// </summary>
    public UpdateCoverage UpdateCoverage { get; init; } = new(UpdateCoverageType.All);

    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("productId", ProductId);
        w.WriteString("status", Status);
        w.WritePropertyName("term");
        Term.WriteTo(w);
        w.WritePropertyName("updateCoverage");
        UpdateCoverage.WriteTo(w);
        w.WritePropertyName("subscription");
        if (Subscription is null) w.WriteNullValue();
        else Subscription.WriteTo(w);
        w.WritePropertyName("policy");
        Policy.WriteTo(w);
        w.WriteString("evaluatedAt", EvaluatedAt);
        w.WriteEndObject();
    }

    internal static Entitlement Read(JsonElement json)
    {
        var r = new Reader();
        var e = r.Object(Reader.Field(r.Object(json, "$"), "entitlement"), "entitlement");
        var t = r.Object(Reader.Field(e, "term"), "entitlement.term");
        var type = r.Str(t, "type", "entitlement.term.type");
        var term = type == "PERPETUAL"
            ? new EntitlementTerm(type, null, null, null)
            : new EntitlementTerm(type, r.OptStr(t, "expiresAt", "entitlement.term.expiresAt"),
                r.OptInt(t, "remainingDays", "entitlement.term.remainingDays"),
                r.OptInt(t, "remainingSeconds", "entitlement.term.remainingSeconds"));
        var coverage = UpdateCoverage.Read(r, Reader.Field(e, "updateCoverage"), "entitlement.updateCoverage");
        var p = r.Object(Reader.Field(e, "policy"), "entitlement.policy");
        var policy = new LicensePolicy(r.Int(p, "maxDevices", "entitlement.policy.maxDevices"),
            r.Int(p, "maxConcurrent", "entitlement.policy.maxConcurrent"),
            r.Int(p, "heartbeatSec", "entitlement.policy.heartbeatSec"),
            r.Int(p, "offlineGraceHours", "entitlement.policy.offlineGraceHours"));
        EntitlementSubscription? sub = null;
        if (!Reader.IsNull(e, "subscription"))
        {
            var s = r.Object(Reader.Field(e, "subscription"), "entitlement.subscription");
            sub = new EntitlementSubscription(r.Str(s, "id", "entitlement.subscription.id"),
                r.Str(s, "status", "entitlement.subscription.status"),
                r.Bool(s, "willRenew", "entitlement.subscription.willRenew"),
                r.OptStr(s, "nextBillingAt", "entitlement.subscription.nextBillingAt"),
                r.OptStr(s, "graceEndsAt", "entitlement.subscription.graceEndsAt"));
        }
        return r.Finish(new Entitlement(r.Str(e, "id", "entitlement.id"), r.Str(e, "productId", "entitlement.productId"),
            r.Str(e, "status", "entitlement.status"), term, sub, policy, r.Str(e, "evaluatedAt", "entitlement.evaluatedAt"))
        {
            UpdateCoverage = coverage,
        });
    }
}

/// <summary>등록된 기기다.</summary>
public sealed record Device(string Id, string Name, string Platform, string RegisteredAt, string LastSeenAt) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("name", Name);
        w.WriteString("platform", Platform);
        w.WriteString("registeredAt", RegisteredAt);
        w.WriteString("lastSeenAt", LastSeenAt);
        w.WriteEndObject();
    }
}

/// <summary>기기 목록 한 쪽이다. <see cref="NextCursor"/>가 null이면 마지막 쪽이다.</summary>
public sealed record DevicePage(IReadOnlyList<Device> Items, string? NextCursor) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("items");
        foreach (var d in Items) d.WriteTo(w);
        w.WriteEndArray();
        Json.Str(w, "nextCursor", NextCursor);
        w.WriteEndObject();
    }

    internal static DevicePage Read(JsonElement json)
    {
        var r = new Reader();
        var page = r.Object(json, "$");
        var items = new List<Device>();
        foreach (var v in r.Array(page, "items", "items"))
        {
            var d = r.Object(v, "items[]");
            items.Add(new Device(r.Str(d, "id", "items[].id"), r.Str(d, "name", "items[].name"),
                r.Str(d, "platform", "items[].platform"), r.Str(d, "registeredAt", "items[].registeredAt"),
                r.Str(d, "lastSeenAt", "items[].lastSeenAt")));
        }
        return r.Finish(new DevicePage(items, r.OptStr(page, "nextCursor", "nextCursor")));
    }
}

/// <summary>기기 해제 결과다.</summary>
public sealed record DeviceRelease(string Id, string Status, string ReleasedAt) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("status", Status);
        w.WriteString("releasedAt", ReleasedAt);
        w.WriteEndObject();
    }

    internal static DeviceRelease Read(JsonElement json)
    {
        var r = new Reader();
        var d = r.Object(Reader.Field(r.Object(json, "$"), "device"), "device");
        return r.Finish(new DeviceRelease(r.Str(d, "id", "device.id"), r.Str(d, "status", "device.status"),
            r.Str(d, "releasedAt", "device.releasedAt")));
    }
}

/// <summary>닉네임과 가명 식별자다. <see cref="PseudonymousId"/>를 판매자 쪽 고객 키로 쓴다.</summary>
public sealed record Profile(string PseudonymousId, string Nickname, string? Email) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("pseudonymousId", PseudonymousId);
        w.WriteString("nickname", Nickname);
        Json.Str(w, "email", Email);
        w.WriteEndObject();
    }

    internal static Profile Read(JsonElement json)
    {
        var r = new Reader();
        var p = r.Object(Reader.Field(r.Object(json, "$"), "profile"), "profile");
        return r.Finish(new Profile(r.Str(p, "pseudonymousId", "profile.pseudonymousId"),
            r.Str(p, "nickname", "profile.nickname"), r.OptStr(p, "email", "profile.email")));
    }
}

/// <summary>외부 채널의 설치 위치다.</summary>
public sealed record ExternalChannel(string Channel, string Locator);

/// <summary>설치 방법이다. <see cref="Mode"/>가 HOSTED면 내려받기 필드, EXTERNAL이면 <see cref="Channels"/>. 모르는 방식이면 Mode만 있다.</summary>
public sealed record ProductInstall(string Mode, string? BuildId = null, string? DownloadUrl = null,
    string? DownloadUrlExpiresAt = null, string? Sha256 = null, long? SizeBytes = null,
    IReadOnlyList<ExternalChannel>? Channels = null) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("mode", Mode);
        if (Mode == InstallMode.Hosted)
        {
            Json.Str(w, "buildId", BuildId);
            Json.Str(w, "downloadUrl", DownloadUrl);
            Json.Str(w, "downloadUrlExpiresAt", DownloadUrlExpiresAt);
            Json.Str(w, "sha256", Sha256);
            Json.Int(w, "sizeBytes", SizeBytes);
        }
        else if (Mode == InstallMode.External)
        {
            w.WriteStartArray("channels");
            foreach (var c in Channels ?? System.Array.Empty<ExternalChannel>())
            {
                w.WriteStartObject();
                w.WriteString("channel", c.Channel);
                w.WriteString("locator", c.Locator);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    internal static ProductInstall Read(Reader r, JsonElement json, string path)
    {
        var install = r.Object(json, path);
        var mode = r.Str(install, "mode", path + ".mode");
        if (mode == InstallMode.Hosted)
        {
            return new ProductInstall(mode, r.Str(install, "buildId", path + ".buildId"),
                r.Str(install, "downloadUrl", path + ".downloadUrl"),
                r.Str(install, "downloadUrlExpiresAt", path + ".downloadUrlExpiresAt"),
                r.Str(install, "sha256", path + ".sha256"), r.Int(install, "sizeBytes", path + ".sizeBytes"));
        }
        if (mode == InstallMode.External)
        {
            var channels = new List<ExternalChannel>();
            foreach (var v in r.Array(install, "channels", path + ".channels"))
            {
                var c = r.Object(v, path + ".channels[]");
                channels.Add(new ExternalChannel(r.Str(c, "channel", path + ".channels[].channel"),
                    r.Str(c, "locator", path + ".channels[].locator")));
            }
            return new ProductInstall(mode, Channels: channels);
        }
        return new ProductInstall(mode);
    }
}

/// <summary>
/// 최신 버전 · 업데이트 판정 · 설치 방법이다 — <see cref="UpdateCheck"/>의 최상위와 <see cref="EntitledUpdate.Release"/>가 같은 모양 · 같은 읽기
/// 규칙이다. <see cref="UpdateStatus"/>가 REQUIRED면 업데이트 전까지 실행을 막는다.
/// </summary>
public sealed record UpdateRelease(string LatestVersion, string LatestReleasedAt, string? LatestNotes, string UpdateStatus,
    ProductInstall Install) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        WriteFields(w, LatestVersion, LatestReleasedAt, LatestNotes, UpdateStatus, Install);
        w.WriteEndObject();
    }

    internal static void WriteFields(Utf8JsonWriter w, string latestVersion, string latestReleasedAt, string? latestNotes,
        string updateStatus, ProductInstall install)
    {
        w.WriteStartObject("latest");
        w.WriteString("version", latestVersion);
        w.WriteString("releasedAt", latestReleasedAt);
        Json.Str(w, "notes", latestNotes);
        w.WriteEndObject();
        w.WriteStartObject("update");
        w.WriteString("status", updateStatus);
        w.WriteEndObject();
        w.WritePropertyName("install");
        install.WriteTo(w);
    }

    /// <summary><paramref name="o"/>의 latest · update · install을 읽는다. <paramref name="prefix"/>는 에러 경로 앞부분이다(최상위는 빈 문자열).</summary>
    internal static UpdateRelease Read(Reader r, JsonElement o, string prefix)
    {
        var latest = r.Object(Reader.Field(o, "latest"), prefix + "latest");
        return new UpdateRelease(r.Str(latest, "version", prefix + "latest.version"),
            r.Str(latest, "releasedAt", prefix + "latest.releasedAt"), r.OptStr(latest, "notes", prefix + "latest.notes"),
            r.Str(r.Object(Reader.Field(o, "update"), prefix + "update"), "status", prefix + "update.status"),
            ProductInstall.Read(r, Reader.Field(o, "install"), prefix + "install"));
    }
}

/// <summary>
/// 업데이트 제공 기한이 있는 구매자에게만 오는 값이다(ADR-0049). 있으면 앱은 최상위 latest · update · install 대신 <see cref="Release"/>를 따른다 —
/// 최상위 최신은 그 구매자가 실행할 수 없는 버전일 수 있고, 범위 밖 버전을 설치하면 세션 획득이 ENTITLEMENT_VERSION_NOT_COVERED로 거절된다.
/// 이 대상에 범위 안 버전이 없으면 <see cref="Release"/>는 null이다.
/// </summary>
public sealed record EntitledUpdate(string CoveredUntil, UpdateRelease? Release) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("coveredUntil", CoveredUntil);
        w.WritePropertyName("release");
        if (Release is null) w.WriteNullValue();
        else Release.WriteTo(w);
        w.WriteEndObject();
    }
}

/// <summary>
/// 업데이트 확인 결과다. <see cref="Entitled"/>가 있으면 그것을 따른다. <see cref="UpdateStatus"/>가 REQUIRED면 업데이트 전까지 실행을 막는다. 받은 파일은
/// 설치 전에 VerifyDownload.
/// </summary>
public sealed record UpdateCheck(string ProductId, string Target, string LatestVersion, string LatestReleasedAt,
    string? LatestNotes, string UpdateStatus, ProductInstall Install) : Model
{
    /// <summary>
    /// 업데이트 제공 기한이 있는 구매자에게만 있다(ADR-0049) — 있으면 앱은 최상위 대신 <see cref="EntitledUpdate.Release"/>를 따른다. 기존 생성자 · 분해를
    /// 그대로 두려고 생성자 인수가 아닌 속성으로 둔다.
    /// </summary>
    public EntitledUpdate? Entitled { get; init; }

    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartObject("product");
        w.WriteString("id", ProductId);
        w.WriteEndObject();
        w.WriteString("target", Target);
        UpdateRelease.WriteFields(w, LatestVersion, LatestReleasedAt, LatestNotes, UpdateStatus, Install);
        if (Entitled is not null)
        {
            w.WritePropertyName("entitled");
            Entitled.WriteTo(w);
        }
        w.WriteEndObject();
    }

    internal static UpdateCheck Read(JsonElement json)
    {
        var r = new Reader();
        var o = r.Object(json, "$");
        var top = UpdateRelease.Read(r, o, "");
        EntitledUpdate? entitled = null;
        if (!Reader.IsNull(o, "entitled"))
        {
            var e = r.Object(Reader.Field(o, "entitled"), "entitled");
            entitled = new EntitledUpdate(r.Str(e, "coveredUntil", "entitled.coveredUntil"),
                Reader.IsNull(e, "release") ? null : UpdateRelease.Read(r, r.Object(Reader.Field(e, "release"), "entitled.release"),
                    "entitled.release."));
        }
        return r.Finish(new UpdateCheck(r.Str(r.Object(Reader.Field(o, "product"), "product"), "id", "product.id"),
            r.Str(o, "target", "target"), top.LatestVersion, top.LatestReleasedAt, top.LatestNotes, top.UpdateStatus, top.Install)
        {
            Entitled = entitled,
        });
    }
}

/// <summary>세션(실행 리스)이다.</summary>
public sealed record SessionInfo(string Id, string ProductId, string DeviceId, long Generation, string LeaseExpiresAt,
    long HeartbeatSec) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("productId", ProductId);
        w.WriteString("deviceId", DeviceId);
        w.WriteNumber("generation", Generation);
        w.WriteString("leaseExpiresAt", LeaseExpiresAt);
        w.WriteNumber("heartbeatSec", HeartbeatSec);
        w.WriteEndObject();
    }
}

/// <summary>세션 보유 증명 JWT와 그 만료(오프라인 유예의 끝)다.</summary>
public sealed record LicenseTokenInfo(string Token, string ExpiresAt) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("token", Token);
        w.WriteString("expiresAt", ExpiresAt);
        w.WriteEndObject();
    }
}

/// <summary>검증된 licenseToken 클레임이다(명세 9절).</summary>
public sealed record LicenseClaims(string Iss, string Sub, string Aud, string ProductId, string DeviceId, string Sid,
    long Generation, long Iat, long Exp) : Model
{
    /// <summary>
    /// 세션을 얻을 때 앱이 보낸 대상 · 버전(<see cref="LicenseClientOptions.App"/>)으로 서버가 확정한 빌드다(ADR-0049, JSON <c>build_id</c>). 확정하지
    /// 못했으면 null이다. 기존 생성자 · 분해를 그대로 두려고 생성자 인수가 아닌 속성으로 둔다.
    /// </summary>
    public string? BuildId { get; init; }

    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("iss", Iss);
        w.WriteString("sub", Sub);
        w.WriteString("aud", Aud);
        w.WriteString("product_id", ProductId);
        w.WriteString("device_id", DeviceId);
        w.WriteString("sid", Sid);
        w.WriteNumber("generation", Generation);
        w.WriteNumber("iat", Iat);
        w.WriteNumber("exp", Exp);
        if (BuildId is not null) w.WriteString("build_id", BuildId);
        w.WriteEndObject();
    }

    internal static LicenseClaims Read(JsonElement json)
    {
        var r = new Reader();
        var c = r.Object(json, "claims");
        return r.Finish(new LicenseClaims(r.Str(c, "iss", "claims.iss"), r.Str(c, "sub", "claims.sub"),
            r.Str(c, "aud", "claims.aud"), r.Str(c, "product_id", "claims.product_id"),
            r.Str(c, "device_id", "claims.device_id"), r.Str(c, "sid", "claims.sid"),
            r.Int(c, "generation", "claims.generation"), r.Int(c, "iat", "claims.iat"), r.Int(c, "exp", "claims.exp"))
        {
            BuildId = r.OptStr(c, "build_id", "claims.build_id"),
        });
    }
}

/// <summary>검증된 access_token 클레임이다(명세 12절).</summary>
public sealed record AccessClaims(string Iss, string Sub, string Aud, string ClientId, string Scope, long Iat, long Exp,
    string Jti) : Model
{
    internal override void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("iss", Iss);
        w.WriteString("sub", Sub);
        w.WriteString("aud", Aud);
        w.WriteString("client_id", ClientId);
        w.WriteString("scope", Scope);
        w.WriteNumber("iat", Iat);
        w.WriteNumber("exp", Exp);
        w.WriteString("jti", Jti);
        w.WriteEndObject();
    }

    internal static AccessClaims Read(JsonElement json)
    {
        var r = new Reader();
        var c = r.Object(json, "claims");
        return r.Finish(new AccessClaims(r.Str(c, "iss", "claims.iss"), r.Str(c, "sub", "claims.sub"),
            r.Str(c, "aud", "claims.aud"), r.Str(c, "client_id", "claims.client_id"), r.Str(c, "scope", "claims.scope"),
            r.Int(c, "iat", "claims.iat"), r.Int(c, "exp", "claims.exp"), r.Str(c, "jti", "claims.jti")));
    }
}

internal sealed record TokenSet(string AccessToken, long ExpiresIn, string RefreshToken, string Scope)
{
    public static TokenSet Read(JsonElement json)
    {
        var r = new Reader();
        var t = r.Object(json, "$");
        return r.Finish(new TokenSet(r.Str(t, "access_token", "access_token"), r.Int(t, "expires_in", "expires_in"),
            r.Str(t, "refresh_token", "refresh_token"), r.Str(t, "scope", "scope")));
    }
}

internal sealed record DeviceCode(string Code, string UserCode, string VerificationUri, string VerificationUriComplete,
    long ExpiresIn, long Interval)
{
    public static DeviceCode Read(JsonElement json)
    {
        var r = new Reader();
        var d = r.Object(json, "$");
        return r.Finish(new DeviceCode(r.Str(d, "device_code", "device_code"), r.Str(d, "user_code", "user_code"),
            r.Str(d, "verification_uri", "verification_uri"),
            r.Str(d, "verification_uri_complete", "verification_uri_complete"),
            r.Int(d, "expires_in", "expires_in"), r.Int(d, "interval", "interval")));
    }
}

internal sealed record Discovery(string Issuer, string JwksUri, string DeviceAuthorizationEndpoint, string TokenEndpoint,
    string RevocationEndpoint)
{
    public static Discovery Read(JsonElement json)
    {
        var r = new Reader();
        var d = r.Object(json, "$");
        return r.Finish(new Discovery(r.Str(d, "issuer", "issuer"), r.Str(d, "jwks_uri", "jwks_uri"),
            r.Str(d, "device_authorization_endpoint", "device_authorization_endpoint"),
            r.Str(d, "token_endpoint", "token_endpoint"), r.Str(d, "revocation_endpoint", "revocation_endpoint")));
    }
}

internal static class SessionResponse
{
    public static (SessionInfo Session, LicenseTokenInfo Token) Read(JsonElement json)
    {
        var r = new Reader();
        var o = r.Object(json, "$");
        var s = r.Object(Reader.Field(o, "session"), "session");
        var t = r.Object(Reader.Field(o, "licenseToken"), "licenseToken");
        return r.Finish((new SessionInfo(r.Str(s, "id", "session.id"), r.Str(s, "productId", "session.productId"),
                r.Str(s, "deviceId", "session.deviceId"), r.Int(s, "generation", "session.generation"),
                r.Str(s, "leaseExpiresAt", "session.leaseExpiresAt"), r.Int(s, "heartbeatSec", "session.heartbeatSec")),
            new LicenseTokenInfo(r.Str(t, "token", "licenseToken.token"), r.Str(t, "expiresAt", "licenseToken.expiresAt"))));
    }
}
