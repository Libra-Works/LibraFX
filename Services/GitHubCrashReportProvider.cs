using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiberaFx.Extensions.Abstractions;

[assembly: InternalsVisibleTo("CommentImageSmokeTests")]
[assembly: InternalsVisibleTo("ProviderUiChecks")]

namespace LiberaFx.Services;

/// <summary>Explicitly configured github.com only. No fallback, shell, saved token or redirects.</summary>
public sealed class GitHubCrashReportProvider : ICrashReportProvider
{
    private readonly string _repository;
    private readonly string _credentialTarget;
    private readonly Uri _apiBase;
    private readonly Func<HttpClientHandler> _handler;
    private readonly Func<string, string> _credential;
    public string Id => "liberafx.github.crash-reports";
    public string Name => "GitHub";
    public CrashAttachmentSupport AttachmentSupport => CrashAttachmentSupport.InlineText;

    public GitHubCrashReportProvider(string repository, string credentialTarget)
        : this(repository, credentialTarget, new Uri("https://api.github.com/"),
            () => new HttpClientHandler(), WindowsCrashCredential.Read) { }

    internal GitHubCrashReportProvider(string repository, string credentialTarget, Uri apiBase,
        Func<HttpClientHandler> handler, Func<string, string> credential)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z") ||
            repository.Split('/')[1] is "." or "..")
            throw new ArgumentException("Crash.InvalidRepository");
        if (string.IsNullOrWhiteSpace(credentialTarget)) throw new ArgumentException("Crash.CredentialMissing");
        _repository = repository;
        _credentialTarget = credentialTarget;
        _apiBase = apiBase;
        _handler = handler;
        _credential = credential;
    }

    public async Task<ReportDestination> GetDestinationAsync(CancellationToken cancellation)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync("repos/" + _repository, cancellation);
        Check(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var root = json.RootElement;
        if (!root.GetProperty("full_name").GetString()!.Equals(_repository, StringComparison.OrdinalIgnoreCase) ||
            !root.GetProperty("has_issues").GetBoolean() ||
            root.GetProperty("archived").GetBoolean())
            throw new InvalidOperationException("Crash.RepositoryUnavailable");
        return new ReportDestination(_repository, new Uri("https://github.com/" + _repository + "/issues"),
            root.GetProperty("private").GetBoolean() ? "private" : "public");
    }

    public async Task<Uri> SubmitAsync(ReportDestination confirmedDestination, CrashReport report,
        CancellationToken cancellation)
    {
        CrashReportSafety.Validate(report);
        var current = await GetDestinationAsync(cancellation);
        if (current != confirmedDestination) throw new InvalidOperationException("Crash.DestinationChanged");
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync("repos/" + _repository + "/issues",
            new { title = report.Title, body = FormatBody(report) }, cancellation);
        Check(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var uri = new Uri(json.RootElement.GetProperty("html_url").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith("/" + _repository + "/issues/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Crash.ResultUnknown");
        return uri;
    }

    // GitHub issue creation has no attachment upload endpoint. Render as escaped inline text.
    public static string FormatBody(CrashReport report)
    {
        CrashReportSafety.Validate(report);
        var body = new StringBuilder(report.Body);
        foreach (var attachment in report.Attachments ?? [])
        {
            body.Append("\n\n---\nText attachment: ")
                .Append(WebUtility.HtmlEncode(attachment.Name))
                .Append("\n<pre>")
                .Append(WebUtility.HtmlEncode(Encoding.UTF8.GetString(attachment.Content.Span)))
                .Append("</pre>");
        }
        return body.ToString();
    }

    private HttpClient CreateClient()
    {
        var token = _credential(_credentialTarget);
        var handler = _handler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        var client = new HttpClient(handler)
        {
            BaseAddress = _apiBase, Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiberaFx-CrashReports/1");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void Check(HttpResponseMessage response)
    {
        // Never show response bodies, headers, raw exception messages or credentials.
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "Crash.AuthenticationFailed" : "Crash.ConnectionFailed");
    }
}

/// <summary>Generic credentials created by the user in Windows Credential Manager.
/// Not gh's config.yml (which can contain plaintext tokens). No environment-token fallback.</summary>
public static class WindowsCrashCredential
{
    public static string Read(string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Crash.SecretStoreUnsupported");
        if (!CredRead(target, 1, 0, out var pointer))
            throw new InvalidOperationException("Crash.CredentialMissing");
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > 8192 ||
                credential.CredentialBlobSize % 2 != 0)
                throw new InvalidOperationException("Crash.CredentialMissing");
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2)!;
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
