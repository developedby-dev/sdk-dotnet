using System.Runtime.InteropServices;

namespace Bydev.Tests;

/// <summary>명령 · 파일을 기록하는 가짜 — 운영체제 명령이 없는 곳에서 명령 모양과 해석을 본다.</summary>
internal sealed class FakeOs(string name) : IOs
{
    public readonly List<(string Program, string[] Args, string? Stdin)> Calls = new();
    public readonly Dictionary<string, string> Files = new();

    /// <summary>"명령 첫인자" 또는 "명령" → 출력(string) 또는 던질 예외.</summary>
    public Dictionary<string, object> Answers = new();

    public string Name => name;

    public Task<string> ExecAsync(string program, string[] args, string? stdin = null)
    {
        Calls.Add((program, args, stdin));
        if (!Answers.TryGetValue($"{program} {args[0]}", out var answer)) Answers.TryGetValue(program, out answer);
        return answer switch
        {
            Exception e => Task.FromException<string>(e),
            string s => Task.FromResult(s),
            _ => Task.FromResult(""),
        };
    }

    public string? ReadFile(string path) => Files.TryGetValue(path, out var v) ? v : null;

    public void WriteFile(string path, string content) => Files[path] = content;

    public void Remove(string path) => Files.Remove(path);

    public string NewId() => "00000000-0000-4000-8000-00000000abcd";
}

internal sealed class RecordingLogger : IBydevLogger
{
    public readonly List<string> Warns = new();

    public void Debug(string message) { }

    public void Info(string message) { }

    public void Warn(string message) => Warns.Add(message);
}

public class PlatformTests
{
    private static readonly CredentialRecord Record = new(1, "rt_node_test_00000000001", "entitlement.read", "2026-10-06T13:00:00Z");
    private const string RecordJson =
        """{"version":1,"refreshToken":"rt_node_test_00000000001","scope":"entitlement.read","savedAt":"2026-10-06T13:00:00Z"}""";

    [Fact]
    public async Task FingerprintPerOs()
    {
        var win = new FakeOs("windows")
        {
            Answers = { ["reg"] = "\r\nHKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Cryptography\r\n    MachineGuid    REG_SZ    3f1e9a2b-1111-2222-3333-444455556666\r\n" },
        };
        Assert.Equal("3f1e9a2b-1111-2222-3333-444455556666", await new OsFingerprint(null, win).ReadAsync());
        var mac = new FakeOs("macos") { Answers = { ["ioreg"] = "  \"IOPlatformUUID\" = \"A1B2C3D4-0000-1111-2222-333344445555\"" } };
        Assert.Equal("A1B2C3D4-0000-1111-2222-333344445555", await new OsFingerprint(null, mac).ReadAsync());
        var linux = new FakeOs("linux") { Files = { ["/var/lib/dbus/machine-id"] = "0123456789abcdef0123456789abcdef\n" } };
        Assert.Equal("0123456789abcdef0123456789abcdef", await new OsFingerprint(null, linux).ReadAsync());
    }

    [Fact]
    public async Task FingerprintInstallIdFallback()
    {
        var os = new FakeOs("macos") { Answers = { ["ioreg"] = new IOException("권한 없음") } };
        var source = new OsFingerprint("/app/install-id", os);
        var first = await source.ReadAsync();
        Assert.Equal("00000000-0000-4000-8000-00000000abcd", first);
        Assert.Equal(first, os.Files["/app/install-id"]);
        os.Files["/app/install-id"] = "kept-install-id\n";
        Assert.Equal("kept-install-id", await source.ReadAsync());
        // 파일 자리도 없으면 null(SDK가 수명 동안의 난수 ID로 물러난다)
        Assert.Null(await new OsFingerprint(null, os).ReadAsync());
    }

