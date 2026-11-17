// 운영체제 포트 실제 확인 — Windows · Linux 기기에서 돌린다(launch-checklist A31 · ADR-0040 ⚠ 31 · 51). 사용자 보안 저장소를 건드리므로
// BYDEV_KEYCHAIN_CHECK=1일 때만 — `BYDEV_KEYCHAIN_CHECK=1 dotnet run --project tools/OsCheck`.
//
// 기본 저장소(DefaultCredentialStore — Windows DPAPI 파일 · Linux Secret Service)에 항목 하나(계정 bydev-keychain-check)를 넣고 · 고치고 · 읽고 ·
// 지운다. 보안 저장소 대신 0600 파일로 떨어지면(경고 로그) 실패다. 지문 출처(FingerprintSource — MachineGuid · machine-id)가 설치 ID 파일 없이
// 운영체제 값을 읽고 두 번 읽어도 같은지 본다.
using Bydev;

const string App = "bydev-os-check";
const string Account = "bydev-keychain-check";

if (Environment.GetEnvironmentVariable("BYDEV_KEYCHAIN_CHECK") != "1")
{
    Console.WriteLine("BYDEV_KEYCHAIN_CHECK=1이 아니라 건너뛴다 — 사용자 보안 저장소를 건드린다");
    return 0;
}
if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}는 대상이 아니다 — Windows · Linux에서 돌린다");
    return 0;
}

var failures = new List<string>();
var device = OsPorts.SystemDeviceInfo();
Console.WriteLine($"기기 {device.Name} · {device.Platform}");

var fingerprint = OsPorts.FingerprintSource();
var first = await fingerprint.ReadAsync();
var again = await fingerprint.ReadAsync();
if (string.IsNullOrEmpty(first)) failures.Add("지문 — 운영체제 값을 읽지 못했다(설치 ID 없이)");
else if (first != again) failures.Add("지문 — 두 번 읽은 값이 다르다");
else Console.WriteLine($"✓ 지문 출처 — 운영체제 값(길이 {first.Length})을 읽었고 두 번 같다");

var logger = new Warnings();
var store = OsPorts.DefaultCredentialStore(App, Account, logger);
var record = new CredentialRecord(1, "rt_node_test_00000000001", "entitlement.read", "2026-10-06T13:00:00Z");
var rotated = record with { RefreshToken = "rt_keychain_rotated_000001" };
try
{
    await store.ClearAsync();
    if (await store.LoadAsync() is not null) failures.Add("저장소 — 지운 뒤에도 항목이 있다");
    foreach (var want in new[] { record, rotated })
    {
        await store.SaveAsync(want);
        if (await store.LoadAsync() != want) failures.Add("저장소 — 넣은 것을 읽지 못했다");
    }
    await store.ClearAsync();
    if (await store.LoadAsync() is not null) failures.Add("저장소 — 지운 항목이 남았다");
    if (logger.Seen.Count > 0) failures.Add("저장소 — 보안 저장소가 아니라 파일로 떨어졌다: " + string.Join(" / ", logger.Seen));
    else if (!failures.Exists(f => f.StartsWith("저장소", StringComparison.Ordinal)))
        Console.WriteLine($"✓ 기본 저장소({(OperatingSystem.IsWindows() ? "DPAPI 파일" : "Secret Service")}) — 넣기 · 고치기 · 읽기 · 지우기");
}
catch (Exception e)
{
    failures.Add($"저장소 — {e.Message}{(e.InnerException is null ? "" : " — " + e.InnerException.Message)}");
}
finally
{
    try
    {
        await store.ClearAsync();
    }
    catch (IOException)
    {
    }
    var dir = OsPorts.AppDataDir(App);
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
}
foreach (var failure in failures) Console.WriteLine("✗ " + failure);
return failures.Count == 0 ? 0 : 1;

sealed class Warnings : IBydevLogger
{
    public List<string> Seen { get; } = new();

    public void Debug(string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message) => Seen.Add(message);
}
