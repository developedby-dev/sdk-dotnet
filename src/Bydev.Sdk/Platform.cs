using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bydev;

/// <summary>명령 실행 · 파일 — 단위 시험이 바꿔 끼운다.</summary>
internal interface IOs
{
    string Name { get; }
    Task<string> ExecAsync(string program, string[] args, string? stdin = null);
    string? ReadFile(string path);
    void WriteFile(string path, string content);
    void Remove(string path);
    string NewId();
}

/// <summary>실행 중인 운영체제.</summary>
internal sealed class RealOs : IOs
{
    public static readonly RealOs Instance = new();

    public string Name =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : "other";

    public async Task<string> ExecAsync(string program, string[] args, string? stdin = null)
    {
        var info = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException(program + "를 띄우지 못했다");
        // 오류 출력도 받아 실어 보낸다 — secret-tool은 "항목 없음"과 오류를 같은 종료 코드(1)로 내고 오류 출력으로만 가른다. 함께 읽는다
        // (한쪽 파이프가 차서 멈추지 않게)
        var errors = process.StandardError.ReadToEndAsync();
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await errors.ConfigureAwait(false);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new ExitCodeException(program, process.ExitCode, stderr);
        return output;
    }

    public string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>사용자만 읽는 권한(0600, Unix) · 임시 파일 뒤 이름 바꾸기(반쯤 쓴 파일이 남지 않게).</summary>
    public void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, content);
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Remove(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public string NewId() => Guid.NewGuid().ToString();
}

internal sealed class ExitCodeException : IOException
{
    public ExitCodeException(string program, int code, string stderr = "") : base($"{program}가 {code}로 끝났다")
    {
        Code = code;
        Stderr = stderr;
    }

    public int Code { get; }

    /// <summary>그 명령의 오류 출력.</summary>
    public string Stderr { get; }

    /// <summary>
    /// secret-tool이 항목을 찾지 못했다 — 오류 출력 없이 1로 끝난다. 오류(D-Bus 없음 · 잠긴 모음 · 없는 모음)도 1이지만 <c>secret-tool: …</c>를 쓴다
    /// (2026-10-17 Debian 12 libsecret 0.20에서 확인 — ADR-0040 Linux 실제 확인 기록).
    /// </summary>
    public bool SecretToolFoundNothing => Code == 1 && string.IsNullOrWhiteSpace(Stderr);
}

/// <summary>
/// 명세 2절 표 — 운영체제별 기본 구현. 원본 지문 · 토큰을 명령 줄 인자로 넘기지 않는다(비밀은 표준 입력으로만). 클라이언트의 기본 저장소는 메모리다 — 앱이 이
/// 중에서 고른다(<see cref="DefaultCredentialStore"/>).
/// </summary>
public static class OsPorts
{
    /// <summary>운영체제 · 앱 이름별 데이터 폴더다.</summary>
    public static string AppDataDir(string appName) => AppDataDir(appName, RealOs.Instance.Name);

