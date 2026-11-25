using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Views;
using LiberaFx.Services.Compilation;
using LiberaFx.Services;

internal static class SpawnableGroupTests
{
    public static void Run()
    {
        VerifySignalBoundaryNavigation();
        VerifyColorOutputCompilation();
        var history = new UndoRedoManager();
        var workspace = new GraphWorkspaceViewModel(history);
        var view = new GraphEditorView { DataContext = workspace };
        if (view.FindControl<MenuItem>("NewSpawnableGroupMenu") is null)
            throw new InvalidOperationException("The graph context menu lacks New Spawnable Group.");
        var localization = LocalizationService.Shared;
        var previousLanguage = localization.SelectedLanguage;
        localization.SelectedLanguage = localization.Languages.Single(language => language.Id == "en");
        if (localization.Get("Editor.NewSpawnableGroup") != "New Spawnable Group")
            throw new InvalidOperationException("English Spawnable Group action is missing.");
        localization.SelectedLanguage = localization.Languages.Single(language => language.Id == "ja");
        if (localization.Get("Editor.NewSpawnableGroup") != "スポーン可能グループを作成")
            throw new InvalidOperationException("Japanese Spawnable Group action is missing.");
        localization.SelectedLanguage = previousLanguage;
        typeof(GraphEditorView).GetField("_pastePosition", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(view, new Point(123, 234));
        typeof(GraphEditorView).GetMethod("OnAddSpawnableGroupClick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(view, [null, new RoutedEventArgs()]);
        var group = workspace.Nodes.Single();
        if (!group.IsSpawnableGroup || group.X != 123 || group.Y != 234 || group.ChildGraph is null)
            throw new InvalidOperationException("Context action did not create one editable group at the clicked position.");
        if (!history.Undo() || workspace.Nodes.Count != 0 || !history.Redo())
            throw new InvalidOperationException("Spawnable Group creation is not undoable.");
        group = workspace.Nodes.Single();
        if (!workspace.EnterGraph(group)) throw new InvalidOperationException("Cannot enter Spawnable Group.");
        var input = workspace.CreateNode(new GraphNodeKind("SpawnInputVector3"), 1, 2);
        var color = workspace.CreateNode(new GraphNodeKind("SpawnInputColor"), 2, 2);
        var capturedTime = workspace.CreateNode(new GraphNodeKind("SpawnInputFloat"), 3, 2);
        var time = workspace.CreateNode(GraphNodeKind.Time, 3, 4);
        var gpu = workspace.CreateNode(new GraphNodeKind("GpuParticle"), 5, 6);
        var snapshot = workspace.CreateSnapshot();
        var encoded = JsonSerializer.Serialize(snapshot);
        var loaded = JsonSerializer.Deserialize<GraphSnapshot>(encoded)!;
        if (!GraphSnapshot.ContentEquals(snapshot, loaded))
            throw new InvalidOperationException("Spawnable Group child graph did not round-trip.");
        workspace.NavigateToGraph(0);
        group = workspace.Nodes.Single();
        if (!group.InputPorts.Any(port => port.Id == SpawnableGroupSchema.PortId(input.Id) && port.PortType == "FloatX3"))
            throw new InvalidOperationException("Spawn input did not synchronize with the container.");
        if (!history.Undo() || !history.Undo() || !history.Undo() || !history.Undo() || !history.Undo() ||
            !history.Redo() || !history.Redo() || !history.Redo() || !history.Redo() || !history.Redo())
            throw new InvalidOperationException("Child graph edits do not use shared undo.");
        group = workspace.Nodes.Single();
        var source = workspace.CreateNode(GraphNodeKind.FloatX3);
        workspace.SetParameter(source, "value", JsonSerializer.SerializeToElement(new[] { 1d, 2d, 3d }));
        var signal = workspace.CreateNode(GraphNodeKind.Signal);
        var parentTime = workspace.CreateNode(GraphNodeKind.Time);
        if (!workspace.TryConnectNodes(source, group, out var error, SpawnableGroupSchema.PortId(input.Id)) ||
            !workspace.TryConnectNodes(signal, group, out error, "spawn") ||
            !workspace.TryConnectNodes(parentTime, group, out error, SpawnableGroupSchema.PortId(capturedTime.Id)))
            throw new InvalidOperationException($"Spawnable Group ports could not connect: {error}");
        snapshot = workspace.CreateSnapshot();
        GraphValidator.Validate(snapshot);
        using (var stream = new MemoryStream())
        {
            var files = new ProjectFileService();
            files.SaveAsync(stream, workspace).GetAwaiter().GetResult();
            stream.Position = 0;
            var project = files.LoadAsync(stream).GetAwaiter().GetResult();
            if (!GraphSnapshot.ContentEquals(snapshot, project.Graph) || project.History is null)
                throw new InvalidOperationException("Project save/load lost the Spawnable Group or shared history.");
        }

        var package = SpawnableGroupPackage.Compile(snapshot);
        var template = package.Templates.Single();
        if (template.ContainerId != group.Id || template.Inputs[0].TemplateNodeId != input.Id ||
            template.Inputs[0].Width != 3 || template.Inputs[1].TemplateNodeId != color.Id ||
            !template.Inputs[1].IsColor || template.Inputs[2].TemplateNodeId != capturedTime.Id || package.ParentBytecode[4] != 11 ||
            template.GpuContainers.Single().Id != gpu.Id ||
            template.Bytecode.AsSpan().IndexOf(gpu.Id.ToByteArray(bigEndian: true)) >= 0 ||
            template.Bytecode.AsSpan().IndexOf(input.Id.ToByteArray(bigEndian: true)) < 0 ||
            package.ParentBytecode.AsSpan().IndexOf(input.Id.ToByteArray(bigEndian: true)) >= 0)
            throw new InvalidOperationException("Template package did not isolate child bytecode and capture mapping.");
        try { VfxProgram.Compile(snapshot); throw new Exception("Raw bytecode silently discarded the template."); }
        catch (InvalidDataException) { }
        VerifyNative(package, signal.Id, input.Id, color.Id, capturedTime.Id, time.Id);
        if (!GraphSnapshot.ContentEquals(snapshot, workspace.CreateSnapshot()))
            throw new InvalidOperationException("Runtime signal execution mutated the editable graph.");
        var pasted = new GraphWorkspaceViewModel(new UndoRedoManager());
        pasted.PasteSnapshot(snapshot, 500, 500);
        GraphValidator.Validate(pasted.CreateSnapshot());
        var pastedGroup = pasted.Nodes.Single(node => node.IsSpawnableGroup);
        if (!pasted.Connections.Any(edge => edge.Target == pastedGroup &&
            edge.TargetPortId == SpawnableGroupSchema.PortId(pastedGroup.ChildGraph!.Nodes.First(node =>
                SpawnableGroupSchema.IsInput(node.Kind)).Id)))
            throw new InvalidOperationException("Pasted group input wiring was not remapped.");

        var provider = new ActiveGraphProvider(workspace);
        var describe = LiberaFxMcpTools.DescribeSpawnable(provider, group.Id.ToString());
        Dispatcher.UIThread.RunJobs();
        using (var description = JsonDocument.Parse(describe.GetAwaiter().GetResult()))
            if (description.RootElement.GetProperty("ports").GetArrayLength() != 3)
                throw new InvalidOperationException("MCP omitted synchronized typed inputs.");
        var enterTask = LiberaFxMcpTools.EnterGraph(provider, group.Id.ToString());
        Dispatcher.UIThread.RunJobs();
        enterTask.GetAwaiter().GetResult();
        workspace.DeleteNode(workspace.GetNode(input.Id));
        if (workspace.CreateSnapshot().Connections.Any(edge => edge.TargetNodeId == group.Id &&
            edge.TargetPortId == SpawnableGroupSchema.PortId(input.Id)))
            throw new InvalidOperationException("Deleting a spawn input left a dangling parent connection.");
        if (!history.Undo()) throw new InvalidOperationException("Cannot undo deleted spawn input.");
        var upTask = LiberaFxMcpTools.ParentGraph(provider);
        Dispatcher.UIThread.RunJobs();
        upTask.GetAwaiter().GetResult();
        var compileTask = LiberaFxMcpTools.CompileSpawnable(provider);
        Dispatcher.UIThread.RunJobs();
        var mcp = compileTask.GetAwaiter().GetResult();
        using var json = JsonDocument.Parse(mcp);
        if (json.RootElement.GetProperty("version").GetInt32() != SpawnableGroupPackage.FormatVersion ||
            json.RootElement.GetProperty("templates")[0].GetProperty("containerId").GetGuid() != group.Id)
            throw new InvalidOperationException("MCP did not expose the same compiled template.");
        var library = new GroupAssetLibrary(Path.Combine(AppDataDirectory.Root, "spawnable-groups"));
        var asset = library.Create("Spawn template", snapshot.Nodes.Single(node => node.Id == group.Id).ChildGraph!);
        var linked = snapshot with { Nodes = snapshot.Nodes.Select(node => node.Id == group.Id
            ? node with { GroupAsset = new GroupAssetReference(asset.Id, asset.Revision, false) } : node).ToArray() };
        var resolver = new GroupCompilationResolver([library]);
        var resolved = resolver.Resolve(linked);
        var resolvedGroup = resolved.Nodes.Single(node => node.Kind == GraphNodeKind.SpawnableGroup);
        if (!resolved.Connections.Any(edge => edge.TargetNodeId == group.Id &&
            edge.TargetPortId == SpawnableGroupSchema.PortId(resolvedGroup.ChildGraph!.Nodes.First(node =>
                SpawnableGroupSchema.IsInput(node.Kind)).Id)))
            throw new InvalidOperationException("Linked Spawnable Group lost its input mapping.");
        _ = SpawnableGroupPackage.Compile(resolved);
        var outer = library.Create("Outer", linked);
        var groupDefinition = GraphNodeDefinition.Core.Single(definition => definition.Kind == GraphNodeKind.GraphGroup);
        var outerNode = new GraphNodeSnapshot(Guid.NewGuid(), groupDefinition.Kind, "Outer", 0, 0,
            groupDefinition.PortType, groupDefinition.PortCategory, ChildGraph: outer.Graph,
            GroupAsset: new GroupAssetReference(outer.Id, outer.Revision, false));
        _ = SpawnableGroupPackage.Compile(resolver.Resolve(new GraphSnapshot([outerNode], [])));
    }

    private static void VerifySignalBoundaryNavigation()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        var groupId = group.Id;
        graph.EnterGraph(group);
        var input = graph.CreateNode(new GraphNodeKind("SpawnInputSignal"));
        var output = graph.CreateNode(new GraphNodeKind("SpawnOutputSignal"));
        graph.NavigateToGraph(0);
        group = graph.GetNode(groupId);
        var source = graph.CreateNode(GraphNodeKind.Sequence);
        var sink = graph.CreateNode(GraphNodeKind.Signal);
        if (!graph.TryConnectNodes(source, group, out _, "spawn", "then_0") ||
            !graph.TryConnectNodes(source, group, out _, SpawnableGroupSchema.PortId(input.Id), "then_1") ||
            !graph.TryConnectNodes(group, sink, out _, sourcePortId: SpawnableGroupSchema.OutputPortId(output.Id)))
            throw new InvalidOperationException("Cannot create custom Spawnable signal routes.");
        var before = graph.CreateSnapshot();
        var historyId = graph.History.CurrentNodeId;
        graph.EnterGraph(group);
        if (!GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) ||
            historyId != graph.History.CurrentNodeId)
            throw new InvalidOperationException("Reading inside a Spawnable changed its parent signal routes or history.");
        graph.NavigateToGraph(0);
        graph.EnterGraph(graph.GetNode(groupId));
        using var project = new MemoryStream();
        new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
        project.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
        if (!GraphSnapshot.ContentEquals(before, loaded.Graph))
            throw new InvalidOperationException("Saving from inside a Spawnable discarded custom signal routes.");
        graph.DeleteNode(graph.GetNode(output.Id));
        var remaining = graph.CreateSnapshot().Connections;
        if (remaining.Count != 2 || remaining.Any(connection => connection.SourceNodeId == groupId))
            throw new InvalidOperationException("Removing a signal output did not prune only its route.");
        graph.DeleteNode(graph.GetNode(input.Id));
        if (graph.CreateSnapshot().Connections.Count != 1 ||
            graph.CreateSnapshot().Connections.Single().TargetPortId != "spawn")
            throw new InvalidOperationException("Removing a signal input did not preserve the spawn route.");
        if (!graph.History.Undo() || !graph.History.Undo() ||
            !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new InvalidOperationException("Undo did not restore the custom signal routes.");
    }

    private static void VerifyColorOutputCompilation()
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = workspace.CreateNode(GraphNodeKind.SpawnableGroup);
        if (!workspace.EnterGraph(group)) throw new InvalidOperationException("Cannot enter color output fixture.");
        workspace.CreateNode(new GraphNodeKind("SpawnOutputColor"));
        workspace.NavigateToGraph(0);
        _ = SpawnableGroupPackage.Compile(workspace.CreateSnapshot());
    }

