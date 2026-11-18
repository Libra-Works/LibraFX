using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services;

namespace LiberaFx.Views;

/// <summary>Local drafts first; constructing/showing this window never calls a provider.</summary>
public sealed class CrashReportsWindow : Window
{
    private readonly CrashDraftStore _store;
    private readonly Func<IReadOnlyList<ICrashReportProvider>> _providers;
    private readonly Func<string, string, GitHubCrashReportProvider> _createGitHub;
    private readonly CrashReportSession _session;
    private readonly ComboBox _drafts = new();
    private readonly ComboBox _provider = new();
    private readonly TextBox _repository = new() { PlaceholderText = "owner/repository" };
    private readonly TextBox _credential = new() { Text = "LiberaFX.GitHub" };
    private readonly TextBox _title = new();
    private readonly TextBox _body = new() { AcceptsReturn = true, MinHeight = 120, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _diagnostics = new() { AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _attach = new();
    private readonly CheckBox _consent = new();
    private readonly CheckBox _duplicate = new();
    private readonly TextBlock _destination = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _attachmentSupport = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _form = new() { Spacing = 8 };
    private CancellationTokenSource? _operation;
    private GitHubCrashReportProvider? _github;
    private bool _busy;
    private bool _loading;
    private CrashDraft? _selected;
    private static string T(string key) => LocalizationService.Shared.Get("Crash." + key);
    private sealed record ProviderChoice(ICrashReportProvider? Provider, string Label)
    { public override string ToString() => Label; }
    private sealed record DraftChoice(CrashDraft Draft)
    { public override string ToString() => $"{Draft.Id.ToString("N")[..8]} — {T(Draft.Kind)} — {T(Draft.Delivery)}"; }

    public CrashReportsWindow(CrashDraftStore store, Func<IReadOnlyList<ICrashReportProvider>> providers)
        : this(store, providers, (repository, credential) => new(repository, credential)) { }

    internal CrashReportsWindow(CrashDraftStore store, Func<IReadOnlyList<ICrashReportProvider>> providers,
        Func<string, string, GitHubCrashReportProvider> createGitHub)
    {
        _store = store;
        _providers = providers;
        _createGitHub = createGitHub;
        _session = new CrashReportSession(store, provider => ReferenceEquals(provider, _github) || _providers().Contains(provider));
        Title = T("Title");
        Width = 780;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _attach.Content = T("Attach");
        _consent.Content = T("Consent");
        _duplicate.Content = T("DuplicateConsent");
        _form.Children.Add(new TextBlock { Text = T("Privacy"), TextWrapping = TextWrapping.Wrap });
        AddField("Draft", _drafts);
        AddField("Provider", _provider);
        _form.Children.Add(_attachmentSupport);
        AddField("Repository", _repository);
        AddField("Credential", _credential);
        _form.Children.Add(new TextBlock { Text = T("CredentialHelp"), TextWrapping = TextWrapping.Wrap });
        AddField("ReportTitle", _title);
        AddField("Body", _body);
        AddField("Diagnostics", _diagnostics);
        var attachmentActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddButton(attachmentActions, "ChooseText", async () => await ChooseTextAsync());
        AddButton(attachmentActions, "Redact", () =>
        {
            _diagnostics.Text = CrashReportSafety.RedactLines(_diagnostics.Text ?? "");
            _attach.IsChecked = false;
            _status.Text = T("Redacted");
        });
        _form.Children.Add(attachmentActions);
        _form.Children.Add(_attach);
        _form.Children.Add(new TextBlock { Text = T("AttachmentLimits"), TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddButton(actions, "Save", Save);
        AddButton(actions, "Check", async () => await CheckAsync());
        AddButton(actions, "Delete", async () => await DeleteAsync());
        _form.Children.Add(actions);
        _form.Children.Add(_destination);
        _form.Children.Add(_consent);
        _form.Children.Add(_duplicate);
        AddButton(_form, "Send", async () => await SendAsync());
        var cancel = new Button { Content = T("Cancel") };
        cancel.Click += (_, _) =>
        {
            if (_busy) _operation?.Cancel();
            else Close(); // No implicit persistence of edits.
        };
        var root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var footer = new StackPanel { Spacing = 6, Children = { _status, cancel } };
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _form });
        Content = root;
        _provider.ItemsSource = new[] { new ProviderChoice(null, "GitHub") }
            .Concat(_providers().Where(p => p.Id != "liberafx.github.crash-reports").Select(p => new ProviderChoice(p, p.Name))).ToArray();
        _provider.SelectedIndex = 0;
        _drafts.SelectionChanged += (_, _) => LoadSelected();
        _provider.SelectionChanged += (_, _) =>
        {
            var builtIn = (_provider.SelectedItem as ProviderChoice)?.Provider is null;
            _repository.IsEnabled = builtIn;
            _credential.IsEnabled = builtIn;
            _attachmentSupport.Text = builtIn ||
                (_provider.SelectedItem as ProviderChoice)?.Provider?.AttachmentSupport == CrashAttachmentSupport.InlineText
                ? T("SupportsText") : T("NoTextSupport");
            Invalidate();
        };
        _attachmentSupport.Text = T("SupportsText");
        foreach (var box in new[] { _repository, _credential, _title, _body, _diagnostics })
            box.TextChanged += (_, _) => { if (!_loading) Invalidate(); };
        _attach.IsCheckedChanged += (_, _) => { if (!_loading) Invalidate(); };
        Closed += (_, _) => { _operation?.Cancel(); _session.Invalidate(); };
        Reload();
    }

    private void AddField(string key, Control control)
    {
        _form.Children.Add(new TextBlock { Text = T(key) });
        _form.Children.Add(control);
    }

    private static void AddButton(Panel panel, string key, Action action)
    {
        var button = new Button { Content = T(key) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private void Reload(Guid? selectedId = null)
    {
        var choices = _store.Recover().Select(d => new DraftChoice(d)).ToArray();
        _drafts.ItemsSource = choices;
        var index = Array.FindIndex(choices, c => c.Draft.Id == selectedId);
        _drafts.SelectedIndex = index >= 0 ? index : _drafts.ItemCount > 0 ? 0 : -1;
        if (_drafts.ItemCount == 0) { _selected = null; _title.Text = _body.Text = _diagnostics.Text = ""; }
        if (!string.IsNullOrEmpty(_store.Status)) _status.Text = LocalizationService.Shared.Get(_store.Status);
        else if (_drafts.ItemCount == 0) _status.Text = T("NoDrafts");
    }

    private void LoadSelected()
    {
        _selected = (_drafts.SelectedItem as DraftChoice)?.Draft;
        _loading = true;
        _title.Text = _selected?.Title ?? "";
        _body.Text = _selected?.Body ?? "";
        _diagnostics.Text = _selected?.Diagnostics ?? "";
        _attach.IsChecked = false;
        _duplicate.IsChecked = false;
        _loading = false;
        Invalidate();
        _status.Text = _selected?.Delivery == "Sending" ? T("DuplicateWarning") :
            _selected?.Delivery == "Sent" ? T("AlreadySent") : "";
    }

    private void Invalidate()
    {
        _session.Invalidate();
        _consent.IsChecked = false;
        _destination.Text = "";
    }

    private CrashDraft Edited() => (_selected ?? throw new InvalidOperationException("Crash.InvalidReport")) with
    { Title = _title.Text ?? "", Body = _body.Text ?? "", Diagnostics = _diagnostics.Text ?? "" };

    private void Save()
    {
        try
        {
            if (_selected is null) return;
            _selected = Edited();
            _store.Save(_selected);
            Reload(_selected.Id);
            Invalidate();
            _status.Text = T("Saved");
        }
            catch { _status.Text = T("StorageFailed"); }
        }

        private async Task ChooseTextAsync()
        {
            try
            {
                var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = T("ChooseText"),
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Text") { Patterns = ["*.txt", "*.log"] }]
                });
                if (selected.Count == 0) return;
                await using var stream = await selected[0].OpenReadAsync();
                var buffer = new byte[16001];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count));
                    if (read == 0) break;
                    count += read;
                }
                if (count > 16000) throw new InvalidDataException();
                var text = new UTF8Encoding(false, true).GetString(buffer, 0, count);
                if (text.Contains('\0')) throw new InvalidDataException();
                _diagnostics.Text = text;
                _attach.IsChecked = false;
                _status.Text = T("TextLoaded");
            }
            catch { _status.Text = T("TextLoadFailed"); }
        }

    private async Task CheckAsync()
    {
        if (_busy || _selected is null) return;
        await RunAsync(async token =>
        {
            Invalidate();
            var draft = Edited();
            ICrashReportProvider provider;
            if ((_provider.SelectedItem as ProviderChoice)?.Provider is { } external) provider = external;
            else provider = _github = _createGitHub(_repository.Text ?? "", _credential.Text ?? "");
            var attachments = _attach.IsChecked == true
                ? new[] { new CrashAttachment("diagnostics.txt", "text/plain", Encoding.UTF8.GetBytes(draft.Diagnostics)) }
                : [];
            var report = new CrashReport(draft.Title, draft.Body, attachments);
            var destination = await _session.CheckAsync(provider, draft, report, token);
            _destination.Text = $"{destination.Label}\n{destination.Address.AbsoluteUri}\n{T("Visibility")}: {destination.Visibility}\n{T("PermissionNotice")}";
            _status.Text = T("Checked");
        });
    }

    private async Task SendAsync()
    {
        if (_busy || _selected is null) return;
        await RunAsync(async token =>
        {
            var result = await _session.SendAsync(_consent.IsChecked == true, _duplicate.IsChecked == true, token);
            Reload(_selected?.Id);
            _status.Text = T("Sent") + "\n" + result.AbsoluteUri;
        }, refreshAfterFailure: true);
    }

    private async Task DeleteAsync()
    {
        if (_busy || _selected is null) return;
        var draft = _selected;
        var dialog = new Window
        {
            Title = T("Delete"), Width = 440, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = T("DeleteConfirm"), TextWrapping = TextWrapping.Wrap });
        AddButton(panel, "Delete", () => dialog.Close(true));
        AddButton(panel, "Cancel", () => dialog.Close(false));
        dialog.Content = panel;
        if (await dialog.ShowDialog<bool>(this))
        {
            try { _store.Delete(draft); Invalidate(); Reload(); }
            catch { _status.Text = T("StorageFailed"); }
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool refreshAfterFailure = false)
    {
        _busy = true;
        _form.IsEnabled = false;
        using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _operation = operation;
        try { await action(operation.Token); }
        catch (Exception ex)
        {
            // Keep the edited preview and attachment selection for explicit retry.
            // The store is authoritative about whether an attempt might have reached the tracker.
            var uncertain = false;
            if (refreshAfterFailure && _selected is { } selected)
            {
                try
                {
                    var delivery = _store.Read(selected.Id).Delivery;
                    uncertain = delivery == "Sending";
                    _selected = selected with { Delivery = delivery };
                }
                catch { /* Keep the current review; submission still consults the store. */ }
                _duplicate.IsChecked = false;
            }
            Invalidate();
            var error = CrashReportSession.SafeError(ex);
            _status.Text = LocalizationService.Shared.Get(error) +
                (uncertain && error != "Crash.DuplicateWarning" ? "\n" + T("DuplicateWarning") : "");
        }
        finally { _busy = false; _form.IsEnabled = true; _operation = null; }
    }
}