    internal static string AppDataDir(string appName, string os)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return os switch
        {
            "windows" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName),
            "macos" => Path.Combine(home, "Library", "Application Support", appName),
            _ => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config"), appName),
        };
    }

    /// <summary>LicenseClientOptions.Device의 기본값 — 컴퓨터 이름(64자까지)과 운영체제.</summary>
    public static DeviceInfo SystemDeviceInfo()
    {
        var platform = RealOs.Instance.Name switch
        {
            "windows" => DevicePlatform.Windows,
            "macos" => DevicePlatform.Macos,
            "linux" => DevicePlatform.Linux,
            _ => DevicePlatform.Other,
        };
        var name = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(name)) name = "PC";
        return new DeviceInfo(name.Length > 64 ? name.Substring(0, 64) : name, platform);
    }

    /// <summary>
    /// 명세 10절 · 2절 표의 지문 출처 — Windows MachineGuid · macOS IOPlatformUUID · Linux /etc/machine-id. 읽지 못하면 installIdFile의 설치 ID(없으면 만든다).
    /// installIdFile이 null이면 null(SDK가 클라이언트 수명 동안의 난수 ID로 물러난다).
    /// </summary>
    public static IFingerprintSource FingerprintSource(string? installIdFile = null) => new OsFingerprint(installIdFile, RealOs.Instance);

    /// <summary>사용자 전용 권한(0600) 파일 — <b>암호화하지 않는다.</b> 운영체제 보안 저장소를 쓸 수 없을 때만(warn 로그).</summary>
    public static ICredentialStore FileCredentialStore(string path, IBydevLogger? logger = null) => new FileStore(path, logger, RealOs.Instance);

    /// <summary>Windows DPAPI(사용자 범위)로 암호화한 파일 — 다른 Windows 사용자는 풀 수 없다.</summary>
    public static ICredentialStore DpapiCredentialStore(string path) => new DpapiStore(path, RealOs.Instance);

    /// <summary>Linux Secret Service(libsecret secret-tool) — 비밀은 표준 입력으로 넘긴다. account는 clientId.</summary>
    public static ICredentialStore SecretToolCredentialStore(string account) => new SecretToolStore(account, RealOs.Instance);

    /// <summary>
    /// 운영체제에 맞는 기본 저장소 — Windows DPAPI 파일 · Linux Secret Service(secret-tool이 없으면 0600 파일 + warn) · macOS는 0600 파일 + warn(TS · Go와 같은
    /// 이유 — ADR-0040 ⚠ 10). account는 clientId.
    /// </summary>
    public static ICredentialStore DefaultCredentialStore(string appName, string account, IBydevLogger? logger = null) =>
        Default(appName, account, logger, RealOs.Instance, HasSecretTool());

    internal static ICredentialStore Default(string appName, string account, IBydevLogger? logger, IOs os, bool secretTool)
    {
        var file = Path.Combine(AppDataDir(appName, os.Name), "bydev-credentials");
        return os.Name switch
        {
            "windows" => new DpapiStore(file, os),
            "linux" when secretTool => new SecretToolStore(account, os),
            _ => new FileStore(file, logger, os),
        };
    }

    private static bool HasSecretTool()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "secret-tool"))) return true;
        return false;
    }

    internal static CredentialRecord? ParseRecord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || Json.Parse(text!) is not { ValueKind: JsonValueKind.Object } o) return null;
        string? S(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (!o.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out var version)
            || version != Rules.StorageRecordVersion)
            return null;
        var token = S("refreshToken");
        return string.IsNullOrEmpty(token) ? null : new CredentialRecord(1, token!, S("scope") ?? "", S("savedAt") ?? "");
    }

    internal static string WriteRecord(CredentialRecord r) => Json.Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("version", r.Version);
        w.WriteString("refreshToken", r.RefreshToken);
        w.WriteString("scope", r.Scope);
        w.WriteString("savedAt", r.SavedAt);
        w.WriteEndObject();
    });
}

internal sealed class OsFingerprint : IFingerprintSource
{
    private static readonly Regex MachineGuid = new(@"MachineGuid\s+REG_SZ\s+(\S+)", RegexOptions.CultureInvariant);
    private static readonly Regex PlatformUuid = new("\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);
    private readonly string? _installIdFile;
    private readonly IOs _os;

    public OsFingerprint(string? installIdFile, IOs os)
    {
        _installIdFile = installIdFile;
        _os = os;
    }

    public async Task<string?> ReadAsync()
    {
        try
        {
            var value = (await MachineIdAsync().ConfigureAwait(false))?.Trim();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 아래 설치 ID로
        }
        if (_installIdFile is null) return null;
        var existing = _os.ReadFile(_installIdFile)?.Trim();
        if (!string.IsNullOrEmpty(existing)) return existing;
        var created = _os.NewId();
        _os.WriteFile(_installIdFile, created);
        return created;
    }

    private async Task<string?> MachineIdAsync()
    {
        switch (_os.Name)
        {
            case "windows":
                {
                    var output = await _os.ExecAsync("reg", new[] { "query", @"HKLM\SOFTWARE\Microsoft\Cryptography", "/v", "MachineGuid" }).ConfigureAwait(false);
                    var m = MachineGuid.Match(output);
                    return m.Success ? m.Groups[1].Value : null;
                }
            case "macos":
                {
                    var output = await _os.ExecAsync("ioreg", new[] { "-rd1", "-c", "IOPlatformExpertDevice" }).ConfigureAwait(false);
                    var m = PlatformUuid.Match(output);
                    return m.Success ? m.Groups[1].Value : null;
                }
            default:
                return _os.ReadFile("/etc/machine-id") is { } id && id.Trim().Length > 0 ? id : _os.ReadFile("/var/lib/dbus/machine-id");
        }
    }
}

internal sealed class FileStore : ICredentialStore
{
    private readonly string _path;
    private readonly IOs _os;

