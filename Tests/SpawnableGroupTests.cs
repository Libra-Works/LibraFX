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
        try { workspace.CreateNode(GraphNodeKind.SpawnableGroup); throw new Exception("Nested Spawnable Group accepted."); }
        catch (InvalidOperationException) { }
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
            !template.Inputs[1].IsColor || template.Inputs[2].TemplateNodeId != capturedTime.Id || package.ParentBytecode[4] != 3 ||
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

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeBinding
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Type;
        public ulong Pool;
        public uint Count, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] Widths;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] Colors;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Nodes;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeEvent
    {
        public uint Kind, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Container;
        public ulong Instance;
        public double Start;
        public ulong Seed;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeValue { public double X, Y, Z, W; public uint Count, Reserved; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProgram(byte[] bytes, ulong size, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateParent(byte[] bytes, ulong size, [In] NativeBinding[] bindings, uint count, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreatePool(ulong program, byte[] container, uint max, ulong budget, uint events, out ulong pool);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateInstance(ulong program, out ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Release(ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EvaluateParent(ulong instance, double time, IntPtr bindings, uint count, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Dispatch(ulong instance, byte[] node, double time, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Process(ulong pool, uint max, out uint processed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Event(ulong pool, out NativeEvent value, out uint hasEvent);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EvaluateChild(ulong pool, ulong instance, double time);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadChild(ulong pool, ulong instance, byte[] node, out NativeValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Stop(ulong pool, ulong instance);

    private static void VerifyNative(SpawnableGroupPackage package, Guid signal, Guid input, Guid color, Guid capturedTime, Guid time)
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrEmpty(path)) { Console.WriteLine("Native Spawnable Group check skipped; set LIBERAFX_NATIVE_TEST_LIBRARY."); return; }
        var library = NativeLibrary.Load(path);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        void Check(int result) { if (result != 0) throw new InvalidOperationException($"Native Spawnable Group error {result}."); }
        var template = package.Templates.Single();
        ulong child = 0, pool = 0, parent = 0, instance = 0;
        try
        {
            Check(Fn<CreateProgram>("libra_program_create")(template.Bytecode, (ulong)template.Bytecode.Length, out child));
            Check(Fn<CreatePool>("libra_generator_pool_create")(child, template.ContainerId.ToByteArray(true), 2, 65536, 8, out pool));
            var nodes = new byte[64]; input.TryWriteBytes(nodes.AsSpan(0, 16), true, out _);
            color.TryWriteBytes(nodes.AsSpan(16, 16), true, out _);
            capturedTime.TryWriteBytes(nodes.AsSpan(32, 16), true, out _);
            var binding = new NativeBinding { Container = template.ContainerId.ToByteArray(true), Type = template.SignalTypeId.ToByteArray(true),
                Pool = pool, Count = 3, Widths = [3, 4, 1, 0], Colors = [0, 1, 0, 0], Nodes = nodes };
            var createParent = Fn<CreateParent>("libra_program_create_spawnable");
            var mismatched = binding;
            mismatched.Widths = [2, 4, 1, 0];
            if (createParent(package.ParentBytecode, (ulong)package.ParentBytecode.Length, [mismatched], 1, out var rejected) == 0 || rejected != 0)
                throw new InvalidOperationException("Mismatched package descriptors were accepted.");
            Check(createParent(package.ParentBytecode, (ulong)package.ParentBytecode.Length, [binding], 1, out parent));
            Check(Fn<CreateInstance>("libra_instance_create")(parent, out instance));
            var evaluateParent = Fn<EvaluateParent>("libra_evaluate");
            var dispatch = Fn<Dispatch>("libra_dispatch_signal");
            Check(evaluateParent(instance, 10, IntPtr.Zero, 0, IntPtr.Zero));
            Check(dispatch(instance, signal.ToByteArray(true), 10, 4));
            Check(evaluateParent(instance, 12, IntPtr.Zero, 0, IntPtr.Zero));
            Check(dispatch(instance, signal.ToByteArray(true), 12, 4));
            Check(Fn<Process>("libra_generator_process_spawns")(pool, 2, out var processed));
            if (processed != 2) throw new InvalidOperationException("Native signal did not create two runtime instances.");
            var next = Fn<Event>("libra_generator_next_event");
            Check(next(pool, out var first, out var has)); if (has == 0) throw new InvalidOperationException("Missing spawn event.");
            Check(next(pool, out var second, out has)); if (has == 0 || first.Instance == second.Instance || first.Seed == second.Seed) throw new InvalidOperationException("Missing distinct instance or seed.");
            var evaluate = Fn<EvaluateChild>("libra_generator_evaluate");
            var read = Fn<ReadChild>("libra_generator_value");
            Check(evaluate(pool, first.Instance, 15)); Check(evaluate(pool, second.Instance, 15));
            Check(read(pool, first.Instance, input.ToByteArray(true), out var vector));
            Check(read(pool, first.Instance, color.ToByteArray(true), out var rgba));
            Check(read(pool, first.Instance, capturedTime.ToByteArray(true), out var firstCapture));
            Check(read(pool, second.Instance, capturedTime.ToByteArray(true), out var secondCapture));
            Check(read(pool, first.Instance, time.ToByteArray(true), out var firstTime));
            Check(read(pool, second.Instance, time.ToByteArray(true), out var secondTime));
            if (vector.Count != 3 || vector.X != 1 || vector.Y != 2 || vector.Z != 3 ||
                rgba.Count != 4 || rgba.X != 1 || rgba.Y != 1 || rgba.Z != 1 || rgba.W != 1 ||
                firstCapture.X != 10 || secondCapture.X != 12 ||
                firstTime.X != 5 || secondTime.X != 3)
                throw new InvalidOperationException("Captured typed input or local elapsed time is incorrect.");
            Check(Fn<Stop>("libra_generator_stop")(pool, first.Instance));
            if (evaluate(pool, first.Instance, 15) != 13) throw new InvalidOperationException("Stopped handle was not invalidated.");
            Check(evaluate(pool, second.Instance, 16));
            Check(dispatch(instance, signal.ToByteArray(true), 12, 4));
            Check(Fn<Process>("libra_generator_process_spawns")(pool, 1, out processed));
            Check(next(pool, out var stopped, out has));
            if (has == 0 || stopped.Kind != 2) throw new InvalidOperationException("Missing stop event.");
            Check(next(pool, out var third, out has));
            if (has == 0 || third.Instance == first.Instance || third.Instance == second.Instance || third.Start != second.Start)
                throw new InvalidOperationException("Repeated same-frame signals did not create independent instances.");
            Check(evaluate(pool, third.Instance, 16));
            Check(read(pool, third.Instance, time.ToByteArray(true), out var thirdTime));
            if (thirdTime.X != 4) throw new InvalidOperationException("Same-frame instance time is incorrect.");
            if (Fn<Release>("libra_generator_pool_release")(pool) != 5)
                throw new InvalidOperationException("An attached pool handle was released prematurely.");
        }
        finally
        {
            if (instance != 0) Fn<Release>("libra_instance_release")(instance);
            if (parent != 0) Fn<Release>("libra_program_release")(parent);
            if (pool != 0) Fn<Release>("libra_generator_pool_release")(pool);
            if (child != 0) Fn<Release>("libra_program_release")(child);
            NativeLibrary.Free(library);
        }
    }
}
