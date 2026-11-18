using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LiberaFx.Extensions.Abstractions;

[assembly: InternalsVisibleTo("CommentImageSmokeTests")]
[assembly: InternalsVisibleTo("ProviderUiChecks")]

namespace LiberaFx.Samples.RedmineCrash;

/// <summary>Trusted DLL sample. SDK + BCL only. No editor dependencies.</summary>
public sealed class RedmineCrashExtension : IEditorExtension
{
    public void Register(IExtensionRegistration registration)
    {
        // Config is intentionally explicit, with no default remote endpoint or credentials.
        // Package fingerprint changes when config changes: approve the modified package in host UI.
        var path = Path.Combine(Path.GetDirectoryName(typeof(RedmineCrashExtension).Assembly.Location)!, "redmine.json");
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing Redmine configuration.");
        registration.AddCrashReportProvider(new Provider(config));
    }

    public void Dispose() { }
    public sealed record Configuration(string BaseUrl, int ProjectId, string CredentialTarget);

    internal sealed class Provider : ICrashReportProvider
    {
        private readonly Configuration _config;
        private readonly Uri _base;
        private readonly Func<HttpClientHandler> _handler;
        private readonly Func<string, string> _credential;
        public string Id => "liberafx.sample.redmine.crash-reports";
        public string Name => "Redmine (sample)";
        public CrashAttachmentSupport AttachmentSupport => CrashAttachmentSupport.None;
        internal Provider(Configuration config) : this(config, () => new HttpClientHandler(), ReadCredential) { }
        internal Provider(Configuration config, Func<HttpClientHandler> handler, Func<string, string> credential)
        {
            _config = config;
            _handler = handler;
            _credential = credential;
            _base = new Uri(config.BaseUrl.TrimEnd('/') + "/");
            if (_base.Scheme != "https" || !string.IsNullOrEmpty(_base.UserInfo) ||
                !string.IsNullOrEmpty(_base.Query) || !string.IsNullOrEmpty(_base.Fragment) ||
                config.ProjectId <= 0 || string.IsNullOrWhiteSpace(config.CredentialTarget))
                throw new InvalidDataException("Invalid Redmine configuration.");
        }

        public async Task<ReportDestination> GetDestinationAsync(CancellationToken cancellation)
        {
            using var client = Client();
            using var response = await client.GetAsync($"projects/{_config.ProjectId}.json", cancellation);
            Check(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var project = json.RootElement.GetProperty("project");
            if (project.GetProperty("id").GetInt32() != _config.ProjectId)
                throw new InvalidDataException("Crash.InvalidDestination");
            // Fail closed on servers which do not disclose the project's visibility.
            var visibility = project.GetProperty("is_public").GetBoolean() ? "public" : "private";
            return new($"Redmine project {_config.ProjectId}",
                new Uri(_base, $"projects/{_config.ProjectId}/issues"), visibility);
        }

        public async Task<Uri> SubmitAsync(ReportDestination confirmedDestination, CrashReport report,
            CancellationToken cancellation)
        {
            if (report.Attachments is { Count: > 0 })
                throw new NotSupportedException("Crash.AttachmentsUnsupported");
            if (await GetDestinationAsync(cancellation) != confirmedDestination)
                throw new InvalidOperationException("Crash.DestinationChanged");
            using var client = Client();
            using var response = await client.PostAsJsonAsync("issues.json",
                new { issue = new { project_id = _config.ProjectId, subject = report.Title, description = report.Body } },
                cancellation);
            Check(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var id = json.RootElement.GetProperty("issue").GetProperty("id").GetInt32();
            if (id <= 0) throw new InvalidDataException("Crash.ResultUnknown");
            return new Uri(_base, "issues/" + id);
        }

        private HttpClient Client()
        {
            var token = _credential(_config.CredentialTarget);
            var handler = _handler();
            handler.AllowAutoRedirect = false;
            handler.UseCookies = false;
            var client = new HttpClient(handler)
            {
                BaseAddress = _base, Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 1024 * 1024
            };
            client.DefaultRequestHeaders.Add("X-Redmine-API-Key", token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private static void Check(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "Crash.AuthenticationFailed" : "Crash.ConnectionFailed");
        }
    }

    private static string ReadCredential(string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Crash.SecretStoreUnsupported");
        if (!CredRead(target, 1, 0, out var pointer)) throw new InvalidOperationException("Crash.CredentialMissing");
        try
        {
            var item = Marshal.PtrToStructure<Credential>(pointer);
            if (item.Size == 0 || item.Size > 8192 || item.Size % 2 != 0)
                throw new InvalidOperationException("Crash.CredentialMissing");
            return Marshal.PtrToStringUni(item.Blob, (int)item.Size / 2)!;
        }
        finally { CredFree(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint Size;
        public IntPtr Blob;
        public uint Persist, Count;
        public IntPtr Attributes;
        public string Alias, User;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