    public FileStore(string path, IBydevLogger? logger, IOs os)
    {
        _path = path;
        _os = os;
        logger?.Warn("로그인 기록을 암호화하지 않은 사용자 전용 파일에 둔다 — 운영체제 보안 저장소를 쓸 수 없는 환경");
    }

    public Task<CredentialRecord?> LoadAsync() => Task.FromResult(OsPorts.ParseRecord(_os.ReadFile(_path)));

    public Task SaveAsync(CredentialRecord record)
    {
        _os.WriteFile(_path, OsPorts.WriteRecord(record));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _os.Remove(_path);
        return Task.CompletedTask;
    }
}

internal sealed class SecretToolStore : ICredentialStore
{
    private readonly string _account;
    private readonly IOs _os;

    public SecretToolStore(string account, IOs os)
    {
        _account = account;
        _os = os;
    }

    private string[] Attributes(string verb, params string[] extra)
    {
        var list = new System.Collections.Generic.List<string> { verb };
        list.AddRange(extra);
        list.AddRange(new[] { "service", Rules.StorageService, "account", _account });
        return list.ToArray();
    }

    public async Task<CredentialRecord?> LoadAsync()
    {
        try
        {
            return OsPorts.ParseRecord(await _os.ExecAsync("secret-tool", Attributes("lookup")).ConfigureAwait(false));
        }
        catch (ExitCodeException e) when (e.SecretToolFoundNothing)
        {
            return null;
        }
    }

    public Task SaveAsync(CredentialRecord record) =>
        _os.ExecAsync("secret-tool", Attributes("store", "--label=bydev " + _account), OsPorts.WriteRecord(record));

    /// <summary>없는 항목을 지우는 것은 성공이다(로그아웃은 몇 번 해도 된다).</summary>
    public async Task ClearAsync()
    {
        try
        {
            await _os.ExecAsync("secret-tool", Attributes("clear")).ConfigureAwait(false);
        }
        catch (ExitCodeException e) when (e.SecretToolFoundNothing)
        {
        }
    }
}

internal sealed class DpapiStore : ICredentialStore
{
    private readonly string _path;
    private readonly IOs _os;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    /// <summary><paramref name="protect"/> · <paramref name="unprotect"/>는 단위 시험이 바꿔 끼운다(Windows가 아닌 곳에서 파일 모양을 본다).</summary>
    public DpapiStore(string path, IOs os, Func<byte[], byte[]>? protect = null, Func<byte[], byte[]>? unprotect = null)
    {
        _path = path;
        _os = os;
        _protect = protect ?? Protect;
        _unprotect = unprotect ?? Unprotect;
    }

    public Task<CredentialRecord?> LoadAsync()
    {
        var sealedText = _os.ReadFile(_path);
        if (string.IsNullOrWhiteSpace(sealedText)) return Task.FromResult<CredentialRecord?>(null);
        var plain = _unprotect(Convert.FromBase64String(sealedText!.Trim()));
        return Task.FromResult(OsPorts.ParseRecord(Encoding.UTF8.GetString(plain)));
    }

    public Task SaveAsync(CredentialRecord record)
    {
        _os.WriteFile(_path, Convert.ToBase64String(_protect(Encoding.UTF8.GetBytes(OsPorts.WriteRecord(record)))));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _os.Remove(_path);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("DPAPI는 Windows에서만");
#pragma warning disable CA1416
        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("DPAPI는 Windows에서만");
#pragma warning disable CA1416
        return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
    }
}
