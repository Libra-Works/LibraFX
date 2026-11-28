using System.Reflection;
using System.Text;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services.Crash;
using LiberaFx.Services.Settings;
using LiberaFx.Views.Windows;

internal static class CrashUi48Tests
{
    public static async Task RunAsync()
    {
        var root = Environment.GetEnvironmentVariable("LIBERAFX_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Isolated data root required.");
        var isolated = Path.Combine(root, "crash-ui-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", isolated);
        try
        {
            await RunIsolatedAsync();
            await CrashTrackerLoopback48Tests.RunAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT", root);
            if (Directory.Exists(isolated)) Directory.Delete(isolated, true);
        }
    }

    private static async Task RunIsolatedAsync()
    {
        using var store = new CrashDraftStore();
        var draft = new CrashDraft(1, Guid.NewGuid(), "ManagedException", "Synthetic report",
            "Synthetic body", "safe\npassword=synthetic-secret");
        store.Save(draft);
        var provider = new SyntheticProvider();
        var window = new CrashReportsWindow(store, () => [provider]);
        try
        {
            window.Show();
            await Task.Delay(200);
            Field<ComboBox>(window, "_provider").SelectedIndex = 1;
            var diagnostics = Field<TextBox>(window, "_diagnostics");
            Invoke(Button(window, "Redact"));
            if ((diagnostics.Text ?? "").Contains("synthetic-secret"))
                throw new Exception("UI redaction did not update preview.");
            Field<CheckBox>(window, "_attach").IsChecked = true;
            Invoke(Button(window, "Check"));
            await WaitAsync(() => provider.Checks > 0, "Destination check");
            if (provider.Sends != 0) throw new Exception("UI sent before consent.");
            Field<CheckBox>(window, "_consent").IsChecked = true;
            Invoke(Button(window, "Send"));
            await WaitAsync(() => provider.Sends == 1, "Explicit send");
            if (provider.Report?.Attachments is not { Count: 1 } ||
                Encoding.UTF8.GetString(provider.Report.Attachments[0].Content.Span).Contains("synthetic-secret"))
                throw new Exception("Unexpected SDK attachment.");
            await WaitAsync(() => !Field<bool>(window, "_busy"), "Send completion");
            Invoke(Button(window, "Delete"));
            await WaitAsync(() => window.OwnedWindows.Count > 0, "Delete confirmation");
            var dialog = window.OwnedWindows.Single();
            var panel = (StackPanel)dialog.Content!;
            var delete = panel.Children.OfType<Button>().Single(b =>
                Equals(b.Content, LocalizationService.Shared.Get("Crash.Delete")));
            Invoke(delete);
            try { await WaitAsync(() => !store.Recover().Any(d => d.Id == draft.Id), "Draft deletion"); }
            catch (TimeoutException)
            {
                throw new TimeoutException("Draft deletion: " + Field<TextBlock>(window, "_status").Text);
            }
        }
        finally
        {
            window.Close();
            store.Delete(draft);
        }

        var retryDraft = draft with { Id = Guid.NewGuid(), Diagnostics = "excluded text" };
        store.Save(retryDraft);
        var retryProvider = new SyntheticProvider { Fail = true };
        var retryWindow = new CrashReportsWindow(store, () => [retryProvider]);
        try
        {
            retryWindow.Show();
            await Task.Delay(200);
            Field<ComboBox>(retryWindow, "_provider").SelectedIndex = 1;
            Invoke(Button(retryWindow, "Check"));
            await WaitAsync(() => retryProvider.Checks == 1 && !Field<bool>(retryWindow, "_busy"), "Retry check");
            Field<CheckBox>(retryWindow, "_consent").IsChecked = true;
            Invoke(Button(retryWindow, "Send"));
            await WaitAsync(() => retryProvider.Sends == 1 && !Field<bool>(retryWindow, "_busy"), "Failed send");
            if (retryProvider.Report?.Attachments is { Count: > 0 } || store.Read(retryDraft.Id).Delivery != "Sending")
                throw new Exception("Excluded text sent or uncertain outcome lost.");
            retryProvider.Fail = false;
            Invoke(Button(retryWindow, "Check"));
            await WaitAsync(() => retryProvider.Checks == 2 && !Field<bool>(retryWindow, "_busy"), "Retry destination check");
            Field<CheckBox>(retryWindow, "_consent").IsChecked = true;
            Field<CheckBox>(retryWindow, "_duplicate").IsChecked = true;
            Invoke(Button(retryWindow, "Send"));
            await WaitAsync(() => retryProvider.Sends == 2 && !Field<bool>(retryWindow, "_busy"), "Explicit retry");
            if (store.Read(retryDraft.Id).Delivery != "Sent") throw new Exception("Retry did not complete.");
        }
        finally
        {
            retryWindow.Close();
            store.Delete(retryDraft);
        }

        var cancelDraft = draft with { Id = Guid.NewGuid() };
        store.Save(cancelDraft);
        var cancelProvider = new SyntheticProvider();
        var cancelWindow = new CrashReportsWindow(store, () => [cancelProvider]);
        try
        {
            cancelWindow.Show();
            await Task.Delay(200);
            Field<TextBox>(cancelWindow, "_body").Text = "Unsaved synthetic change";
            var rootPanel = (DockPanel)cancelWindow.Content!;
            var footer = (StackPanel)rootPanel.Children[0];
            var cancel = footer.Children.OfType<Button>().Single();
            Invoke(cancel);
            await WaitAsync(() => !cancelWindow.IsVisible, "Cancel window");
            if (store.Read(cancelDraft.Id).Body != cancelDraft.Body || cancelProvider.Sends != 0)
                throw new Exception("Cancel persisted or sent unsaved changes.");
        }
        finally
        {
            cancelWindow.Close();
            store.Delete(cancelDraft);
        }
    }

    internal static Button Button(CrashReportsWindow window, string key)
    {
        var form = Field<StackPanel>(window, "_form");
        var label = LocalizationService.Shared.Get("Crash." + key);
        return Descendants(form).OfType<Button>().Single(button => Equals(button.Content, label));
    }
    private static IEnumerable<Control> Descendants(Panel parent)
    {
        foreach (var child in parent.Children)
        {
            yield return child;
            if (child is Panel panel)
                foreach (var item in Descendants(panel)) yield return item;
        }
    }
    internal static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    internal static void Invoke(Button button) =>
        new ButtonAutomationPeer(button).GetProvider<IInvokeProvider>()!.Invoke();
    internal static async Task WaitAsync(Func<bool> predicate, string step)
    {
        for (var i = 0; i < 400; i++)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(step);
    }

    private sealed class SyntheticProvider : ICrashReportProvider
    {
        public string Id => "synthetic.crash.ui";
        public string Name => "Synthetic offline";
        public CrashAttachmentSupport AttachmentSupport => CrashAttachmentSupport.InlineText;
        public int Checks, Sends;
        public bool Fail;
        public CrashReport? Report;
        public Task<ReportDestination> GetDestinationAsync(CancellationToken cancellation)
        {
            Checks++;
            return Task.FromResult(new ReportDestination("Synthetic", new Uri("https://example.invalid/issues"), "private"));
        }
        public Task<Uri> SubmitAsync(ReportDestination destination, CrashReport report, CancellationToken cancellation)
        {
            Sends++;
            Report = report;
            if (Fail) throw new IOException("Synthetic failure");
            return Task.FromResult(new Uri("https://example.invalid/issues/1"));
        }
    }
}