    [Fact]
    public async Task SecretToolStore()
    {
        var os = new FakeOs("linux") { Answers = { ["secret-tool lookup"] = new ExitCodeException("secret-tool", 1) } };
        var store = new SecretToolStore("cli_test", os);
        Assert.Null(await store.LoadAsync()); // 항목 없음
        await store.SaveAsync(Record);
        var save = os.Calls[1];
        Assert.Equal($"store --label=bydev cli_test service {Rules.StorageService} account cli_test", string.Join(" ", save.Args));
        // 비밀은 인자에 없고 표준 입력으로만
        Assert.DoesNotContain(save.Args, a => a.Contains(Record.RefreshToken));
        Assert.Contains(Record.RefreshToken, save.Stdin);
        os.Answers = new() { ["secret-tool lookup"] = RecordJson };
        Assert.Equal(Record, await store.LoadAsync());
        // 다른 실패는 던진다 — SDK가 STORAGE_FAILED로 감싼다
        os.Answers = new() { ["secret-tool lookup"] = new ExitCodeException("secret-tool", 2) };
        await Assert.ThrowsAsync<ExitCodeException>(() => store.LoadAsync());
        // 없는 항목 지우기는 성공 — 오류 출력이 있으면(D-Bus 없음 등) 읽기 · 지우기 모두 던진다
        os.Answers = new() { ["secret-tool clear"] = new ExitCodeException("secret-tool", 1) };
        await store.ClearAsync();
        var broken = new ExitCodeException("secret-tool", 1, "secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY\n");
        os.Answers = new() { ["secret-tool clear"] = broken, ["secret-tool lookup"] = broken };
        await Assert.ThrowsAsync<ExitCodeException>(() => store.ClearAsync());
        await Assert.ThrowsAsync<ExitCodeException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task DpapiStoreFile()
    {
        var os = new FakeOs("windows");
        // 거꾸로 뒤집기로 DPAPI를 흉내 낸다 — 파일에는 봉한 바이트의 base64만
        var store = new DpapiStore(@"C:\app\bydev-credentials", os, b => b.Reverse().ToArray(), b => b.Reverse().ToArray());
        await store.SaveAsync(Record);
        var sealedText = os.Files[@"C:\app\bydev-credentials"];
        Assert.DoesNotContain(Record.RefreshToken, sealedText);
        Assert.Equal(RecordJson, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sealedText).Reverse().ToArray()));
        Assert.Equal(Record, await store.LoadAsync());
        await store.ClearAsync();
        Assert.Null(await store.LoadAsync());
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new DpapiStore("x", os).SaveAsync(Record));
    }

    [Fact]
    public async Task FileStoreRealDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bydev-dotnet-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "sub", "bydev-credentials");
            var logger = new RecordingLogger();
            var store = OsPorts.FileCredentialStore(path, logger);
            Assert.Single(logger.Warns); // 암호화하지 않는다는 warn
            Assert.Null(await store.LoadAsync());
            await store.SaveAsync(Record);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(Record, await store.LoadAsync());
            // 판이 다른 기록은 없는 것으로 읽는다
            File.WriteAllText(path, """{"version":99,"refreshToken":"x"}""");
            Assert.Null(await store.LoadAsync());
            await store.ClearAsync();
            await store.ClearAsync(); // 없는 파일 지우기는 실패가 아니다
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "sub"))); // 임시 파일이 남지 않는다
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DefaultStoreChoice()
    {
        Assert.IsType<DpapiStore>(OsPorts.Default("App", "cli", null, new FakeOs("windows"), false));
        var logger = new RecordingLogger();
        Assert.IsType<FileStore>(OsPorts.Default("App", "cli", logger, new FakeOs("linux"), false)); // secret-tool이 없으면
        Assert.Single(logger.Warns);
        Assert.IsType<SecretToolStore>(OsPorts.Default("App", "cli", null, new FakeOs("linux"), true));
        Assert.IsType<FileStore>(OsPorts.Default("App", "cli", null, new FakeOs("macos"), false));
    }

    [Fact]
    public void AppDataDirPerOs()
    {
        Assert.EndsWith(Path.Combine("Library", "Application Support", "App"), OsPorts.AppDataDir("App", "macos"));
        Assert.EndsWith("App", OsPorts.AppDataDir("App", "linux"));
        var device = OsPorts.SystemDeviceInfo();
        Assert.InRange(device.Name.Length, 1, 64);
    }

    /// <summary>이 기기의 실제 지문 출처를 한 번 읽는다(값은 출력하지 않는다).</summary>
    [Fact]
    public async Task FingerprintThisMachine()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        Assert.False(string.IsNullOrEmpty(await OsPorts.FingerprintSource().ReadAsync()), "이 기기의 지문 출처를 읽지 못했다");
    }
}
