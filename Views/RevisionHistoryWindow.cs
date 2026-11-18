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
    private readonly TextBlock _scope = new();
    private GraphSnapshot? _beforeGraph, _afterGraph;
    private IReadOnlyList<GraphNodeDifference> _differences = [];
    private CancellationTokenSource? _lifetime;
    private bool _busy;
    private sealed record Choice(string? Revision, string Label)
    { public override string ToString() => Label; }
    private sealed record ChangeChoice(GraphNodeDifference Difference, string Label)
    { public override string ToString() => Label; }

    public RevisionHistoryWindow(MainViewModel main)
    {
        _main = main;
        _document = main.ActiveDocument;
        if (Uri.TryCreate(_document?.FileKey, UriKind.Absolute, out var uri) && uri.IsFile)
            _file = uri.LocalPath;
        var text = LocalizationService.Shared;
        _provider.ItemsSource = main.Extensions.VersionControlProviders;
        _provider.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
        _provider.SelectedIndex = _provider.ItemCount > 0 ? 0 : -1;
        Avalonia.Automation.AutomationProperties.SetName(_provider, text.Get("Vcs.Provider"));
        Avalonia.Automation.AutomationProperties.SetName(_before, text.Get("Vcs.Before"));
        Avalonia.Automation.AutomationProperties.SetName(_after, text.Get("Vcs.After"));
        ToolTip.SetTip(_before, text.Get("Vcs.Before"));
        ToolTip.SetTip(_after, text.Get("Vcs.After"));
        var refresh = new Button { Content = text.Get("Vcs.History") };
        var compare = new Button { Content = text.Get("Vcs.Compare") };
        refresh.Click += async (_, _) => await LoadHistoryAsync();
        compare.Click += async (_, _) => await CompareAsync();
        var controls = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _provider, refresh, _before, _after, compare }
        };
        var graphs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        graphs.Children.Add(_left);
        Grid.SetColumn(_right, 1);
        graphs.Children.Add(_right);
        var root = new Grid { Margin = new Thickness(12), MinHeight = 500, RowDefinitions = new RowDefinitions("Auto,Auto,*,150,Auto"), RowSpacing = 8 };
        root.Children.Add(controls);
        Grid.SetRow(_scope, 1);
        root.Children.Add(_scope);
        Grid.SetRow(graphs, 2);
        root.Children.Add(graphs);
        Grid.SetRow(_changes, 3);
        root.Children.Add(_changes);
        Grid.SetRow(_status, 4);
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root };
        _provider.SelectionChanged += (_, _) =>
        {
            _before.ItemsSource = null;
            _after.ItemsSource = null;
            _changes.ItemsSource = null;
            _beforeGraph = _afterGraph = null;
            _scope.Text = "";
            _left.Show(new([], []), [], true, new([], []));
            _right.Show(new([], []), [], false, new([], []));
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
        _status.Text = _file is null ? text.Get("Vcs.SaveFirst") : _file;
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
            _after.ItemsSource = new[] { new Choice(null, LocalizationService.Shared.Get("Vcs.Working")) }.Concat(choices).ToArray();
            _before.SelectedIndex = choices.Length > 0 ? 0 : -1;
            _after.SelectedIndex = 0;
            _status.Text = LocalizationService.Shared.Get("Vcs.ReadOnly");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.GetBaseException().Message; }
        finally { _busy = false; _provider.IsEnabled = true; }
    }

    private async Task CompareAsync()
    {
        if (_busy || _file is null || _document is null || _lifetime is not { } lifetime ||
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
            if (!_main.Documents.Contains(_document!)) throw new InvalidOperationException("The document was closed.");
            return _document!.Workspace.CreateSnapshot();
        }
        var bytes = await provider.ReadRevisionAsync(_file!, choice.Revision, token);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Revision exceeds 16 MiB.");
        using var stream = new MemoryStream(bytes, writable: false);
        return (await new ProjectFileService().LoadAsync(stream)).Graph;
    }

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
