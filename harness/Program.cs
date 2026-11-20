// .NET SDK의 적합성 하네스(sdks/spec/harness-protocol.md). 표준 입출력 JSON 줄로 실행기와 이야기하며 SDK를 가상 시계 · 가상 스케줄러 · 대리 전송으로 몬다.
//
// 단일 스레드 펌프다. 포트가 돌려준 Task를 하네스가 이 스레드에서 끝내면 SDK의 이어지는 코드도 그 자리에서 돈다(TaskCompletionSource의 동기 이어짐 —
// TS의 마이크로태스크와 같다). 그래서 명령 하나를 처리하고 때가 된 예약을 다 돌리고 답을 기다리는 요청이 없으면 할 일이 없다.
using System.Text.Json;
using System.Text.Json.Nodes;
using Bydev;

var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
var input = new StreamReader(Console.OpenStandardInput());
var h = new Harness(output, input);
h.Run();

sealed class Harness
{
    private readonly StreamWriter _out;
    private readonly StreamReader _in;
    private readonly List<Timer> _timers = new();
    private readonly Dictionary<string, TaskCompletionSource<HttpResponse>> _pending = new();
    private readonly Queue<double> _randoms = new();
    private long _now;
    private long _seq;
    private int _uuids;
    private int _http;
    private CredentialRecord? _record;
    private bool _failLoad, _failSave, _failClear;
    private bool _failFingerprint; // set fingerprint.fail — 지문 출처를 읽지 못한다(하네스 규약 2절)
    private string? _fingerprint;
    private LicenseClient? _client;
    private LoginAttempt? _login;

    public Harness(StreamWriter output, StreamReader input)
    {
        _out = output;
        _in = input;
    }

    private sealed class Timer : IDisposable
    {
        public long Due;
        public long Seq;
        public required Func<Task> Task;
        public bool Cancelled;

        public void Dispose() => Cancelled = true;
    }

    public void Emit(JsonObject message) => _out.WriteLine(message.ToJsonString());

    private void Fatal(string message)
    {
        Emit(new JsonObject { ["type"] = "fatal", ["message"] = message });
        Environment.Exit(1);
    }

    public void Run()
    {
        Emit(new JsonObject { ["type"] = "ready", ["sdk"] = "dotnet", ["version"] = LicenseClient.SdkVersion, ["specVersion"] = Spec.Version });
        while (_in.ReadLine() is { } line)
        {
            var message = Parse(line);
            switch ((string?)message["type"])
            {
                case "init":
                    Init(message);
                    Settle();
                    break;
                case "call":
                    foreach (var call in message["calls"]!.AsArray())
                        StartCall((string)call!["id"]!, (string)call["method"]!, call["args"] as JsonObject ?? new JsonObject());
                    Settle();
                    break;
                case "advance":
                    Advance((long)message["ms"]!);
                    break;
                case "set":
                    var value = (bool)message["value"]!;
                    switch ((string?)message["key"])
                    {
                        case "storage.failLoad": _failLoad = value; break;
                        case "storage.failSave": _failSave = value; break;
                        case "storage.failClear": _failClear = value; break;
                        case "fingerprint.fail": _failFingerprint = value; break;
                    }
                    Settle();
                    break;
                case "end":
                    Environment.Exit(0);
                    return;
                default:
                    Fatal($"모르는 명령 {(string?)message["type"]}");
                    return;
            }
            Emit(new JsonObject { ["type"] = "idle", ["now"] = _now });
        }
    }

