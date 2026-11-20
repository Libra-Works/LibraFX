using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Appearance;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class EditorInterfaceTests
{
    public static void VerifyConstantEditors()
    {
        foreach (var (kind, count) in new[] { ("Float", 1), ("Int32", 1), ("FloatX2", 2), ("FloatX3", 3), ("FloatX4", 4) })
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var node = graph.CreateNode(new GraphNodeKind(kind));
            var view = new GraphNodeView { DataContext = node };
            var leave = new Button { Content = "Commit" };
            var window = new Window { Width = 420, Height = 320,
                Content = new StackPanel { Children = { view, leave } } };
            window.Show();
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var fields = view.GetVisualDescendants().OfType<ParameterEditorHost>()
                    .Where(host => host.Compact).SelectMany(host => host.GetVisualDescendants().OfType<TextBox>()).ToArray();
                if (fields.Length != count || fields.Any(field => !field.IsEffectivelyVisible || !field.IsEnabled) ||
                    view.FindControl<ToggleButton>("DetailsToggle")!.IsVisible || node.HasLegacyInput)
                    throw new InvalidOperationException(kind + " must show editable values without expanding details or exposing an input.");
                var before = graph.History.CurrentNodeId;
                fields[^1].Focus();
                fields[^1].SetCurrentValue(TextBox.TextProperty, "7");
                leave.Focus();
                Dispatcher.UIThread.RunJobs();
                var definition = node.Definition.Parameters.Single(p => p.Id == "value");
                var value = node.GetParameterValue(definition);
                if ((count == 1 ? value.GetDouble() : value[count - 1].GetDouble()) != 7 ||
                    graph.History.CurrentNodeId == before)
                    throw new InvalidOperationException(kind + " inline editing did not commit through document history.");
                graph.History.Undo();
                node = graph.GetNode(node.Id);
                value = node.GetParameterValue(definition);
                if ((count == 1 ? value.GetDouble() : value[count - 1].GetDouble()) != 0)
                    throw new InvalidOperationException(kind + " inline edit cannot be undone.");
                graph.History.Redo();
                node = graph.GetNode(node.Id);
                view.DataContext = node;
                graph.SetParameterLocked(node, "value", true);
                Dispatcher.UIThread.RunJobs();
                if (view.GetVisualDescendants().OfType<ParameterEditorHost>().Where(host => host.Compact)
                    .SelectMany(host => host.GetVisualDescendants().OfType<TextBox>()).Any(field => field.IsEffectivelyEnabled))
                    throw new InvalidOperationException(kind + " inline editor must respect parameter locks.");
            }
            finally { window.Close(); }
        }
    }

    public static void Run()
    {
        VerifyConstantEditors();
        VerifyGraphToolbars();
        VerifyParameterRows();
        VerifyContainerPresentation();
        VerifyLifecycleHelp();
        VerifySpawnInputBindingEditor();
        VerifyCatalogs();
        var text = LocalizationService.Shared;
        var originalLanguage = text.SelectedLanguage;
        var originalNodeLanguage = text.SelectedNodeLanguage;
        var root = Path.Combine(AppDataDirectory.Root, "inspector-tests");
        var directory = Path.Combine(root, "groups");
        var profilesPath = Path.Combine(root, "profiles.json");
        var profiles = new TeamProfilesService(profilesPath);
        var originalProfile = profiles.Active.Id;
        if (!profiles.Save(null, "Inspector", directory)) throw new InvalidOperationException(profiles.Status);
        using var catalog = new GroupAssetCatalog(profiles);
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var provider = new ActiveGraphProvider(graph);
        var pane = new NodePropertiesPaneViewModel(provider);
        var nodeList = new NodeCatalogViewModel(provider, catalog);
        var parameterGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var parameterNode = parameterGraph.CreateNode(GraphNodeKind.Float);
        var parameter = new NodeParameterEditorViewModel(parameterGraph, parameterNode, parameterNode.Definition.Parameters.Single());
        var parameterNameRefreshed = false;
        parameter.PropertyChanged += (_, e) => parameterNameRefreshed |= e.PropertyName == nameof(parameter.Name);
        var view = new NodePropertiesView { DataContext = pane };
        var window = new Window { Content = view };
        Guid assetId = Guid.Empty;
        window.Show();
        try
        {
            var group = graph.CreateNode(GraphNodeKind.GraphGroup);
            if (!pane.IsGroup || !pane.SaveGroupCommand.CanExecute(null) || pane.ToggleGroupLockCommand.CanExecute(null))
                throw new InvalidOperationException("Inspector group actions have incorrect availability.");
            pane.SaveGroupCommand.Execute(null);
            assetId = group.GroupAsset?.Id ?? throw new InvalidOperationException("Inspector save did not create an asset.");
            if (!File.Exists(Path.Combine(directory, assetId.ToString("N") + ".lfxgroup")) ||
                !pane.ToggleGroupLockCommand.CanExecute(null))
                throw new InvalidOperationException("Saved group is not reusable or lockable.");

            var save = view.FindControl<IconButton>("SaveGroupButton")!;
            var toggle = view.FindControl<IconButton>("GroupLockButton")!;
            pane.ToggleGroupLockCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            if (!pane.IsGroupLocked || pane.SaveGroupCommand.CanExecute(null) ||
                toggle.Icon != EditorIcon.Lock || graph.EnterGraph(group))
                throw new InvalidOperationException("Inspector did not reflect the locked asset.");

            var before = graph.CreateSnapshot();
            text.SelectedLanguage = text.Languages.Single(language => language.Id == "ja");
            text.SelectedNodeLanguage = text.SelectedLanguage;
            Dispatcher.UIThread.RunJobs();
            if (AutomationProperties.GetName(save) != text.Get("Editor.SaveGroup") ||
                AutomationProperties.GetName(toggle) != text.Get("Editor.UnlockGroup") ||
                ToolTip.GetTip(save)?.ToString() != text.Get("Editor.SaveGroup") ||
                pane.GroupRevision != text.Get("Editor.GroupRevision", 1) ||
                !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
                throw new InvalidOperationException("Live translation changed graph data or missed inspector accessibility text.");
            nodeList.SearchText = "Scalar";
            if (!nodeList.Groups.SelectMany(category => category.Nodes)
                    .Any(entry => entry.KindName == "Float" && entry.Name == text.Get("Editor.Node.Scalar")) ||
                parameter.Name != text.Get("Editor.Parameter.Value") || !parameterNameRefreshed ||
                parameterNode.Name != parameterNode.Definition.Name)
                throw new InvalidOperationException(
                    $"Translated catalog/inspector mismatch: entries={string.Join(", ", nodeList.Groups.SelectMany(category => category.Nodes).Select(entry => $"{entry.KindName}:{entry.Name}"))}; " +
                    $"parameter={parameter.Name}/{text.Get("Editor.Parameter.Value")}; refreshed={parameterNameRefreshed}; " +
                    $"savedName={parameterNode.Name}/{parameterNode.Definition.Name}; originalLanguage={originalLanguage.Id}.");
            using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppDataDirectory.Root, "LiberaFx", "language.json"))))
                if (settings.RootElement.GetProperty("Language").GetString() != "ja")
                    throw new InvalidOperationException("The selected language was not saved.");

            pane.ToggleGroupLockCommand.Execute(null);
            if (pane.IsGroupLocked || !pane.SaveGroupCommand.CanExecute(null) || !graph.EnterGraph(group))
                throw new InvalidOperationException("Inspector unlock did not restore editing.");
            graph.CreateNode(GraphNodeKind.Time);
            graph.NavigateToGraph(0);
            graph.SelectNode(graph.Nodes.Single());
            pane.SaveGroupCommand.Execute(null);
            if (new GroupAssetLibrary(directory).Load(assetId).Revision != 2)
                throw new InvalidOperationException("Inspector save did not publish group edits.");

            profiles.Select(originalProfile);
            if (pane.SaveGroupCommand.CanExecute(null) || pane.ToggleGroupLockCommand.CanExecute(null) ||
                graph.Nodes.Single().GroupAsset?.Id != assetId)
                throw new InvalidOperationException("Project switching changed embedded data or allowed missing-asset actions.");
            graph.CreateNode(GraphNodeKind.Float);
            if (pane.IsGroup || pane.SaveGroupCommand.CanExecute(null))
                throw new InvalidOperationException("Non-group nodes expose group save actions.");
            foreach (var icon in Enum.GetValues<EditorIcon>())
                if (new IconButton { Icon = icon }.Content is not Avalonia.Controls.Shapes.Path { Data: not null })
                    throw new InvalidOperationException($"Icon {icon} has no vector geometry.");
        }

        finally
        {
            text.SelectedLanguage = originalLanguage;
            text.SelectedNodeLanguage = originalNodeLanguage;
            window.Close();
            if (File.Exists(profilesPath)) File.Delete(profilesPath);
            if (assetId != Guid.Empty)
            {
                var name = assetId.ToString("N");
                foreach (var path in new[] {
                    Path.Combine(directory, name + ".lfxgroup"), Path.Combine(directory, "." + name + ".lock"),
                    Path.Combine(directory, ".versions", name, "1.lfxgroup") })
                    if (File.Exists(path)) File.Delete(path);
                var archived = Path.Combine(directory, ".versions", name);
                if (Directory.Exists(archived)) Directory.Delete(archived);
                var versions = Path.Combine(directory, ".versions");
                if (Directory.Exists(versions)) Directory.Delete(versions);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
        VerifySettingsBindingLifetime();
    }

    public static void VerifyGraphToolbars()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        graph.CreateNode(GraphNodeKind.Float);
        var view = new GraphEditorView { DataContext = graph };
        var window = new Window { Width = 950, Height = 650, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var toolbar = view.FindControl<Border>("GraphToolbar")!;
            var viewport = view.FindControl<ScrollViewer>("GraphScrollViewer")!;
            if (toolbar.Bounds.Top < viewport.Bounds.Bottom - 1 ||
                toolbar.GetVisualDescendants().OfType<IconButton>().Any(button => button.Icon == EditorIcon.Delete) ||
                !graph.DeleteSelectedNodeCommand.CanExecute(null))
                throw new InvalidOperationException("Graph controls must be below the viewport without removing selection deletion.");
            var gpu = new GpuWorkspaceView();
            window.Content = gpu;
            var container = GpuParticleEditorService.Create(graph);
            gpu.Open(graph, container.Id);
            try
            {
                gpu.Workspace!.EnterKernel(gpu.Workspace.State.Program.Kernels[0].Id);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var gpuToolbar = gpu.FindControl<StackPanel>("KernelToolbar")!;
                var gpuViewport = gpu.FindControl<ScrollViewer>("GpuGraphScrollViewer")!;
                if (gpuToolbar.Bounds.Top < gpuViewport.Bounds.Bottom - 1)
                    throw new InvalidOperationException("GPU graph controls must be below the viewport too.");
            }
            finally { gpu.CloseWorkspace(); }
        }
        finally { window.Close(); }
    }

    public static void VerifyContainerPresentation()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var scalar = graph.CreateNode(GraphNodeKind.Float);
        var gpu = graph.CreateNode(new GraphNodeKind("GpuParticle"));
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        var target = graph.CreateNode(GraphNodeKind.Multiply);
        var before = graph.CreateSnapshot();
        var scalarView = new GraphNodeView { DataContext = scalar };
        var gpuView = new GraphNodeView { DataContext = gpu };
        var spawnView = new GraphNodeView { DataContext = spawnable };
        var window = new Window { Content = new StackPanel { Children = { scalarView, gpuView, spawnView } } };
        var localization = LocalizationService.Shared;
        var language = localization.SelectedLanguage;
        localization.SelectedLanguage = localization.Languages.Single(item => item.Id == "en");
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (!scalarView.FindControl<TextBlock>("PortTypeLabel")!.IsEffectivelyVisible ||
                scalar.DisplayPortType != "Scalar" ||
                !gpuView.FindControl<TextBlock>("PortTypeLabel")!.IsEffectivelyVisible ||
                gpuView.FindControl<TextBlock>("PortTypeLabel")!.Text != "GPU Particle" ||
                !spawnView.FindControl<TextBlock>("PortTypeLabel")!.IsEffectivelyVisible ||
                spawnView.FindControl<TextBlock>("GroupHintLabel")!.IsEffectivelyVisible ||
                gpu.HasLegacyInput || gpu.HasLegacyOutput ||
                graph.TryConnectNodes(gpu, target, out _, "a"))
                throw new InvalidOperationException("GPU containers must not present or expose scalar ports.");
            localization.SelectedLanguage = localization.Languages.Single(item => item.Id == "ja");
            Dispatcher.UIThread.RunJobs();
            if (gpuView.FindControl<TextBlock>("PortTypeLabel")!.Text != gpu.OriginalNodeName ||
                !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
                throw new InvalidOperationException("GPU container label localization changed graph data.");
            graph.RenameNode(gpu, "aaa");
            Dispatcher.UIThread.RunJobs();
            if (gpuView.FindControl<TextBlock>("PortTypeLabel")!.Text != gpu.OriginalNodeName)
                throw new InvalidOperationException("Renaming must retain the original node type in the footer.");
        }
        finally { localization.SelectedLanguage = language; window.Close(); }
    }

    private static void VerifyLifecycleHelp()
    {
        var root = new GraphWorkspaceViewModel(new UndoRedoManager());
        var life = root.CreateNode(new GraphNodeKind("LifeTime"));
        var destroy = root.CreateNode(new GraphNodeKind("OnDestroy"));
        var delay = root.CreateNode(new GraphNodeKind("Delay"));
        var spawnable = root.CreateNode(GraphNodeKind.SpawnableGroup);
        var child = new GraphWorkspaceViewModel(new UndoRedoManager());
        var childGroup = child.CreateNode(GraphNodeKind.SpawnableGroup);
        if (!child.EnterGraph(childGroup)) throw new InvalidOperationException("Cannot enter Spawnable for lifecycle help.");
        var ownerDestroy = child.CreateNode(new GraphNodeKind("OwnerDestroy"));
        var spawnInput = child.CreateNode(new GraphNodeKind("SpawnInputFloat"));
        var entries = new[]
        {
            (Node: life, Key: "Editor.NodeHelp.LifeTime"),
            (Node: destroy, Key: "Editor.NodeHelp.OnDestroy"),
            (Node: delay, Key: "Editor.NodeHelp.Delay"),
            (Node: spawnable, Key: "Editor.NodeHelp.SpawnableGroup"),
            (Node: ownerDestroy, Key: "Editor.NodeHelp.OwnerDestroy"),
            (Node: spawnInput, Key: "Editor.NodeHelp.SpawnInput")
        };
        var views = entries.Select(entry => new GraphNodeView { DataContext = entry.Node }).ToArray();
        var panel = new StackPanel();
        foreach (var view in views) panel.Children.Add(view);
        var window = new Window { Content = panel };
        var localization = LocalizationService.Shared;
        var language = localization.SelectedLanguage;
        var before = root.CreateSnapshot();
        window.Show();
        try
        {
            foreach (var languageId in new[] { "en", "ja" })
            {
                localization.SelectedLanguage = localization.Languages.Single(item => item.Id == languageId);
                Dispatcher.UIThread.RunJobs();
                for (var index = 0; index < entries.Length; index++)
                {
                    var header = views[index].FindControl<Border>("NodeHeader")!;
                    var expected = localization.Get(entries[index].Key);
                    var tooltip = ToolTip.GetTip(header)?.ToString();
                    if (!header.IsEffectivelyVisible || tooltip is null ||
                        !tooltip.StartsWith(expected, StringComparison.Ordinal) ||
                        AutomationProperties.GetHelpText(header) != tooltip)
                        throw new InvalidOperationException($"Lifecycle help is missing for {entries[index].Node.Definition.Kind} in {languageId}. " +
                            $"Expected '{expected}', tooltip '{ToolTip.GetTip(header)}', help '{AutomationProperties.GetHelpText(header)}'.");
                }
            }
            if (!GraphSnapshot.ContentEquals(before, root.CreateSnapshot()))
                throw new InvalidOperationException("Lifecycle help changed the authored graph.");
        }
        finally { localization.SelectedLanguage = language; window.Close(); }
    }

    private static void VerifySpawnInputBindingEditor()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        if (!graph.EnterGraph(group)) throw new InvalidOperationException("Cannot enter binding editor fixture.");
        var input = graph.CreateNode(new GraphNodeKind("SpawnInputVector3"));
        graph.SelectNode(input);
        var host = new ParameterEditorHost();
        void BindCurrent()
        {
            var current = graph.GetNode(input.Id);
            graph.SelectNode(current);
            host.DataContext = current.InlineParameters.Single(parameter => parameter.Id == "bindingMode");
            Dispatcher.UIThread.RunJobs();
        }
        BindCurrent();
        var localization = LocalizationService.Shared;
        var language = localization.SelectedLanguage;
        var window = new Window { Content = host };
        window.Show();
        try
        {
            foreach (var id in new[] { "en", "ja" })
            {
                localization.SelectedLanguage = localization.Languages.Single(item => item.Id == id);
                BindCurrent();
                var parameter = (NodeParameterEditorViewModel)host.DataContext!;
                if (parameter.Name != localization.Get("Editor.Parameter.SpawnBindingMode") ||
                    !host.GetVisualDescendants().OfType<ComboBox>().Single().ItemsSource!.Cast<GraphEnumValue>()
                    .Select(value => value.Name).SequenceEqual([
                        localization.Get("Editor.SpawnBinding.CaptureAtSpawn"), localization.Get("Editor.SpawnBinding.Live")]))
                    throw new InvalidOperationException("Capture/Live editor labels are not localized.");
            }
            var selector = host.GetVisualDescendants().OfType<ComboBox>().Single();
            selector.SelectedItem = selector.ItemsSource!.Cast<GraphEnumValue>().Single(value => value.Value == 1);
            Dispatcher.UIThread.RunJobs();
            if (!SpawnableGroupSchema.IsLive(graph.CreateCurrentSnapshot().Nodes.Single(node => node.Id == input.Id)) ||
                !graph.History.Undo())
                throw new InvalidOperationException("The visible Capture/Live selector did not create an undoable edit.");
            BindCurrent();
            if (((NodeParameterEditorViewModel)host.DataContext!).EnumValue != 0 || !graph.History.Redo())
                throw new InvalidOperationException("Undo did not restore CaptureAtSpawn.");
            BindCurrent();
            if (((NodeParameterEditorViewModel)host.DataContext!).EnumValue != 1)
                throw new InvalidOperationException("Redo did not restore Live.");
        }
        finally { localization.SelectedLanguage = language; window.Close(); }
    }

    public static void VerifyParameterRows()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var sourceA = graph.CreateNode(GraphNodeKind.Float);
        var sourceB = graph.CreateNode(GraphNodeKind.Float);
        var target = graph.CreateNode(GraphNodeKind.Multiply);
        var targetId = target.Id;
        graph.SetParameter(target, "a", JsonSerializer.SerializeToElement(7));
        graph.SetParameter(target, "b", JsonSerializer.SerializeToElement(8));
        if (!graph.TryConnectNodes(sourceA, target, out _, "a") || !graph.TryConnectNodes(sourceB, target, out _, "b"))
            throw new InvalidOperationException("Parameter-row connection fixtures failed.");
        graph.SelectNode(target);
        var pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph));
        var parameter = pane.Parameters.Single(p => p.Id == "a");
        var host = new ParameterEditorHost { DataContext = parameter };
        var window = new Window { Width = 540, Height = 140, Content = host };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var row = (Grid)host.Content!;
            var actions = row.Children.OfType<StackPanel>().Single();
            var disconnect = actions.Children.OfType<IconButton>().Single(button => button.Name == "DisconnectParameterButton");
            var lockButton = actions.Children.OfType<IconButton>().Single(button => button.Name == "LockParameterButton");
            var reset = actions.Children.OfType<IconButton>().Single(button => button.Icon == EditorIcon.Reset);
            var label = row.Children.OfType<TextBlock>().First();
            var editor = row.Children.OfType<TextBox>().Single();
            if (!disconnect.IsVisible || disconnect.Icon != EditorIcon.Link || editor.IsEnabled ||
                reset.Command!.CanExecute(null) || editor.Bounds.Left < label.Bounds.Right ||
                actions.Bounds.Left < editor.Bounds.Right || disconnect.Bounds.Right > reset.Bounds.Left)
                throw new InvalidOperationException("Parameter row must align label, disabled connected field and trailing link/lock/reset actions.");
            if (row.Children.OfType<TextBlock>().Single(block => block.Name == "ParameterStatus").IsVisible)
                throw new InvalidOperationException("Empty parameter status must not reserve an extra inspector row.");
            pane.Parameters.Single(p => p.Id == "a").DisconnectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            if (graph.Connections.Count != 1 || graph.Connections.Single().TargetPortId != "b" ||
                pane.Parameters.Single(p => p.Id == "a").HasConnection ||
                !disconnect.IsVisible || disconnect.Icon != EditorIcon.Disconnect || !editor.IsEnabled)
                throw new InvalidOperationException("Row unlink must remove only the matching input pin, keep the link icon visible, and re-enable editing.");
            reset.Command!.Execute(null);
            if (parameter.NumericValue != 1 || graph.Connections.Count != 1 || parameter.ResetCommand.CanExecute(null))
                throw new InvalidOperationException("Reset must restore the declared default when unlocked and disconnected.");
            graph.History.Undo();
            if (graph.Nodes.Single(node => node.Id == targetId).Parameters!.Single(p => p.Id == "a").Value.GetDouble() != 7)
                throw new InvalidOperationException("Parameter reset must use one undoable graph edit.");
            graph.History.Redo();
            parameter = pane.Parameters.Single(p => p.Id == "a");
            host.DataContext = parameter;
            Dispatcher.UIThread.RunJobs();
            row = (Grid)host.Content!;
            actions = row.Children.OfType<StackPanel>().Single();
            lockButton = actions.Children.OfType<IconButton>().Single(button => button.Name == "LockParameterButton");
            reset = actions.Children.OfType<IconButton>().Single(button => button.Icon == EditorIcon.Reset);
            editor = row.Children.OfType<TextBox>().Single();
            lockButton.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            if (!parameter.IsParameterLocked || editor.IsEnabled || reset.Command!.CanExecute(null) ||
                lockButton.Icon != EditorIcon.Lock || !lockButton.IsEnabled)
                throw new InvalidOperationException(
                    $"Per-parameter lock failed: locked={parameter.IsParameterLocked}, editorEnabled={editor.IsEnabled}, resetCan={reset.Command!.CanExecute(null)}, icon={lockButton.Icon}, lockEnabled={lockButton.IsEnabled}.");
            pane.ToggleNodeLockCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            if (!pane.IsNodeLocked || !parameter.IsEffectiveLocked || editor.IsEnabled ||
                lockButton.Command!.CanExecute(null))
                throw new InvalidOperationException("Node-level lock must disable editing and lock child parameter lock buttons.");
            pane.ToggleNodeLockCommand.Execute(null);
            lockButton.Command!.Execute(null);
            graph.History.Undo();
            graph.History.Undo();
            graph.History.Undo();
            graph.History.Undo();
            graph.History.Undo();
            graph.History.Undo();
            if (graph.Connections.Count != 2 || !pane.Parameters.Single(p => p.Id == "a").HasConnection)
                throw new InvalidOperationException("Undo must restore the row link state.");
            var vector = graph.CreateNode(GraphNodeKind.FloatX4);
            graph.SetParameter(vector, "value", JsonSerializer.SerializeToElement(new[] { 2, 3, 4, 5 }));
            graph.SelectNode(vector);
            pane.Parameters.Single().ResetCommand.Execute(null);
            using var resetVector = JsonDocument.Parse(pane.Parameters.Single().JsonValue);
            if (resetVector.RootElement.GetArrayLength() != 4 ||
                resetVector.RootElement.EnumerateArray().Any(component => component.GetDouble() != 0))
                throw new InvalidOperationException("Vector reset must restore all components from the declared default.");

            VerifyResponsiveVectorRows(graph, vector);
        }
        finally { window.Close(); }
    }

    private static void VerifyResponsiveVectorRows(GraphWorkspaceViewModel graph, GraphNodeViewModel vector)
    {
        graph.SetParameter(vector, "value", JsonSerializer.SerializeToElement(new[] { -123456.123456789, 2, 3, 4 }));
        using var pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(graph));
        var view = new NodePropertiesView { DataContext = pane };
        var frame = new Border { Width = 270, Child = view };
        var window = new Window { Width = 680, Height = 470, Content = frame };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var host = view.GetVisualDescendants().OfType<ParameterEditorHost>().Single();
            var inputs = host.GetVisualDescendants().OfType<TextBox>().ToArray();
            if (inputs.Length != 4 || inputs.Any(input => input.Bounds.Width < 40) ||
                inputs.Max(input => input.Bounds.Width) - inputs.Min(input => input.Bounds.Width) > 1 ||
                inputs[3].TranslatePoint(default, host)!.Value.Y <= inputs[0].TranslatePoint(default, host)!.Value.Y ||
                inputs.Any(input => input.TranslatePoint(new Point(input.Bounds.Width, 0), view)!.Value.X > view.Bounds.Width))
            {
                throw new InvalidOperationException("A narrow inspector must wrap vector components without widening for long values.");
            }

            frame.Width = 620;
            Dispatcher.UIThread.RunJobs();
            if (inputs.Any(input => input.TranslatePoint(default, host)!.Value.Y != inputs[0].TranslatePoint(default, host)!.Value.Y))
            {
                throw new InvalidOperationException("Widening the inspector did not put the same vector inputs back on one row.");
            }
        }
        finally { window.Close(); }
    }

    private static void VerifySettingsBindingLifetime()
    {
        var model = new MainViewModel();
        var settings = new SettingsWindow { DataContext = model };
        var text = LocalizationService.Shared;
        var original = text.SelectedLanguage;
        try
        {
            settings.Show();
            settings.FindControl<ComboBox>("LanguageList")!.SelectedItem = text.Languages.Single(language => language.Id == "ja");
            Dispatcher.UIThread.RunJobs();
            if (text.SelectedLanguage.Id != "ja" || settings.Title != text.Get("Editor.SettingsTitle"))
                throw new InvalidOperationException("The language setting did not update the actual settings window.");
            settings.Close();
            if (!model.TeamProfiles.Save(null, "Profile after closing settings", string.Empty) ||
                model.TeamProfiles.Active.Name != "Profile after closing settings")
                throw new InvalidOperationException("A closed settings window reverted the active project.");
        }
        finally
        {
            if (settings.IsVisible) settings.Close();
            text.SelectedLanguage = original;
            model.DisposeStorageFiles();
        }
    }

    private static void VerifyCatalogs()
    {
        var catalogs = new Dictionary<string, Dictionary<string, string>>();
        foreach (var language in new[] { "en", "ja" })
        {
            var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var uri in AssetLoader.GetAssets(new Uri("avares://LiberaFx/Assets/Translations"), null)
                         .Where(uri => uri.AbsolutePath.EndsWith("." + language + ".json", StringComparison.Ordinal)))
            {
                using var stream = AssetLoader.Open(uri);
                foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!)
                    if (!catalog.TryAdd(entry.Key, entry.Value) || string.IsNullOrWhiteSpace(entry.Value))
                        throw new InvalidOperationException($"Duplicate or empty translation: {entry.Key}");
            }
            catalogs.Add(language, catalog);
        }
        if (!catalogs["en"].Keys.ToHashSet().SetEquals(catalogs["ja"].Keys))
            throw new InvalidOperationException("English and Japanese catalogs have different keys.");
        foreach (var (key, english) in catalogs["en"])
        {
            var japanese = catalogs["ja"][key];
            var expected = Regex.Matches(english, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}").Select(match => match.Groups[1].Value).Order();
            var actual = Regex.Matches(japanese, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}").Select(match => match.Groups[1].Value).Order();
            if (!expected.SequenceEqual(actual))
                throw new InvalidOperationException($"Translation format arguments differ: {key}");
        }
        if (LocalizationService.Shared.Get("Missing.Test.Key") != "Missing.Test.Key")
            throw new InvalidOperationException("Missing translations do not fall back predictably.");
    }
}
