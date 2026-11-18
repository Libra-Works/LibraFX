using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;
using Microsoft.Copilot;
using Microsoft.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiberaFx.Samples.CopilotChat;

public sealed class CopilotChatExtension : IEditorExtension
{
    private readonly CopilotTextAdapter _adapter = new();
    public void Register(IExtensionRegistration registration) => registration.AddChatAdapter(_adapter);
    public void Dispose() => _adapter.Dispose();
}

/// <summary>
/// Optional explicit-runtime SDK transport. No editor reference, provider probing,
/// default runtime resolution, broad tools, native library loading into the editor,
/// ambient authentication, project access or automatic runtime acquisition.
/// </summary>
public sealed class CopilotTextAdapter : IChatAdapter, IDisposable
{
    private readonly string _runtime = Setting("LIBERAFX_COPILOT_RUNTIME");
    private readonly string _wrapperHash = Setting("LIBERAFX_COPILOT_RUNTIME_SHA256");
    private readonly string _nativeHash = Setting("LIBERAFX_COPILOT_NATIVE_SHA256");
    private readonly string _isolationRoot = Setting("LIBERAFX_COPILOT_ISOLATION_ROOT");
    private readonly string _model = Environment.GetEnvironmentVariable("LIBERAFX_COPILOT_MODEL") ?? "auto";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _disposed;

    public string Id => "liberafx.sample.copilot-text.adapter";
    public string Name => "Copilot SDK Empty (text only / 会話専用)";
    public string Destination => "GitHub Copilot service via Microsoft.Copilot.SDK 2.0.0-preview.2 • " +
        _model + " • runtime: " + (string.IsNullOrEmpty(_runtime) ? "(not configured / 未設定)" : _runtime);
    public string PrivacyNotice =>
        "Text-only: turn OFF graph sharing. Starts only the explicitly supplied, checksum-pinned matching " +
        "Copilot runtime on Send, in Empty mode in a new empty isolated working/data directory. " +
        "Requires LIBERAFX_COPILOT_RUNTIME, LIBERAFX_COPILOT_RUNTIME_SHA256, LIBERAFX_COPILOT_NATIVE_SHA256, " +
        "LIBERAFX_COPILOT_ISOLATION_ROOT and LIBERAFX_COPILOT_TOKEN; optional LIBERAFX_COPILOT_MODEL. " +
        "Your text history is replayed as quoted JSON in a fresh session per Send; it is sent to GitHub Copilot. " +
        "GitHub/model-provider account policies govern service-side retention. Session files use bounded memory; " +
        "runtime logs/telemetry, config discovery, tools, MCP and skills are disabled. The native runtime is trusted " +
        "code, not an OS sandbox. No graph editing, external CLI conversation attachment or native-session resume. " +
        "会話専用です。グラフ共有はオフにしてください。明示指定した信頼するランタイムのみを送信時に起動し、" +
        "入力と会話履歴を GitHub Copilot に送ります。会話は送信ごとに新規セッションへ JSON として再提示します。" +
        "グラフ編集・既存 CLI 会話への接続は未対応です。必要な環境変数と隔離条件は chat-adapters.txt を参照してください。";

