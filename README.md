# 데브 SDK: .NET

데브 라이선스 API의 공식 .NET SDK입니다. [공통 동작 명세](https://github.com/developedby-dev/sdk-spec/blob/main/spec/README.md)를 따르며, [적합성 시험 시나리오](https://github.com/developedby-dev/sdk-spec/tree/main/conformance/scenarios)를 모두 통과합니다.

| 지원 | 버전 |
|---|---|
| 동작 명세 | 1.1.1 |
| API | v1 |
| 대상 | `net8.0`(WPF, WinForms, MAUI, Avalonia용, 트리밍과 Native AOT 지원), `netstandard2.1`(Unity, .NET Core 3.x) |
| 운영체제 | Windows, macOS, Linux |
| 의존성 | `BouncyCastle.Cryptography`(기본 라이브러리에 없는 Ed25519용, 순수 관리 코드), `System.Security.Cryptography.ProtectedData`(DPAPI), `netstandard2.1`에서만 `System.Text.Json` |

NuGet 패키지 이름은 `Bydev.Sdk`이고, 네임스페이스는 `Bydev`입니다. 지금 NuGet에 있는 0.x 판은 이름을 먼저 등록하려고 낸 미리보기입니다. 1.0.0은 서비스 오픈과
함께 내며, 그 전에는 호환되지 않는 변경이 있을 수 있습니다. 판매하는 앱에는 1.0.0 이상을 써 주세요.

## 빠른 시작

```csharp
using Bydev;

const string ClientId = "cli_01J9M0Y6X4N3Q8ZD5R2KT7VCPW"; // 판매자 콘솔에서 제품마다 발급받습니다
var client = new LicenseClient(new LicenseClientOptions
{
    BaseUrl = "https://api.developedby.dev",
    ClientId = ClientId,
    ProductId = "prod_01J9KZ3F7W2X8YQ4N6M5R1TBCD",
    Scopes = new[] { "entitlement.read", "session.write", "version.read" },
    Device = OsPorts.SystemDeviceInfo(),
    App = new AppInfo("WINDOWS_X64", "1.4.0"), // 콘솔에 업로드한 빌드의 버전과 같게
    Ports = new Ports
    {
        Store = OsPorts.DefaultCredentialStore("MyApp", ClientId, logger),
        Fingerprint = OsPorts.FingerprintSource(Path.Combine(OsPorts.AppDataDir("MyApp"), "install-id")),
    },
});

if (!await client.HasCredentialsAsync())
{
    var login = await client.StartLoginAsync();
    ShowCode(login.UserCode, login.VerificationUriComplete); // 화면에 코드와 QR 코드를 보여 줍니다
    await login.WaitAsync(); // 사용자가 브라우저에서 승인할 때까지 기다립니다. 취소는 login.Cancel()로 합니다
}

using var subscription = client.Session.Subscribe(state =>
    dispatcher.Invoke(() => // SDK의 스레드에서 불리므로, UI 스레드로 넘깁니다
    {
        switch (state)
        {
            case SessionState.Active a: EnableFeatures(a.Claims); break; // exp와 sid는 기능 코드에서 쓰고, 참/거짓 하나로만 판단하지 마세요
            case SessionState.Degraded d: EnableFeatures(d.Claims); break; // OfflineUntil까지는 계속 쓸 수 있습니다
            case SessionState.NeedsDecision n: AskUser(n); break; // DEVICE_IN_USE: "여기서 사용할까요?"에 동의하면 client.Session.TakeOver()를 부릅니다
            case SessionState.Locked l: LockApp(l.Code, l.CorrelationId); break; // 오류 화면에 CorrelationId를 보여 주세요
        }
    }));
client.Session.Start();
// 앱을 끌 때
await client.Session.StopAsync();
```

SDK 안의 `await`는 모두 `ConfigureAwait(false)`로 되어 있어서, SDK 안에서 이어지는 코드는 UI 스레드로 돌아오지 않습니다. 앱에서도 `.Result`로 스레드를
막지 말고 `await`로 기다리세요.

## 포트

| 포트 | 기본 구현 | 직접 구현하거나 설정할 항목 |
|---|---|---|
| `Transport` | `HttpClientTransport`. 시간 초과는 30초이고, 리다이렉트를 따라가지 않습니다. | 프록시나 인증서 고정이 필요하면 `new HttpClientTransport(httpClient)`를 주세요. 이때 리다이렉트를 끄는 것은 앱이 맡습니다. |
| `Store` | **메모리**. 앱을 끄면 로그인이 사라집니다. | `OsPorts.DefaultCredentialStore`, `DpapiCredentialStore`, `SecretToolCredentialStore`, `FileCredentialStore` |
| `Fingerprint` | 없음. 실행할 때마다 새 기기로 보일 수 있습니다(경고 로그를 남깁니다) | `OsPorts.FingerprintSource(설치 ID 파일)`. MachineGuid, IOPlatformUUID, machine-id를 읽고, 못 읽으면 설치 ID를 씁니다. |
| `Clock`, `Scheduler`, `Random` | 시스템 시계, `TimerScheduler`(`System.Threading.Timer`), `RandomNumberGenerator` | 시험할 때 교체합니다. SDK는 시간이 지나기를 기다릴 때 `IScheduler.Schedule`만 씁니다. |
| `Logger` | 로그 기록 없음(`NullBydevLogger`) | 토큰과 기기 지문 원본은 로그로 넘어오지 않습니다. |

`DefaultCredentialStore`는 Windows에서는 DPAPI(현재 사용자 범위)로 보호한 파일을, Linux에서는 Secret Service를 씁니다. Linux에 `secret-tool`이 없으면 0600
파일에 저장하고 경고를 남깁니다.

macOS에서는 TypeScript, Go SDK와 같은 이유로 키체인을 쓰지 않고, 0600 파일에 저장한 뒤 경고를 남깁니다. 키체인이 꼭 필요한
앱은 Security.framework로 `ICredentialStore`를 직접 구현해 주세요.

SDK는 비밀 값(refresh_token)을 명령줄 인자로 넘기지 않고, `secret-tool`에 표준 입력으로만
전달합니다.

**Linux용 배포 패키지에는 `secret-tool`을 의존성으로 걸어 주세요.** 기본 저장소는 이 명령으로 Secret Service에 접근하는데, Debian과 Ubuntu 데스크톱의
기본 설치에는 이 명령이 없습니다.

Ubuntu 26.04.1에서 확인했으며, Secret Service 자체는 실행되고 있었습니다. .deb 패키지는 `libsecret-tools`를 `Depends`나
`Recommends`에 넣고, RPM(Fedora 등)과 Arch 패키지는 `libsecret`을 넣어 주세요.

의존성을 지정할 수 없는 AppImage나 tar.gz 배포본에는 `secret-tool` 설치 방법을 안내해 주세요.
이 명령이 없으면 0600 파일에 대신 저장하고 경고 로그를 남깁니다.

## 판매자 서버

```csharp
// 웹훅은 받은 본문을 그대로 넣어 검증합니다. 파싱했다가 다시 만든 바이트로는 서명이 맞지 않습니다.
// 시크릿을 교체하는 중이면 두 시크릿을 모두 넘기세요
var body = await new StreamReader(Request.Body).ReadToEndAsync();
var webhook = Webhooks.Verify(new[] { secret }, Request.Headers.Select(h => KeyValuePair.Create(h.Key, h.Value.ToString())), body);

// 앱이 보낸 access_token이나 licenseToken을 검증합니다. JWKS를 받아 캐시하므로 인스턴스 하나를 오래 두고 쓰세요
var verifier = new ServerVerifier("https://api.developedby.dev", clientId);
var claims = await verifier.VerifyAccessTokenAsync(token);
```

## 사용 조건

- **업데이트**: `CheckForUpdateAsync(target, currentVersion)`가 반환한 `UpdateStatus`가 `REQUIRED`이면, 업데이트하기 전까지 앱 실행을 막아 주세요. 받은
  파일은 설치하기 전에 `VerifyDownload(data, check.Install.Sha256)`로 확인해 주세요.

  `Entitled`가 있으면 최상위 값 대신 `Entitled.Release`를 따라 주세요. 업데이트 제공 범위 밖의 버전을 설치하면 세션 획득이
  `ENTITLEMENT_VERSION_NOT_COVERED`로 거절됩니다. `Entitled.Release`가 `null`이면 그 대상에는 받을 수 있는 버전이 없습니다.
- **오류**: 실패하면 `BydevException`이 납니다. `Code`로 분기해 주세요. 코드 값은 생성된 `Codes.*` 상수에 있습니다. `Message`는 화면에 보여 주는 용도로만
  사용해 주세요. `IsTransient`가 참이면 다시 시도해 볼 만한 오류입니다.
- **모르는 값**: `EntitlementStatus` 같은 열거형은 `string`입니다. 그래서 이 버전의 SDK가 모르는 새 값도 그대로 담깁니다. `switch`에는 `default`를 넣어
  두어 주세요.

  모든 모델에는 기록과 진단에 쓸 수 있는 `ToJson()`이 있습니다.
- 라이선스: MIT 또는 Apache-2.0.

## 출처 확인

이 꾸러미는 공개 저장소 `developedby-dev/sdk-dotnet`의 GitHub Actions가 판 태그에서 짓고 올립니다. NuGet에 올린 `.nupkg`에는 GitHub 출처 증명이 있습니다. 받은 파일을 `gh attestation verify <파일> --repo developedby-dev/sdk-dotnet`로 확인할 수 있습니다.

## 개발

| 명령 | 하는 일 |
|---|---|
| `dotnet build Bydev.slnx` | 라이브러리(`netstandard2.1`, `net8.0`), 하네스, 시험을 빌드합니다. 경고도 오류로 처리합니다. |
| `dotnet test tests` | 단위 시험입니다. 실제 전송은 적합성 시험용 가짜 서버(HTTP 모드)를 node로 띄워 확인합니다. 쓸 node는 `BYDEV_NODE`로 바꿀 수 있습니다. |
| `pnpm sdk:check dotnet` | `dotnet format`, 빌드, 단위 시험, 적합성 시나리오 48개를 확인합니다. |

`src/Bydev.Sdk/Generated.cs`는 생성된 파일이며 `pnpm --filter @bydev/sdk-codegen generate`로 다시 만듭니다. 시험과 하네스는 `net10.0` 대상이라 .NET SDK
10이 필요합니다.

라이브러리를 쓰는 앱은 .NET 8이나 netstandard2.1 런타임이면 됩니다.