    private JsonObject Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line)!.AsObject();
        }
        catch (JsonException)
        {
            Fatal($"JSON이 아닌 입력: {line}");
            throw;
        }
    }

    // ── 정착 ──────────────────────────────────────────────────

    /// <summary>지금 시각까지 온 예약을 다 돌리고, 답을 기다리는 요청이 있으면 그 답을 읽어 넘긴다 — 할 일이 없을 때까지.</summary>
    private void Settle()
    {
        while (true)
        {
            if (RunDue(_now)) continue;
            if (_pending.Count == 0) return;
            var line = _in.ReadLine();
            if (line is null) Environment.Exit(1);
            var message = Parse(line!);
            if ((string?)message["type"] != "http.response") Fatal($"답을 기다리는 동안 {(string?)message["type"]}");
            var id = (string)message["id"]!;
            if (!_pending.Remove(id, out var tcs)) Fatal($"모르는 http.response id {id}");
            if (message["response"] is JsonObject r)
            {
                var headers = new Dictionary<string, string>();
                foreach (var (k, v) in r["headers"]?.AsObject() ?? new JsonObject()) headers[k.ToLowerInvariant()] = (string?)v ?? "";
                tcs!.SetResult(new HttpResponse((int)r["status"]!, headers, (string?)r["body"] ?? ""));
            }
            else
            {
                tcs!.SetException(new IOException("대리 전송 실패"));
            }
        }
    }

    private bool RunDue(long limit)
    {
        _timers.RemoveAll(t => t.Cancelled);
        var due = _timers.Where(t => t.Due <= limit).OrderBy(t => t.Due).ThenBy(t => t.Seq).FirstOrDefault();
        if (due is null) return false;
        _timers.Remove(due);
        // 끝까지 기다리지 않는다 — 작업이 미래 시각을 기다리면 그 시각은 advance가 와야 온다
        var task = due.Task();
        task.ContinueWith(t => Console.Error.WriteLine($"예약 작업이 던졌다: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }

    private void Advance(long ms)
    {
        var target = _now + ms;
        while (true)
        {
            Settle();
            _timers.RemoveAll(t => t.Cancelled);
            var next = _timers.Where(t => t.Due <= target).Select(t => (long?)t.Due).Min();
            if (next is null) break;
            _now = next.Value;
        }
        _now = target;
        Settle();
    }

    // ── 포트 ──────────────────────────────────────────────────

    private sealed class Ports(Harness h) : ITransport, ICredentialStore, IClock, IScheduler, IRandom, IFingerprintSource, IBydevLogger
    {
        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            var id = $"h{++h._http}";
            var tcs = new TaskCompletionSource<HttpResponse>();
            h._pending[id] = tcs;
            var headers = new JsonObject();
            foreach (var (k, v) in request.Headers) headers[k.ToLowerInvariant()] = v;
            h.Emit(new JsonObject
            {
                ["type"] = "http.request",
                ["id"] = id,
                ["now"] = h._now,
                ["request"] = new JsonObject { ["method"] = request.Method, ["url"] = request.Url, ["headers"] = headers, ["body"] = request.Body },
            });
            return tcs.Task;
        }

        public Task<CredentialRecord?> LoadAsync() =>
            h._failLoad ? Task.FromException<CredentialRecord?>(new IOException("저장소 읽기 실패(시험)")) : Task.FromResult(h._record);

        public Task SaveAsync(CredentialRecord record)
        {
            if (h._failSave) return Task.FromException(new IOException("저장소 쓰기 실패(시험)"));
            h._record = record;
            h.Emit(new JsonObject
            {
                ["type"] = "event",
                ["now"] = h._now,
                ["event"] = new JsonObject { ["kind"] = "storage", ["op"] = "save", ["record"] = RecordJson(record) },
            });
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            if (h._failClear) return Task.FromException(new IOException("저장소 지우기 실패(시험)"));
            h._record = null;
            h.Emit(new JsonObject { ["type"] = "event", ["now"] = h._now, ["event"] = new JsonObject { ["kind"] = "storage", ["op"] = "clear" } });
            return Task.CompletedTask;
        }

        public long NowMs() => h._now;

        public IDisposable Schedule(TimeSpan delay, Func<Task> task)
        {
            var timer = new Timer { Due = h._now + (long)Math.Max(0, delay.TotalMilliseconds), Seq = h._seq++, Task = task };
            h._timers.Add(timer);
            return timer;
        }

        public double NextDouble()
        {
            if (h._randoms.Count == 0) h.Fatal("시나리오의 random 값을 다 썼다 — random에 더 적는다");
            return h._randoms.Dequeue();
        }

        public string NewUuid() => $"00000000-0000-4000-8000-{++h._uuids:x12}";

        public Task<string?> ReadAsync() => Task.FromResult(h._failFingerprint ? null : h._fingerprint);

        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message) => Console.Error.WriteLine("warn: " + message);
    }

    private static JsonObject RecordJson(CredentialRecord r) => new()
    {
        ["version"] = r.Version,
        ["refreshToken"] = r.RefreshToken,
        ["scope"] = r.Scope,
        ["savedAt"] = r.SavedAt,
    };

    // ── 호출 ──────────────────────────────────────────────────

    private void Init(JsonObject message)
    {
        var config = message["config"]!.AsObject();
        _now = DateTimeOffset.Parse((string)message["now"]!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        foreach (var r in message["random"]?.AsArray() ?? new JsonArray()) _randoms.Enqueue((double)r!);
        if (message["storage"] is JsonObject s)
            _record = new CredentialRecord((int)s["version"]!, (string)s["refreshToken"]!, (string)s["scope"]!, (string)s["savedAt"]!);
        var device = config["device"]!.AsObject();
        _fingerprint = (string?)device["fingerprintSource"];
        // 시나리오가 적을 때만 온다 (명세 1절 · ADR-0049)
        var app = config["app"] is JsonObject a ? new AppInfo((string)a["target"]!, (string)a["version"]!) : null;
        var ports = new Ports(this);
        _client = new LicenseClient(new LicenseClientOptions
        {
            BaseUrl = (string)config["baseUrl"]!,
            ClientId = (string)config["clientId"]!,
            ProductId = (string)config["productId"]!,
            Scopes = config["scopes"]!.AsArray().Select(x => (string)x!).ToList(),
            Device = new DeviceInfo((string)device["name"]!, (string)device["platform"]!),
            App = app,
            Ports = new Bydev.Ports { Transport = ports, Store = ports, Clock = ports, Scheduler = ports, Random = ports, Fingerprint = ports, Logger = ports },
        });
        _client.Session.Subscribe(state => Emit(new JsonObject
        {
            ["type"] = "event",
            ["now"] = _now,
            ["event"] = new JsonObject { ["kind"] = "session.state", ["state"] = JsonNode.Parse(state.ToJson()) },
        }));
    }

    private static string Arg(JsonObject args, string key) => (string?)args[key] ?? throw new InvalidOperationException($"인자 {key}가 문자열이 아니다");

    private async Task<JsonNode?> InvokeAsync(string method, JsonObject args)
    {
        var c = _client ?? throw new InvalidOperationException("init 전에 호출했다");
        switch (method)
        {
            case "login.start":
                _login = await c.StartLoginAsync().ConfigureAwait(false);
                return new JsonObject
                {
                    ["userCode"] = _login.UserCode,
                    ["verificationUri"] = _login.VerificationUri,
                    ["verificationUriComplete"] = _login.VerificationUriComplete,
                    ["expiresAt"] = _login.ExpiresAt,
                };
            case "login.wait":
                var result = await (_login ?? throw new InvalidOperationException("login.start 전에 login.wait")).WaitAsync().ConfigureAwait(false);
                return new JsonObject { ["scope"] = result.Scope };
            case "login.cancel":
                _login?.Cancel();
                return null;
            case "hasCredentials":
                return await c.HasCredentialsAsync().ConfigureAwait(false);
            case "entitlement":
                return JsonNode.Parse((await c.EntitlementAsync().ConfigureAwait(false)).ToJson());
            case "devices":
                return JsonNode.Parse((await c.DevicesAsync((string?)args["cursor"], (int?)args["limit"]).ConfigureAwait(false)).ToJson());
            case "releaseDevice":
                return JsonNode.Parse((await c.ReleaseDeviceAsync(Arg(args, "deviceId")).ConfigureAwait(false)).ToJson());
            case "profile":
                return JsonNode.Parse((await c.ProfileAsync().ConfigureAwait(false)).ToJson());
            case "checkForUpdate":
                return JsonNode.Parse((await c.CheckForUpdateAsync(Arg(args, "target"), Arg(args, "currentVersion")).ConfigureAwait(false)).ToJson());
            case "verifyDownload":
                c.VerifyDownload(Convert.FromBase64String(Arg(args, "dataBase64")), Arg(args, "sha256"));
                return null;
            case "verifyToken":
                return JsonNode.Parse((await c.VerifyTokenAsync(Arg(args, "token")).ConfigureAwait(false)).ToJson());
            case "logout":
                return new JsonObject { ["serverRevoked"] = await c.LogoutAsync().ConfigureAwait(false) };
            case "session.start":
                c.Session.Start();
                return null;
            case "session.takeOver":
                c.Session.TakeOver();
                return null;
            case "session.retry":
                c.Session.Retry();
                return null;
            case "session.stop":
                await c.Session.StopAsync().ConfigureAwait(false);
                return null;
            case "session.state":
                return JsonNode.Parse(c.Session.State.ToJson());
            default:
                Fatal($"모르는 호출 {method}");
                return null;
        }
    }

    private void StartCall(string id, string method, JsonObject args)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            JsonObject message;
            try
            {
                var value = await InvokeAsync(method, args).ConfigureAwait(false);
                message = new JsonObject { ["type"] = "result", ["id"] = id, ["now"] = _now, ["ok"] = true, ["value"] = value };
            }
            catch (BydevException e)
            {
                message = new JsonObject { ["type"] = "result", ["id"] = id, ["now"] = _now, ["ok"] = false, ["error"] = JsonNode.Parse(e.ToJson()) };
            }
            catch (Exception e)
            {
                message = new JsonObject
                {
                    ["type"] = "result",
                    ["id"] = id,
                    ["now"] = _now,
                    ["ok"] = false,
                    ["error"] = new JsonObject { ["code"] = "HARNESS_ERROR", ["message"] = e.Message },
                };
            }
            Emit(message);
        }
    }
}