    private static void VerifyNative(SpawnableGroupPackage package, Guid signal, Guid input, Guid color, Guid capturedTime, Guid time)
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine("Native Spawnable Group check skipped; set LIBERAFX_NATIVE_TEST_LIBRARY.");
            return;
        }
        var template = package.Templates.Single();
        var inputs = template.Inputs.ToArray();
        inputs[0] = inputs[0] with { Width = 2 };
        var mismatched = package with { Templates = [template with { Inputs = inputs }] };
        var rejected = false;
        try
        {
            using var invalid = new NativeSpawnableSession(mismatched, path);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        if (!rejected)
        {
            throw new InvalidOperationException("Mismatched package descriptors were accepted.");
        }
        using var host = new NativeSpawnableSession(package with { AutoDestroyWhenIdle = false }, path);
        host.Advance(10);
        host.DispatchSignal(signal);
        host.Advance(12);
        host.DispatchSignal(signal);
        host.DispatchSignal(signal);
        var spawns = host.Events.Where(item => item.Kind == 1).ToArray();
        if (spawns.Length != 3 || spawns.Select(item => item.InstanceId).Distinct().Count() != 3 ||
            spawns.Select(item => item.Seed).Distinct().Count() != 3 || spawns[1].Time != spawns[2].Time)
        {
            throw new InvalidOperationException("Repeated signals did not create distinct independent instances.");
        }
        host.Advance(15);
        var first = spawns[0].InstanceId;
        var second = spawns[1].InstanceId;
        var third = spawns[2].InstanceId;
        var vector = host.Read(template.ContainerId, first, input);
        var rgba = host.Read(template.ContainerId, first, color);
        var firstCapture = host.Read(template.ContainerId, first, capturedTime);
        var secondCapture = host.Read(template.ContainerId, second, capturedTime);
        var firstTime = host.Read(template.ContainerId, first, time);
        var secondTime = host.Read(template.ContainerId, second, time);
        if (vector.Width != 3 || !vector.Lanes.Take(3).SequenceEqual([1d, 2d, 3d]) ||
            rgba.Width != 4 || !rgba.Lanes.SequenceEqual([1d, 1d, 1d, 1d]) ||
            firstCapture.Lanes[0] != 10 || secondCapture.Lanes[0] != 12 ||
            firstTime.Lanes[0] != 5 || secondTime.Lanes[0] != 3)
        {
            throw new InvalidOperationException("Captured typed input or local elapsed time is incorrect.");
        }
        host.ForceTerminate(template.ContainerId, first);
        var invalidated = false;
        try
        {
            _ = host.Read(template.ContainerId, first, time);
        }
        catch (InvalidOperationException)
        {
            invalidated = true;
        }
        if (!invalidated || !host.Events.Any(item => item.Kind == 5 && item.InstanceId == first))
        {
            throw new InvalidOperationException("Terminated instance was not invalidated and reported.");
        }
        host.Advance(16);
        if (host.Read(template.ContainerId, second, time).Lanes[0] != 4 ||
            host.Read(template.ContainerId, third, time).Lanes[0] != 4)
        {
            throw new InvalidOperationException("Same-frame instance time is incorrect.");
        }
    }
}
