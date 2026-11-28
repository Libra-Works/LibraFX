using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Samples;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Panels;
using LiberaFx.Views.Controls;
using LiberaFx.Views.Graph;
using LiberaFx.Views.Panels;

internal static class StandardModuleInterfaceTests
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        _ = new ThemeSettingsService();
        var profiles = new TeamProfilesService(Path.Combine(directory, "profiles.json"));
        using var catalog = new GroupAssetCatalog(profiles);
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var active = new ActiveGraphProvider(workspace);
        var list = new NodeCatalogView { DataContext = new NodeCatalogViewModel(active, catalog) };
        var editor = new GraphEditorView { DataContext = workspace };
        var pane = new NodePropertiesPaneViewModel(active);
        var inspector = new NodePropertiesView { DataContext = pane };
        var blur = new Button { Content = "Commit" };
        var right = new Grid { RowDefinitions = new("*,Auto") };
        right.Children.Add(inspector);
        Grid.SetRow(blur, 1);
        right.Children.Add(blur);
        var content = new Grid { ColumnDefinitions = new("260,*,380") };
        content.Children.Add(list);
        Grid.SetColumn(editor, 1); content.Children.Add(editor);
        Grid.SetColumn(right, 2); content.Children.Add(right);
        var window = new Window { Width = 1500, Height = 920, Content = content };
        var text = LocalizationService.Shared;
        var language = text.SelectedLanguage;
        var nodeLanguage = text.SelectedNodeLanguage;
        window.Show();
        window.Activate();
        try
        {
            text.SelectedLanguage = text.Languages.Single(item => item.Id == "en");
            text.SelectedNodeLanguage = text.Languages.Single(item => item.Id == "en");
            Pump(window);
            list.GetVisualDescendants().OfType<TextBox>().Single().Text = "Tree Billboard";
            Pump(window);
            var row = list.GetVisualDescendants().OfType<Border>().Single(control =>
                control.DataContext is NodeCatalogEntryViewModel { GroupAssetId: not null } &&
                control.Tag is string identifier && identifier.StartsWith(NodeCatalogEntryViewModel.GroupPrefix));
            Drag(row, editor);
            Pump(window);
            Require(workspace.Nodes.Count == 1, "Dragging the actual standard-library search row must insert one ordinary Group.");
            var group = workspace.Nodes.Single();
            var id = group.Id;
            var assetId = group.GroupAsset!.Id;
            workspace.SelectNode(group);
            Pump(window);
            Require(pane.IsStandardGroup && pane.Parameters.Count == 8 && !pane.CanCloneGroup,
                "A fresh project's actual inspector must show eight published fields and disable cloning without a project library.");
            var view = editor.GetVisualDescendants().OfType<GraphNodeView>().Single();
            Require(view.GetVisualDescendants().OfType<Border>().Count(border => border.Tag is GraphOutputPortDefinition) == 2,
                "World Matrix and Valid must be actual visible output pins.");
            var target = group.ChildGraph!.ExposedItems!.Single(item => item.Name == "Target");
            var metadata = catalog.Metadata(assetId)!.Parameters.Single(item => item.ItemId == target.Id);
            var parameter = pane.Parameters.Single(item => item.Description == text.Get(metadata.DescriptionKey));
            var host = inspector.GetVisualDescendants().OfType<ParameterEditorHost>()
                .Single(item => ReferenceEquals(item.DataContext, parameter));
            var box = host.GetVisualDescendants().OfType<TextBox>().First();
            Click(box, window);
            box.SelectAll();
            SendMessage(window.TryGetPlatformHandle()!.Handle, 0x0102, (nuint)'2', 0);
            Pump(window);
            Click(blur, window);
            Require(workspace.GetNode(id).GroupOverrides?.Single().Components.Single().Value?.EnumerateArray().First().GetDouble() == 2,
                "Editing the real locked standard-Group inspector must change its instance Target, not the shared asset.");
            Require(workspace.History.Undo() && workspace.History.Redo(), "Actual inspector edits must Undo/Redo.");
            Capture("standard-library-en.png");
            text.SelectedLanguage = text.Languages.Single(item => item.Id == "ja");
            Pump(window);
            Require(pane.Parameters.Any(item => item.Description == text.Get(metadata.DescriptionKey)) &&
                list.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Text ==
                    "Tree Billboard"),
                "The module name must stay English while parameter descriptions switch to Japanese.");
            Capture("standard-library-ja.png");
            Require(profiles.Save(null, "Isolated module review", Path.Combine(directory, "project-library")), profiles.Status);
            Pump(window);
            var clone = inspector.GetVisualDescendants().OfType<Button>().Single(button =>
                ReferenceEquals(button.Command, pane.CloneGroupCommand) && button.IsEffectivelyVisible);
            new ButtonAutomationPeer(clone).GetProvider<IInvokeProvider>()!.Invoke();
            Pump(window);
            Require(workspace.GetNode(id).GroupAsset?.Id != assetId &&
                workspace.EnterGraph(workspace.GetNode(id)), "The actual Clone button must produce an editable independent project asset.");
            File.WriteAllText(Path.Combine(directory, "module-ui-observations.json"), JsonSerializer.Serialize(new
            {
                nativeSearchDragDrop = true, visibleMatrixAndValidPins = true, publishedFields = 8,
                lockedInstanceEditable = true, undoRedo = true, localizedSearchAndDescriptions = true,
                explicitClone = true
            }));
        }
        finally
        {
            text.SelectedNodeLanguage = nodeLanguage;
            text.SelectedLanguage = language;
            window.Close();
        }

        void Capture(string name)
        {
            Pump(window);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }
    }

    private static void Drag(Control source, Control target)
    {
        var from = source.PointToScreen(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2));
        var to = target.PointToScreen(new Point(target.Bounds.Width * 0.4, target.Bounds.Height * 0.35));
        var frame = new DispatcherFrame();
        var driver = Task.Run(() =>
        {
            try
            {
                SetCursorPos(from.X, from.Y);
                Thread.Sleep(150);
                MouseEvent(0x0002, 0, 0, 0, 0);
                for (var step = 1; step <= 20; step++)
                {
                    SetCursorPos(from.X + (to.X - from.X) * step / 20, from.Y + (to.Y - from.Y) * step / 20);
                    Thread.Sleep(30);
                }
                Thread.Sleep(150);
            }
            finally
            {
                MouseEvent(0x0004, 0, 0, 0, 0);
                Thread.Sleep(300);
                Dispatcher.UIThread.Post(() => frame.Continue = false);
            }
        });
        Dispatcher.UIThread.PushFrame(frame);
        driver.GetAwaiter().GetResult();
    }

    private static void Pump(Window window)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.UIThread.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void Click(Control control, Window window)
    {
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        var client = new NativePoint { X = point.X, Y = point.Y };
        var handle = window.TryGetPlatformHandle()!.Handle;
        Require(ScreenToClient(handle, ref client), "The isolated window must have native input coordinates.");
        var position = (nint)((client.Y << 16) | (client.X & 0xffff));
        SendMessage(handle, 0x0200, 0, position);
        SendMessage(handle, 0x0201, 1, position);
        Pump(window);
        SendMessage(handle, 0x0202, 0, position);
        Pump(window);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint handle, ref NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint handle, uint message, nuint wParam, nint lParam);
}
