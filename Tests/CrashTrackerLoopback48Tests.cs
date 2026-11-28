using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using LiberaFx.Samples.RedmineCrash;
using LiberaFx.Services.Crash;
using LiberaFx.Services.Settings;
using LiberaFx.Views.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static CrashUi48Tests;

internal static class CrashTrackerLoopback48Tests
{
    private const string Repository = "synthetic/tracker";
    private const string CredentialTarget = "synthetic-target";
    private const string FakeHeaderValue = "loopback-fixture-only";
    private const string SafeText = "kept <b>literally & safely";
    private const string Sensitive = "password=fixture-secret\nfile:///home/private/log.txt";

    public static async Task RunAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // SChannel requires an imported private key for the test listener on Windows.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        var fixture = new TrackerFixture();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        app.Run(fixture.RespondAsync);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StartAsync(startup.Token);
        try
        {
            var endpoint = new Uri(app.Urls.Single() + "/");
            HttpClientHandler Handler() => new()
            {
                UseProxy = false,
                // No machine trust changes. Only this ephemeral fixture certificate is trusted,
                // only for requests to the bound loopback endpoint.
                ServerCertificateCustomValidationCallback = (message, remote, _, _) =>
                    message.RequestUri is { } address && address.Scheme == "https" &&
                    address.Host == "127.0.0.1" && address.Port == endpoint.Port &&
                    remote is not null &&
                    CryptographicOperations.FixedTimeEquals(remote.GetCertHash(HashAlgorithmName.SHA256),
                        certificate.GetCertHash(HashAlgorithmName.SHA256))
            };
            static string Credential(string target) => target == CredentialTarget
                ? FakeHeaderValue : throw new InvalidOperationException("Unexpected credential target.");
            using var store = new CrashDraftStore();
            var redmine = new RedmineCrashExtension.Provider(new(endpoint.AbsoluteUri + "redmine/", 42, CredentialTarget),
                Handler, Credential);
            foreach (var github in new[] { true, false })
            {
                var draft = new CrashDraft(1, Guid.NewGuid(), "ManagedException", "Synthetic crash",
                    "Synthetic report body", SafeText + "\n" + Sensitive);
                store.Save(draft);
                var window = new CrashReportsWindow(store, () => [redmine],
                    (repository, credential) => new(repository, credential, new Uri(endpoint, "github/"), Handler, Credential));
                try { await ExerciseAsync(window, store, draft, fixture, github); }
                finally { window.Close(); store.Delete(draft); }
            }
        }
        finally { await app.StopAsync(TimeSpan.FromSeconds(10)); }
    }

    private static async Task ExerciseAsync(CrashReportsWindow window, CrashDraftStore store,
        CrashDraft draft, TrackerFixture fixture, bool github)
    {
        var name = github ? "GitHub" : "Redmine";
        var gets = fixture.Gets;
        var posts = fixture.Posts.Count;
        window.Show();
        await Task.Delay(200);
        Require(fixture.Gets == gets && fixture.Posts.Count == posts, $"{name}: window performed network I/O.");
        if (github)
        {
            Field<TextBox>(window, "_repository").Text = Repository;
            Field<TextBox>(window, "_credential").Text = CredentialTarget;
        }
        else Field<ComboBox>(window, "_provider").SelectedIndex = 1;
        await Task.Delay(50);
        var attach = Field<CheckBox>(window, "_attach");
        var consent = Field<CheckBox>(window, "_consent");
        var duplicate = Field<CheckBox>(window, "_duplicate");
        var status = Field<TextBlock>(window, "_status");
        var destination = Field<TextBlock>(window, "_destination");

        attach.IsChecked = true;
        await ActAsync(window, "Check", () => HasStatus("SensitiveText"));
        Require(fixture.Gets == gets && fixture.Posts.Count == posts, $"{name}: unsafe text reached the service.");
        Invoke(Button(window, "Redact"));
        await WaitAsync(() => HasStatus("Redacted"), $"{name}: redact preview");
        Require(attach.IsChecked != true &&
            Field<TextBox>(window, "_diagnostics").Text == SafeText + "\n[REDACTED]\n[REDACTED]",
            $"{name}: redaction preview or selection did not update.");
        attach.IsChecked = true;
        if (!github)
        {
            await ActAsync(window, "Check", () => HasStatus("AttachmentsUnsupported"));
            Require(fixture.Gets == gets && fixture.Posts.Count == posts, "Redmine: unsupported attachment reached the service.");
            attach.IsChecked = false;
        }

        fixture.RejectAuthentication = true;
        await ActAsync(window, "Check", () => HasStatus("AuthenticationFailed"));
        Require(destination.Text == "" && fixture.Gets == ++gets && fixture.Posts.Count == posts,
            $"{name}: rejected authentication authorized send.");
        fixture.RejectAuthentication = false;
        await ActAsync(window, "Check", () => HasStatus("Checked"));
        Require(fixture.Gets == ++gets && fixture.Posts.Count == posts &&
            destination.Text?.Contains(github ? $"https://github.com/{Repository}/issues" : "/redmine/projects/42/issues") == true &&
            destination.Text.Contains("private"), $"{name}: wrong destination preview or premature send.");
        await ActAsync(window, "Send", () => HasStatus("ConsentRequired"));
        Require(fixture.Gets == gets && fixture.Posts.Count == posts, $"{name}: send without consent reached the service.");

        await ActAsync(window, "Check", () => HasStatus("Checked"));
        gets++;
        fixture.RejectPost = true;
        consent.IsChecked = true;
        await ActAsync(window, "Send", () => HasStatus("ConnectionFailed"));
        Require(store.Read(draft.Id).Delivery == "Sending" && fixture.Gets == ++gets && fixture.Posts.Count == ++posts,
            $"{name}: rejected POST lost the uncertain state.");
        fixture.RejectPost = false;

        await ActAsync(window, "Check", () => HasStatus("Checked"));
        gets++;
        Require(attach.IsChecked == github, $"{name}: failed send changed attachment selection.");
        consent.IsChecked = true;
        await ActAsync(window, "Send", () => HasStatus("DuplicateWarning"));
        Require(fixture.Gets == gets && fixture.Posts.Count == posts, $"{name}: duplicate was not acknowledged.");
        await ActAsync(window, "Check", () => HasStatus("Checked"));
        gets++;
        fixture.BlockPost();
        consent.IsChecked = duplicate.IsChecked = true;
        Invoke(Button(window, "Send"));
        try
        {
            await fixture.PostArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var footer = (StackPanel)((DockPanel)window.Content!).Children[0];
            Invoke(footer.Children.OfType<Button>().Single());
            await fixture.PostAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitAsync(() => !Field<bool>(window, "_busy") && HasStatus("Cancelled"), $"{name}: cancelled POST");
        }
        finally { fixture.ReleasePost(); }
        Require(store.Read(draft.Id).Delivery == "Sending" && fixture.Gets == ++gets && fixture.Posts.Count == ++posts,
            $"{name}: cancellation claimed success or retried.");

        attach.IsChecked = github;
        await ActAsync(window, "Check", () => HasStatus("Checked"));
        gets++;
        consent.IsChecked = duplicate.IsChecked = true;
        await ActAsync(window, "Send", () => HasStatus("Sent"));
        Require(store.Read(draft.Id).Delivery == "Sent" && fixture.Gets == ++gets && fixture.Posts.Count == ++posts &&
            status.Text?.Contains("/issues/91") == true, $"{name}: send/result was not confirmed.");
        foreach (var post in fixture.Posts.ToArray()[^3..])
        {
            Require(post.Path == (github ? $"/github/repos/{Repository}/issues" : "/redmine/issues.json"),
                $"{name}: POST reached the wrong API route.");
            using var document = JsonDocument.Parse(post.Json);
            var payload = github ? document.RootElement : document.RootElement.GetProperty("issue");
            Require(payload.GetProperty(github ? "title" : "subject").GetString() == draft.Title &&
                (github || payload.GetProperty("project_id").GetInt32() == 42), $"{name}: wrong tracker payload.");
            var body = payload.GetProperty(github ? "body" : "description").GetString()!;
            Require(!body.Contains("fixture-secret") && !body.Contains("/home/private") &&
                !post.Json.Contains(CredentialTarget) && !post.Json.Contains(FakeHeaderValue), $"{name}: sensitive data was sent.");
            Require(github
                ? body.Contains("diagnostics.txt") && body.Contains("kept &lt;b&gt;literally &amp; safely") && body.Contains("[REDACTED]")
                : body == draft.Body, $"{name}: selected/excluded attachment content was not respected.");
        }
        Require(fixture.InvalidHeaders == 0, $"{name}: API authentication header incorrect.");

        bool HasStatus(string statusKey) => status.Text?.StartsWith(
            LocalizationService.Shared.Get("Crash." + statusKey), StringComparison.Ordinal) == true;
    }

    private static async Task ActAsync(CrashReportsWindow window, string action, Func<bool> outcome)
    {
        Invoke(Button(window, action));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        try { await WaitAsync(() => !Field<bool>(window, "_busy") && outcome(), action); }
        catch (TimeoutException)
        {
            throw new TimeoutException(action + ": " + Field<TextBlock>(window, "_status").Text);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TrackerFixture
    {
        private int _gets, _invalidHeaders;
        public int Gets => Volatile.Read(ref _gets);
        public int InvalidHeaders => Volatile.Read(ref _invalidHeaders);
        public readonly ConcurrentQueue<(string Path, string Json)> Posts = new();
        public volatile bool RejectAuthentication, RejectPost;
        private volatile PostGate? _gate;
        public TaskCompletionSource PostArrived => _gate!.Arrived;
        public TaskCompletionSource PostAborted => _gate!.Aborted;
        public void BlockPost() => _gate = new();
        public void ReleasePost() => _gate = null;

        public async Task RespondAsync(HttpContext context)
        {
            var path = context.Request.Path.Value!;
            var github = path.StartsWith("/github/", StringComparison.Ordinal);
            var actual = context.Request.Headers[github ? "Authorization" : "X-Redmine-API-Key"].ToString();
            var expected = github ? "Bearer " + FakeHeaderValue : FakeHeaderValue;
            if (HttpMethods.IsGet(context.Request.Method)) Interlocked.Increment(ref _gets);
            if (actual != expected) Interlocked.Increment(ref _invalidHeaders);
            if (actual != expected || RejectAuthentication)
            {
                context.Response.StatusCode = github ? 401 : 403;
                return;
            }
            context.Response.ContentType = "application/json";
            if (HttpMethods.IsGet(context.Request.Method) && path == $"/github/repos/{Repository}")
                await context.Response.WriteAsync("""{"full_name":"synthetic/tracker","has_issues":true,"archived":false,"private":true}""");
            else if (HttpMethods.IsGet(context.Request.Method) && path == "/redmine/projects/42.json")
                await context.Response.WriteAsync("""{"project":{"id":42,"is_public":false}}""");
            else if (HttpMethods.IsPost(context.Request.Method) &&
                path is $"/github/repos/{Repository}/issues" or "/redmine/issues.json")
            {
                using var reader = new StreamReader(context.Request.Body);
                Posts.Enqueue((path, await reader.ReadToEndAsync(context.RequestAborted)));
                if (_gate is { } gate)
                {
                    gate.Arrived.TrySetResult();
                    try { await Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted); }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        gate.Aborted.TrySetResult();
                        return;
                    }
                    context.Response.StatusCode = 504;
                    return;
                }
                if (RejectPost) { context.Response.StatusCode = 503; return; }
                context.Response.StatusCode = 201;
                await context.Response.WriteAsync(github
                    ? """{"html_url":"https://github.com/synthetic/tracker/issues/91"}"""
                    : """{"issue":{"id":91}}""");
            }
            else context.Response.StatusCode = 404;
        }

        private sealed class PostGate
        {
            public readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource Aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
