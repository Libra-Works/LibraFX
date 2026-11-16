using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class MatrixInterfaceTests
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var model = new MainViewModel();
        var workspace = model.Graph;
        workspace.InitializeFromSnapshot(new([], []));
        var transform = workspace.CreateNode(new("Transform"), 40, 40);
        var point = workspace.CreateNode(new("TransformPoint"), 430, 40);
        var split = workspace.CreateNode(new("MatrixBreak"), 40, 300);
        var decompose = workspace.CreateNode(new("DecomposeTransform"), 430, 300);
        var graph = new GraphEditorView { DataContext = workspace };
        var inspector = new NodePropertiesView { DataContext = new NodePropertiesPaneViewModel(new(workspace)) };
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,360") };
        layout.Children.Add(graph);
        Grid.SetColumn(inspector, 1);
        layout.Children.Add(inspector);
        var window = new Window
        {
            Title = "LIBERA FX Matrix isolated UI verification",
            Width = 1360,
            Height = 820,
            Content = layout
        };
        var text = LocalizationService.Shared;
        var previousLanguage = text.SelectedLanguage;
        window.Show();
        window.Activate();
        try
        {
            Pump();
            Capture("matrix-before-connections.png");
            Connect(transform, "value", point, "matrix");
            Capture("matrix-after-first-connection.png");
            Connect(transform, "value", split, "matrix");
            Require(workspace.Connections.Count == 2 && workspace.TypeProblems.Count == 0,
                $"Real pin clicks must connect the dedicated Matrix outputs without type errors: {workspace.Connections.Count}, {workspace.ConnectionStatus}.");
            Require(workspace.History.Undo() && workspace.Connections.Count == 1,
                "A real Matrix pin connection must undo.");
            Require(workspace.History.Redo() && workspace.Connections.Count == 2,
                "A real Matrix pin connection must redo.");
            Pump();
            Connect(transform, "value", decompose, "matrix");
            var decomposeView = graph.GetVisualDescendants().OfType<GraphNodeView>()
                .Single(view => view.DataContext is GraphNodeViewModel node && node.Id == decompose.Id);
            foreach (var pin in decomposeView.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Tag is GraphOutputPortDefinition))
            {
                var center = pin.TranslatePoint(new Point(pin.Bounds.Width / 2, pin.Bounds.Height / 2), decomposeView);
                Require(center is { } position && position.X > 0 && position.X < decomposeView.Bounds.Width &&
                    position.Y > 0 && position.Y < decomposeView.Bounds.Height,
                    "Long Decompose Transform output labels must not push their pins outside the node.");
            }
            Connect(decompose, "rotation", point, "point");
            Require(workspace.Connections.Count == 4 && workspace.TypeProblems.Count == 0,
                "The long rotation output must be connectable using actual native pointer input.");
            var rotationConnection = workspace.Connections.Single(connection => connection.Source.Id == decompose.Id);
            Require(rotationConnection.Description.Contains("[Rotation", StringComparison.Ordinal) &&
                !rotationConnection.Description.Contains("[rotation]", StringComparison.Ordinal) &&
                rotationConnection.SourcePortId == "rotation",
                "Inspector connection descriptions must show human-readable pin labels without changing saved output IDs.");
            workspace.SelectNode(workspace.GetNode(transform.Id));
            Pump();
            Capture("matrix-authoring-en.png");
            text.SelectedLanguage = text.Languages.Single(language => language.Id == "ja");
            Pump();
            Capture("matrix-authoring-ja.png");
            var catalog = new NodeCatalogViewModel(new(workspace)) { SearchText = "Transform" };
            Require(catalog.Groups.SelectMany(group => group.Nodes).Any(node => node.KindName == "Transform"),
                "The English Transform alias must remain searchable under Japanese labels.");
            Require(inspector.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text.Get("Matrix.Parameter.Scale")),
                "The actual inspector must show localized Transform parameters.");
            Require(inspector.GetVisualDescendants().OfType<TextBlock>().Any(block =>
                ToolTip.GetTip(block)?.ToString() == text.Get("Matrix.Parameter.Rotation")),
                "The compact rotation label must expose its complete axis convention as a localized tooltip.");
            var group = workspace.CreateNode(GraphNodeKind.GraphGroup, 40, 540);
            workspace.EnterGraph(group);
            var groupInput = workspace.CreateNode(new GraphNodeKind("GroupInputMatrix"));
            var groupOutput = workspace.CreateNode(new GraphNodeKind("GroupOutputMatrix"));
            Require(workspace.TryConnectNodes(groupInput, groupOutput, out _),
                "The Group Matrix boundaries must connect internally.");
            workspace.NavigateToGraph(0);
            group = workspace.GetNode(group.Id);
            transform = workspace.GetNode(transform.Id);
            var direction = workspace.CreateNode(new GraphNodeKind("TransformDirection"), 430, 540);
            Pump();
            Connect(transform, "value", group, GroupBoundarySchema.InputPortId(groupInput.Id));
            Connect(group, GroupBoundarySchema.OutputPortId(groupOutput.Id), direction, "matrix");
            Require(workspace.Connections.Count == 6 && workspace.TypeProblems.Count == 0,
                "Real pin clicks must connect both sides of the Group Matrix boundary.");
            Require(workspace.History.Undo() && workspace.Connections.Count == 5 &&
                workspace.History.Redo() && workspace.Connections.Count == 6,
                "A Group Matrix connection must undo and redo.");
            Pump();
            Capture("matrix-group-boundary-ja.png");
            File.WriteAllText(Path.Combine(directory, "matrix-ui-observations.json"),
                JsonSerializer.Serialize(new
                {
                    actualNativePointerClicks = 12,
                    nativeWindowInput = true,
                    connections = workspace.Connections.Count,
                    undoRedo = true,
                    japaneseInspector = true,
                    englishSearchAlias = true,
                    visibleOutputPins = split.OutputPorts.Select(port => port.Id).ToArray(),
                    matrixPins = transform.OutputPorts.Select(port => port.PortType).ToArray()
                }));
            workspace.DeleteNodes(workspace.Nodes.ToArray());
            var rotatePoint = workspace.CreateNode(new GraphNodeKind("RotatePoint"), 40, 100);
            var translatePoint = workspace.CreateNode(new GraphNodeKind("TranslatePoint"), 430, 100);
            Pump();
            Connect(rotatePoint, "value", translatePoint, "point");
            workspace.SelectNode(rotatePoint);
            Pump();
            Require(workspace.Connections.Count == 1 && workspace.TypeProblems.Count == 0,
                "The point operation pins must connect through real pointer input.");
            var pointCatalog = new NodeCatalogViewModel(new(workspace)) { SearchText = "Rotate Point" };
            Require(pointCatalog.Groups.SelectMany(group => group.Nodes).Any(node => node.KindName == "RotatePoint"),
                "Rotate Point must be searchable by its English name in Japanese.");
            Require(inspector.GetVisualDescendants().OfType<TextBlock>().Any(block =>
                ToolTip.GetTip(block)?.ToString() == text.Get("Matrix.Parameter.Rotation")),
                "Rotate Point must expose the full localized rotation convention.");
            Capture("point-operations-ja.png");
            workspace.DeleteNodes(workspace.Nodes.ToArray());
            var signalGroup = workspace.CreateNode(GraphNodeKind.GraphGroup, 40, 180);
            workspace.EnterGraph(signalGroup);
            var signalOutput = workspace.CreateNode(new GraphNodeKind("GroupOutputSignal"));
            workspace.NavigateToGraph(0);
            signalGroup = workspace.GetNode(signalGroup.Id);
            var firstTarget = workspace.CreateNode(GraphNodeKind.SpawnableGroup, 430, 40);
            var secondTarget = workspace.CreateNode(GraphNodeKind.SpawnableGroup, 430, 310);
            Pump();
            var outputPort = GroupBoundarySchema.OutputPortId(signalOutput.Id);
            Connect(signalGroup, outputPort, firstTarget, "spawn");
            Connect(signalGroup, outputPort, secondTarget, "spawn");
            workspace.SelectNode(signalGroup);
            Pump();
            Require(workspace.Connections.Select(connection => connection.SignalOrder).SequenceEqual([1, 2]) &&
                workspace.SelectedConnections.All(connection => connection.Description.Contains("Signal実行順", StringComparison.Ordinal)) &&
                inspector.GetVisualDescendants().OfType<TextBlock>().Any(block =>
                    ToolTip.GetTip(block)?.ToString() == text.Get("Editor.SignalExecutionOrderHelp")),
                "Signal fan-out order must be visible and localized in the real inspector.");
            Capture("signal-fanout-ja.png");
            Require(workspace.History.Undo() && workspace.Connections.Single().SignalOrder == 0 &&
                workspace.History.Redo() && workspace.Connections.Select(connection => connection.SignalOrder).SequenceEqual([1, 2]),
                "Signal order badges must update after undo and redo.");
        }
        finally
        {
            text.SelectedLanguage = previousLanguage;
            window.Close();
            model.DisposeStorageFiles();
        }

        void Connect(GraphNodeViewModel source, string output, GraphNodeViewModel target, string input)
        {
            var sourceView = graph.GetVisualDescendants().OfType<GraphNodeView>()
                .Single(view => view.DataContext is GraphNodeViewModel node && node.Id == source.Id);
            var outputControl = sourceView.GetVisualDescendants().OfType<Border>()
                .Single(border => border.Tag is GraphOutputPortDefinition port && port.Id == output);
            Click(outputControl);
            var targetView = graph.GetVisualDescendants().OfType<GraphNodeView>()
                .Single(view => view.DataContext is GraphNodeViewModel node && node.Id == target.Id);
            var inputControl = targetView.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Tag is GraphInputPortDefinition port && port.Id == input);
            Click(inputControl);
        }

        void Capture(string name)
        {
            window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)layout.Bounds.Width, (int)layout.Bounds.Height));
            bitmap.Render(layout);
            bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }
    }

    private static void Click(Control control)
    {
        Require(control.IsEffectivelyVisible && control.Bounds.Width > 0, "A requested pin must be visible and measurable.");
        var window = TopLevel.GetTopLevel(control) as Window;
        var handle = window?.TryGetPlatformHandle()?.Handle ?? 0;
        Require(handle != 0, "Native pointer input requires the isolated verification window.");
        var position = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        // Target only this native window; never move the global pointer on a shared desktop.
        var client = new NativePoint { X = position.X, Y = position.Y };
        Require(ScreenToClient(handle, ref client), "The isolated window must have valid client coordinates.");
        var coordinates = (nint)((client.Y << 16) | (client.X & 0xffff));
        SendMessage(handle, 0x0200, 0, coordinates);
        SendMessage(handle, 0x0201, 1, coordinates);
        Pump();
        SendMessage(handle, 0x0202, 0, coordinates);
        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.UIThread.PushFrame(frame);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nuint buttons, nint coordinates);
}
