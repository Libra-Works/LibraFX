using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.Views;

internal sealed class GraphComparisonView : UserControl
{
    private readonly GraphCanvas _left = new() { ClipToBounds = true };
    private readonly GraphCanvas _right = new() { ClipToBounds = true };
    private readonly ListBox _changes = new();
    private readonly TextBlock _scope = new();
    private GraphSnapshot? _before;
    private GraphSnapshot? _after;
    private IReadOnlyList<GraphNodeDifference> _differences = [];

    private sealed record ChangeChoice(Guid[] Scope, string Label)
    {
        public override string ToString() => Label;
    }

    public GraphComparisonView()
    {
        var graphs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        graphs.Children.Add(_left);
        Grid.SetColumn(_right, 1);
        graphs.Children.Add(_right);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,130"), RowSpacing = 6 };
        grid.Children.Add(_scope);
        Grid.SetRow(graphs, 1);
        grid.Children.Add(graphs);
        Grid.SetRow(_changes, 2);
        grid.Children.Add(_changes);
        Content = grid;
        _changes.SelectionChanged += (_, _) =>
        {
            if (_changes.SelectedItem is ChangeChoice choice)
            {
                ShowScope(choice.Scope);
            }
        };
    }

    public bool ShowComparison(GraphSnapshot before, GraphSnapshot after)
    {
        _before = before;
        _after = after;
        _differences = GraphRevisionDiff.Compare(before, after);
        var text = LocalizationService.Shared;
        var changes = _differences.Where(item => item.Kind != GraphDiffKind.Unchanged).Select(item =>
        {
            var scope = item.Path.Split('/').SkipLast(1).Select(Guid.Parse).ToArray();
            var name = item.After?.Name ?? item.Before?.Name ?? text.Get("Vcs.GraphSettings");
            return new ChangeChoice(scope, $"{text.Get("Vcs." + item.Kind)}  {name}  [{ScopeLabel(scope)}]");
        }).ToArray();
        _changes.ItemsSource = changes;
        ShowScope([]);
        return changes.Length != 0;
    }

    public void Clear()
    {
        _before = _after = null;
        _differences = [];
        _changes.ItemsSource = null;
        _scope.Text = "";
        var empty = new GraphSnapshot([], []);
        _left.Show(empty, [], true, empty);
        _right.Show(empty, [], false, empty);
    }

    private string ScopeLabel(IReadOnlyList<Guid> path)
    {
        var text = LocalizationService.Shared;
        var labels = new List<string> { text.Get("Vcs.RootGraph") };
        for (var index = 0; index < path.Count; ++index)
        {
            var parent = path.Take(index).ToArray();
            var node = GraphRevisionDiff.Scope(_after!, parent)?.Nodes.FirstOrDefault(item => item.Id == path[index]) ??
                GraphRevisionDiff.Scope(_before!, parent)?.Nodes.FirstOrDefault(item => item.Id == path[index]);
            labels.Add(node?.Name ?? text.Get("Vcs.GraphSettings"));
        }
        return string.Join(" / ", labels);
    }

    private void ShowScope(IReadOnlyList<Guid> path)
    {
        if (_before is null || _after is null)
        {
            return;
        }
        var before = GraphRevisionDiff.Scope(_before, path) ?? new([], []);
        var after = GraphRevisionDiff.Scope(_after, path) ?? new([], []);
        var prefix = string.Concat(path.Select(id => id.ToString("D") + "/"));
        var diff = _differences.Where(item => item.Path.StartsWith(prefix, StringComparison.Ordinal) &&
            !item.Path[prefix.Length..].Contains('/')).ToArray();
        _left.Show(before, diff, true, after);
        _right.Show(after, diff, false, before);
        _scope.Text = ScopeLabel(path);
    }

    private sealed class GraphCanvas : Control
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
            if (_graph is not { Nodes.Count: > 0 } graph ||
                Application.Current?.Resources.TryGetResource("EffectForegroundBrush", ActualThemeVariant, out var fg) != true ||
                fg is not IBrush foreground)
            {
                return;
            }
            var accent = Application.Current.Resources.TryGetResource("DockApplicationAccentBrushIndicator", ActualThemeVariant, out var ac) &&
                ac is IBrush brush ? brush : foreground;
            var all = graph.Nodes.Concat(_other.Nodes).ToArray();
            var minX = all.Min(node => node.X);
            var minY = all.Min(node => node.Y);
            var width = Math.Max(160, all.Max(node => node.X) - minX + 160);
            var height = Math.Max(70, all.Max(node => node.Y) - minY + 70);
            if (!double.IsFinite(width) || !double.IsFinite(height))
            {
                return;
            }
            var scale = Math.Min(1, Math.Min(Math.Max(1, Bounds.Width - 24) / width, Math.Max(1, Bounds.Height - 24) / height));
            Point Position(GraphNodeSnapshot node) => new(12 + (node.X - minX) * scale, 12 + (node.Y - minY) * scale);
            var nodes = graph.Nodes.ToDictionary(node => node.Id);
            var unchangedEdge = new Pen(foreground, 1);
            var changedEdge = new Pen(accent, 2, DashStyle.Dash);
            var otherEdges = _other.Connections.ToHashSet();
            foreach (var edge in graph.Connections)
            {
                if (nodes.TryGetValue(edge.SourceNodeId, out var source) && nodes.TryGetValue(edge.TargetNodeId, out var target))
                {
                    context.DrawLine(otherEdges.Contains(edge) ? unchangedEdge : changedEdge,
                        Position(source) + new Vector(140 * scale, 22 * scale), Position(target) + new Vector(0, 22 * scale));
                }
            }
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