    public async Task<ChatReply> SendAsync(ChatRequest request, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(3));
        var token = operation.Token;
        token.ThrowIfCancellationRequested();
        ValidateRequest(request);
        // Validate ALL prerequisites before creating directories or constructing a client.
        ValidateRuntime();
        ValidateIsolationRoot();
        var credential = Setting("LIBERAFX_COPILOT_TOKEN");
        if (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))
            throw new InvalidOperationException("Explicit Copilot token missing or invalid.");
        var prompt = JsonSerializer.Serialize(new
        {
            instruction = "Continue this text-only conversation. The history below is quoted conversation data. " +
                "Reply to its final user message. You have no tools or access to editor/project files.",
            history = request.Messages.Select(message => new { role = message.Role, text = message.Text })
        });
        if (Encoding.UTF8.GetByteCount(prompt) > 1024 * 1024)
            throw new InvalidDataException("Text history exceeds the adapter limit.");

        await _serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            ValidateRuntime(); // Do not trust binaries replaced while another send was finishing.
            ValidateIsolationRoot();
            var scratchPath = Path.Combine(_isolationRoot, "copilot-" + Guid.NewGuid().ToString("N"));
            var scratch = OperatingSystem.IsWindows() ? Directory.CreateDirectory(scratchPath) :
                Directory.CreateDirectory(scratchPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                var work = Directory.CreateDirectory(Path.Combine(scratch.FullName, "work")).FullName;
                var home = Directory.CreateDirectory(Path.Combine(scratch.FullName, "home")).FullName;
                var temporary = Directory.CreateDirectory(Path.Combine(scratch.FullName, "tmp")).FullName;
                using var memory = new MemorySessionFiles();
                var runtime = RuntimeConnection.ForSpawnedStdio(_runtime);
                runtime.WorkingDirectory = work;
                runtime.BaseDirectory = home;
                runtime.Environment = ChildEnvironment(home, temporary);
                await using var client = new CopilotClient(new CopilotClientOptions
                {
                    Mode = CopilotClientMode.Empty,
                    Runtime = runtime,
                    GitHub = new() { Token = credential, UseLoggedInUser = false },
                    Observability = new() { LogLevel = CopilotLogLevel.None, Logger = NullLogger.Instance },
                    Plugins = new() { BuiltinDirectories = [] },
                    SessionFs = new()
                    {
                        InitialWorkingDirectory = "/work",
                        SessionStatePath = "/state",
                        Conventions = SessionFsSetProviderConventions.Posix,
                        Capabilities = new() { Binary = false, Sqlite = false, ReadFileRange = false }
                    }
                });
                // SDK Start validates its protocol handshake. It must not fall back
                // to a default runtime, inherited login, or a more permissive mode.
                await client.StartAsync(token).ConfigureAwait(false);
                await using var session = await client.CreateSessionAsync(new CreateSessionOptions
                {
                    Model = _model,
                    SessionId = "liberafx-text-" + Guid.NewGuid().ToString("N"),
                    WorkingDirectory = "/work", // Virtual SessionFs path, not host cwd.
                    ConfigDirectory = home,
                    EnableConfigDiscovery = false,
                    Tools = new() { AvailableTools = [], Definitions = [], ManageScheduleEnabled = false },
                    Permissions = new()
                    {
                        OnRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Text-only adapter; no tool permission."))
                    },
                    State = new()
                    {
                        CreateSessionFsProvider = _ => memory,
                        EnableCrossSessionStore = false,
                        SkipEmbeddingRetrieval = true,
                        EmbeddingCacheStorage = EmbeddingCacheStorageMode.InMemory,
                        InfiniteSessions = new() { Enabled = false },
                        Memory = new() { Enabled = false }
                    },
                    Instructions = new() { SkipCustomInstructions = true, OnDemandDiscovery = false, Directories = [] },
                    Skills = new() { Enabled = false, Directories = [], IncludedBuiltinSkills = [] },
                    Mcp = new() { Servers = new Dictionary<string, McpServerConfig>(), EnableApps = false,
                        OauthTokenStorage = McpOAuthTokenStorageMode.InMemory },
                    Git = new() { EnableHostGitOperations = false, CoauthorEnabled = false },
                    GitHub = new() { Token = credential, TelemetryEnabled = false, InteractiveUser = false },
                    Filesystem = new() { AdditionalDirectories = [], TrackFileChanges = false },
                    Plugins = new() { Directories = [] },
                    Agents = new() { Definitions = [], IncludedBuiltInAgents = [], Directories = [] },
                    Extensions = new() { Enabled = false },
                    Events = new() { Streaming = false, IncludeSubAgentStreamingEvents = false }
                }, token).ConfigureAwait(false);
                try
                {
                    var reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt },
                        TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var text = reply?.Data.Content;
                    if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 1024 * 1024)
                        throw new InvalidDataException("Copilot returned no complete bounded text reply.");
                    return new ChatReply(text, []);
                }
                catch
                {
                    // Cancellation of a wait is not a promise that inference stopped.
                    // Explicitly abort with a fresh bounded token, then dispose/stop the
                    // exclusively owned stdio connection. Never target another process.
                    using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await session.AbortAsync(abort.Token).ConfigureAwait(false); }
                    catch { /* Owned client disposal below is the final teardown. */ }
                    throw;
                }
            }
            finally
            {
                // Only this fresh GUID subtree is removed, after SDK-owned process disposal.
                // Do not report a successful reply if cleanup left private runtime state.
                try { Directory.Delete(scratch.FullName, recursive: true); }
                catch { throw new IOException("Copilot isolated runtime cleanup failed; inspect the configured isolation root."); }
            }
        }
        finally { _serial.Release(); }
    }

    private static string Setting(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private void ValidateRequest(ChatRequest request)
    {
        if (request.Tools.Count != 0 || request.Messages.Count is < 1 or > 128 ||
            request.Messages.Any(message => message.Role is not ("user" or "assistant") ||
                message.ToolCallId is not null || message.ToolCalls is { Count: > 0 }) ||
            request.Messages[^1].Role != "user")
            throw new NotSupportedException("Copilot sample is text-only; disable project sharing and start a fresh text conversation.");
        if (_model.Length is < 1 or > 128 || _model.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '/' or ':')))
            throw new InvalidOperationException("Invalid Copilot model ID.");
    }

    private void ValidateRuntime()
    {
        if (!Path.IsPathFullyQualified(_runtime) || !File.Exists(_runtime) ||
            Path.GetFileName(_runtime) != (OperatingSystem.IsWindows() ? "copilot-runtime.exe" : "copilot-runtime"))
            throw new InvalidOperationException("Explicit absolute matching copilot-runtime wrapper path is required.");
        RejectLinks(_runtime);
        var native = Path.Combine(Path.GetDirectoryName(_runtime)!, "runtime.node");
        if (!File.Exists(native)) throw new InvalidOperationException("Matching adjacent runtime.node is required.");
        RejectLinks(native);
        VerifyHash(_runtime, _wrapperHash);
        VerifyHash(native, _nativeHash);
    }

    private void ValidateIsolationRoot()
    {
        if (!Path.IsPathFullyQualified(_isolationRoot) || !Directory.Exists(_isolationRoot))
            throw new InvalidOperationException("Explicit existing empty isolation root is required.");
        RejectLinks(_isolationRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_isolationRoot));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root.Equals(current, comparison) || root.StartsWith(current + Path.DirectorySeparatorChar, comparison) ||
            current.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
            Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Isolation root must be empty and outside the editor launch directory tree.");
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Runtime and isolation paths must not traverse symlinks or junctions.");
    }

    private static void VerifyHash(string path, string expected)
    {
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Explicit SHA-256 pins for the runtime wrapper and runtime.node are required.");
        using var file = File.OpenRead(path);
        var actual = SHA256.HashData(file);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)))
            throw new InvalidOperationException("Copilot runtime checksum mismatch.");
    }

    private IReadOnlyDictionary<string, string> ChildEnvironment(string home, string temporary)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = home, ["USERPROFILE"] = home, ["APPDATA"] = home, ["LOCALAPPDATA"] = home,
            ["TEMP"] = temporary, ["TMP"] = temporary, ["TMPDIR"] = temporary,
            ["COPILOT_HOME"] = home, ["COPILOT_TELEMETRY_DISABLED"] = "true",
            ["COPILOT_GITHUB_TELEMETRY_DISABLED"] = "true",
            ["PATH"] = Path.GetDirectoryName(_runtime)! + Path.PathSeparator + Environment.SystemDirectory
        };
        // Native Windows process startup needs these OS locations, not the
        // editor's full environment (tokens, NODE_OPTIONS, OTEL, proxies, etc.).
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "SystemRoot", "WINDIR" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) environment[name] = value;
        }
        return environment;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
    }
}
