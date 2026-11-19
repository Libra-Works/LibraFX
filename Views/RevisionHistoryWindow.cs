using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed class RevisionHistoryWindow : UserControl
{
    private readonly MainViewModel _main;
    private readonly string? _file;
    private readonly GraphDocumentViewModel? _document;
    private readonly ComboBox _provider = new();
    private readonly ComboBox _before = new() { Width = 260 };
    private readonly ComboBox _after = new() { Width = 260 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly RevisionGraphCanvas _left = new();
    private readonly RevisionGraphCanvas _right = new();
    private readonly ListBox _changes = new();
    private readonly ListBox _workspaceChanges = new() { MaxHeight = 170 };
    private readonly TextBlock _branch = new();
    private readonly Button _workspaceRefresh = new();
    private readonly Button _openForEdit = new();
    private readonly TextBlock _scope = new();
    private readonly bool _canCompare;
    private GraphSnapshot? _beforeGraph, _afterGraph;
    private IReadOnlyList<GraphNodeDifference> _differences = [];
    private CancellationTokenSource? _lifetime;
    private bool _busy;
    private sealed record Choice(string? Revision, string Label)
    { public override string ToString() => Label; }
    private sealed record ChangeChoice(GraphNodeDifference Difference, string Label)
    { public override string ToString() => Label; }

    public RevisionHistoryWindow(MainViewModel main, string? resourceFile = null, bool compareGraphs = true)
    {
        _main = main;
        var active = main.ActiveDocument;
        var activeFile = Uri.TryCreate(active?.FileKey, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        _file = resourceFile ?? activeFile;
        _document = resourceFile is null || string.Equals(resourceFile, activeFile,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? active : null;
        _canCompare = compareGraphs;
        var text = LocalizationService.Shared;
        _provider.ItemsSource = main.Extensions.VersionControlProviders;
        _provider.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
        _provider.SelectedIndex = _provider.ItemCount > 0 ? 0 : -1;
        Avalonia.Automation.AutomationProperties.SetName(_provider, text.Get("Vcs.Provider"));
        Avalonia.Automation.AutomationProperties.SetName(_before, text.Get("Vcs.Before"));
        Avalonia.Automation.AutomationProperties.SetName(_after, text.Get("Vcs.After"));
        ToolTip.SetTip(_before, text.Get("Vcs.Before"));
        ToolTip.SetTip(_after, text.Get("Vcs.After"));
        var refresh = new IconButton
        {
            Classes = { "editor-tool" }, Icon = LiberaFx.Models.Appearance.EditorIcon.Refresh
        };
        ToolTip.SetTip(refresh, text.Get("Vcs.History"));
        Avalonia.Automation.AutomationProperties.SetName(refresh, text.Get("Vcs.History"));
        var compare = new IconButton
        {
            Classes = { "editor-tool", "transport-primary" }, Icon = LiberaFx.Models.Appearance.EditorIcon.Check
        };
        ToolTip.SetTip(compare, text.Get("Vcs.Compare"));
        Avalonia.Automation.AutomationProperties.SetName(compare, text.Get("Vcs.Compare"));
        refresh.Click += async (_, _) => await LoadHistoryAsync();
        compare.Click += async (_, _) => await CompareAsync();
        _workspaceRefresh.Content = text.Get("Vcs.WorkspaceRefresh");
        _workspaceRefresh.Click += async (_, _) => await RunWorkspaceOperationAsync(openForEdit: false);
        _openForEdit.Content = text.Get("Vcs.OpenForEdit");
        _openForEdit.Click += async (_, _) => await RunWorkspaceOperationAsync(openForEdit: true);
        var controls = new Border
        {
            Classes = { "editor-bar" }, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new WrapPanel
            {
                Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4,
                Children =
                {
                    new Border
                    {
                        Classes = { "editor-toolbar-group" }, Padding = new Thickness(4, 2),
                        Child = new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
                            Children = { _provider, refresh }
                        }
                    },
                    new Border
                    {
                        Classes = { "editor-toolbar-group" }, Padding = new Thickness(4, 2),
                        Child = new StackPanel
                        {
                            Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
                            Children = { _before, _after, compare }
                        }
                    }
                }
            }
        };
        var graphs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        graphs.Children.Add(_left);
        Grid.SetColumn(_right, 1);
        graphs.Children.Add(_right);
        _before.IsVisible = _after.IsVisible = compare.IsVisible = _canCompare;
        var workspace = new Expander
        {
            Header = text.Get("Vcs.Workspace"),
            IsExpanded = resourceFile is not null,
            Margin = new Thickness(10, 0),
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new WrapPanel { ItemSpacing = 6, Children = { _workspaceRefresh, _openForEdit } },
                    _branch, _workspaceChanges
                }
            }
        };
        var root = new Grid { RowDefinitions = new RowDefinitions(_canCompare ? "Auto,Auto,Auto,*,130,Auto" : "Auto,Auto,Auto,0,*,Auto"), RowSpacing = 6 };
        root.Children.Add(controls);
        Grid.SetRow(workspace, 1);
        root.Children.Add(workspace);
        _scope.Margin = new Thickness(10, 0);
        Grid.SetRow(_scope, 2);
        root.Children.Add(_scope);
        Grid.SetRow(graphs, 3);
        root.Children.Add(graphs);
        _changes.Margin = new Thickness(10, 0);
        Grid.SetRow(_changes, 4);
        root.Children.Add(_changes);
        _status.Margin = new Thickness(10, 0, 10, 8);
        _status.Classes.Add("editor-label");
        Grid.SetRow(_status, 5);
        root.Children.Add(_status);
        Content = root;
        _provider.SelectionChanged += (_, _) =>
        {
            _before.ItemsSource = null;
            _after.ItemsSource = null;
            _changes.ItemsSource = null;
            _workspaceChanges.ItemsSource = null;
            _branch.Text = "";
            _beforeGraph = _afterGraph = null;
            _scope.Text = "";
            _left.Show(new([], []), [], true, new([], []));
            _right.Show(new([], []), [], false, new([], []));
            _status.Text = _file is null ? text.Get("Vcs.SaveFirst") :
                _provider.SelectedItem is null ? text.Get("Vcs.NoProvider") : _file;
            UpdateWorkspaceActions();
        };
        _changes.SelectionChanged += (_, _) =>
        {
            if (_changes.SelectedItem is ChangeChoice choice)
                ShowScope(choice.Difference.Path.Split('/').SkipLast(1).Select(Guid.Parse).ToArray());
        };
        AttachedToVisualTree += (_, _) => _lifetime = new CancellationTokenSource();
        DetachedFromVisualTree += (_, _) =>
        {
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        };
        _status.Text = _file is null ? text.Get("Vcs.SaveFirst") :
            _provider.ItemCount == 0 ? text.Get("Vcs.NoProvider") : _file;
        UpdateWorkspaceActions();

        void UpdateWorkspaceActions()
        {
            _workspaceRefresh.IsVisible = _file is not null && _provider.SelectedItem is IVersionControlWorkspaceProvider;
            _openForEdit.IsVisible = _file is not null && _provider.SelectedItem is IVersionControlEditProvider;
            workspace.IsVisible = _workspaceRefresh.IsVisible || _openForEdit.IsVisible;
        }
    }

    private async Task LoadHistoryAsync()
    {
        if (_busy || _file is null || _lifetime is not { } lifetime ||
            _provider.SelectedItem is not IVersionControlProvider provider)
        {
            return;
        }
        var token = lifetime.Token;
        _busy = true;
        _provider.IsEnabled = false;
        try
        {
            var revisions = await provider.GetHistoryAsync(_file, token);
            token.ThrowIfCancellationRequested();
            if (!_main.Extensions.VersionControlProviders.Contains(provider))
                throw new InvalidOperationException("The selected provider was disabled.");
            var choices = revisions.Take(100).Select(revision =>
                new Choice(revision.Id, revision.Id[..Math.Min(8, revision.Id.Length)] + " " + revision.Summary)).ToArray();
            _before.ItemsSource = choices;
            var text = LocalizationService.Shared;
            _after.ItemsSource = _canCompare
                ? new[] { new Choice(null, text.Get(_document is null ? "Vcs.WorkingFile" : "Vcs.Working")) }.Concat(choices).ToArray()
                : null;
            if (!_canCompare)
            {
                _changes.ItemsSource = choices;
            }
            _before.SelectedIndex = choices.Length > 0 ? 0 : -1;
            _after.SelectedIndex = _canCompare ? 0 : -1;
            _status.Text = text.Get(_canCompare ? "Vcs.ReadOnly" : "Vcs.HistoryOnly");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.GetBaseException().Message; }
        finally { _busy = false; _provider.IsEnabled = true; }
    }

    private async Task RunWorkspaceOperationAsync(bool openForEdit)
    {
        if (_busy || _file is null || _lifetime is not { } lifetime ||
            _provider.SelectedItem is not IVersionControlProvider provider)
        {
            return;
        }
        _busy = true;
        _provider.IsEnabled = false;
        var token = lifetime.Token;
        var text = LocalizationService.Shared;
        _workspaceRefresh.IsEnabled = _openForEdit.IsEnabled = false;
        try
        {
            if (openForEdit && provider is IVersionControlEditProvider edit)
            {
                if (TopLevel.GetTopLevel(this) is not Window owner)
                {
                    return;
                }
                var cancel = new Button { Content = text.Get("Extensions.Cancel"), IsCancel = true };
                var accept = new Button { Content = text.Get("Vcs.OpenForEdit") };
                var dialog = new Window
                {
                    Title = text.Get("Vcs.OpenForEdit"), Width = 480, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(16), Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = text.Get("Vcs.ConfirmEdit", provider.Name, _file), TextWrapping = TextWrapping.Wrap },
                            new WrapPanel { ItemSpacing = 8, Children = { cancel, accept } }
                        }
                    }
                };
                cancel.Click += (_, _) => dialog.Close(false);
                accept.Click += (_, _) => dialog.Close(true);
                if (!await dialog.ShowDialog<bool>(owner))
                {
                    return;
                }
                token.ThrowIfCancellationRequested();
                if (!_main.Extensions.VersionControlProviders.Contains(provider) || !File.Exists(_file))
                {
                    throw new InvalidOperationException(text.Get("Vcs.EditUnavailable"));
                }
                await edit.OpenForEditAsync(_file, token);
                token.ThrowIfCancellationRequested();
                _status.Text = text.Get("Vcs.EditOpened");
            }
            if (provider is IVersionControlWorkspaceProvider workspace)
            {
                var status = await workspace.GetWorkspaceStatusAsync(_file, token);
                token.ThrowIfCancellationRequested();
                if (!_main.Extensions.VersionControlProviders.Contains(provider))
                {
                    return;
                }
                _branch.Text = text.Get("Vcs.WorkspaceSummary", status.Branch ?? "—", status.Changes.Count);
                _workspaceChanges.ItemsSource = status.Changes.Select(change =>
                    $"{(change.PreviousRelativePath is null ? "" : change.PreviousRelativePath + " → ")}{change.RelativePath} · " +
                    text.Get("Vcs.ChangeColumns", text.Get("Vcs.Change." + change.Staged), text.Get("Vcs.Change." + change.Working)))
                    .ToArray();
                if (!openForEdit)
                {
                    _status.Text = _file;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.GetBaseException().Message; }
        finally
        {
            _busy = false;
            _provider.IsEnabled = _workspaceRefresh.IsEnabled = _openForEdit.IsEnabled = true;
        }
    }

    private async Task CompareAsync()
    {
        if (_busy || !_canCompare || _file is null || _lifetime is not { } lifetime ||
            _provider.SelectedItem is not IVersionControlProvider provider ||
            _before.SelectedItem is not Choice a || _after.SelectedItem is not Choice b)
        {
            return;
        }
        var token = lifetime.Token;
        _busy = true;
        _provider.IsEnabled = false;
        try
        {
            var before = await ReadAsync(a, provider, token);
            var after = await ReadAsync(b, provider, token);
            token.ThrowIfCancellationRequested();
            if (!_main.Extensions.VersionControlProviders.Contains(provider))
                throw new InvalidOperationException("The selected provider was disabled.");
            _beforeGraph = before;
            _afterGraph = after;
            _differences = GraphRevisionDiff.Compare(before, after);
            ShowScope([]);
            _changes.ItemsSource = _differences.Where(item => item.Kind != GraphDiffKind.Unchanged)
                .Select(item => new ChangeChoice(item, $"{LocalizationService.Shared.Get("Vcs." + item.Kind)}  " +
                    $"{item.After?.Name ?? item.Before?.Name ?? LocalizationService.Shared.Get("Vcs.GraphSettings")}  " +
                    $"[{ScopeLabel(item.Path.Split('/').SkipLast(1).Select(Guid.Parse).ToArray())}]")).ToArray();
            _status.Text = LocalizationService.Shared.Get("Vcs.Legend");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.GetBaseException().Message; }
        finally { _busy = false; _provider.IsEnabled = true; }
    }

    private string ScopeLabel(IReadOnlyList<Guid> path)
    {
        var labels = new List<string> { LocalizationService.Shared.Get("Vcs.RootGraph") };
        for (var index = 0; index < path.Count; ++index)
        {
            var parent = path.Take(index).ToArray();
            var node = GraphRevisionDiff.Scope(_afterGraph!, parent)?.Nodes.FirstOrDefault(item => item.Id == path[index]) ??
                GraphRevisionDiff.Scope(_beforeGraph!, parent)?.Nodes.FirstOrDefault(item => item.Id == path[index]);
            labels.Add(node?.Name ?? LocalizationService.Shared.Get("Vcs.GraphSettings"));
        }
        return string.Join(" / ", labels);
    }

    private void ShowScope(IReadOnlyList<Guid> path)
    {
        if (_beforeGraph is null || _afterGraph is null) return;
        var before = GraphRevisionDiff.Scope(_beforeGraph, path) ?? new([], []);
        var after = GraphRevisionDiff.Scope(_afterGraph, path) ?? new([], []);
        var prefix = string.Concat(path.Select(id => id.ToString("D") + "/"));
        var diff = _differences.Where(item => item.Path.StartsWith(prefix, StringComparison.Ordinal) &&
            !item.Path[prefix.Length..].Contains('/')).ToArray();
        _left.Show(before, diff, true, after);
        _right.Show(after, diff, false, before);
        _scope.Text = ScopeLabel(path);
    }

    private async Task<GraphSnapshot> ReadAsync(Choice choice, IVersionControlProvider provider, CancellationToken token)
    {
        if (choice.Revision is null)
        {
            var open = _document is not null && _main.Documents.Contains(_document) ? _document :
                _main.Documents.FirstOrDefault(document => Uri.TryCreate(document.FileKey, UriKind.Absolute, out var uri) && uri.IsFile &&
                    string.Equals(uri.LocalPath, _file, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (open is not null)
            {
                return open.Workspace.CreateSnapshot();
            }
            using var file = File.OpenRead(_file!);
            return await ReadGraphAsync(file);
        }
        var bytes = await provider.ReadRevisionAsync(_file!, choice.Revision, token);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Revision exceeds 16 MiB.");
        using var stream = new MemoryStream(bytes, writable: false);
        return await ReadGraphAsync(stream);
    }

    private async Task<GraphSnapshot> ReadGraphAsync(Stream stream) =>
        string.Equals(Path.GetExtension(_file), GroupAssetLibrary.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? GroupAssetLibrary.ReadAsset(stream).Graph
            : (await new ProjectFileService().LoadAsync(stream)).Graph;

    private sealed class RevisionGraphCanvas : Control
    {
        private GraphSnapshot? _graph;
        private Dictionary<Guid, GraphDiffKind> _changes = [];
        private GraphSnapshot _other = new([], []);
        public void Show(GraphSnapshot graph, IReadOnlyList<GraphNodeDifference> diff, bool beforeSide, GraphSnapshot other)
        {
            _graph = graph;
            _other = other;
            _changes = diff.Where(item => (beforeSide ? item.Before : item.After) is not null)
                .ToDictionary(item => (beforeSide ? item.Before : item.After)!.Id, item => item.Kind);
            InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_graph is not { Nodes.Count: > 0 } graph) return;
            if (Application.Current?.Resources.TryGetResource("EffectForegroundBrush", ActualThemeVariant, out var fg) != true ||
                fg is not IBrush foreground) return;
            var accent = Application.Current.Resources.TryGetResource("DockApplicationAccentBrushIndicator", ActualThemeVariant, out var ac) &&
                ac is IBrush brush ? brush : foreground;
            var all = graph.Nodes.Concat(_other.Nodes).ToArray();
            var minX = all.Min(node => node.X);
            var minY = all.Min(node => node.Y);
            var width = Math.Max(160, all.Max(node => node.X) - minX + 160);
            var height = Math.Max(70, all.Max(node => node.Y) - minY + 70);
            if (!double.IsFinite(width) || !double.IsFinite(height)) return;
            var scale = Math.Min(1, Math.Min(Math.Max(1, Bounds.Width - 24) / width, Math.Max(1, Bounds.Height - 24) / height));
            Point Position(GraphNodeSnapshot node) => new(12 + (node.X - minX) * scale, 12 + (node.Y - minY) * scale);
            var nodes = graph.Nodes.ToDictionary(node => node.Id);
            foreach (var edge in graph.Connections)
                if (nodes.TryGetValue(edge.SourceNodeId, out var source) && nodes.TryGetValue(edge.TargetNodeId, out var target))
                    context.DrawLine(_other.Connections.Contains(edge) ? new Pen(foreground, 1) :
                        new Pen(accent, 2, DashStyle.Dash), Position(source) + new Vector(140 * scale, 22 * scale),
                        Position(target) + new Vector(0, 22 * scale));
            foreach (var node in graph.Nodes)
            {
                var kind = _changes.GetValueOrDefault(node.Id);
                var position = Position(node);
                context.DrawRectangle(null, new Pen(kind == GraphDiffKind.Unchanged ? foreground : accent,
                    kind == GraphDiffKind.Unchanged ? 1 : 3), new Rect(position, new Size(140 * scale, 44 * scale)));
                var marker = kind switch { GraphDiffKind.Added => "+ ", GraphDiffKind.Removed => "- ", GraphDiffKind.Modified => "~ ", _ => "" };
                var label = new FormattedText(marker + node.Name, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, Typeface.Default, Math.Max(7, 11 * scale), foreground)
                    { MaxTextWidth = Math.Max(1, 132 * scale), MaxTextHeight = Math.Max(1, 40 * scale), Trimming = TextTrimming.CharacterEllipsis };
                context.DrawText(label, position + new Vector(4 * scale, 4 * scale));
            }
        }
    }
}
