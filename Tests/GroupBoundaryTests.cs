using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class GroupBoundaryTests
{
    public static void Run()
    {
        VerifyNumericBoundary();
        VerifySignalBoundary();
        VerifyTypedDefaults();
        VerifyTypedRuntimeRouting();
        VerifyNestedScopes();
        VerifyNestedSignalBoundary();
        VerifySignalConnectionOrder();
        VerifySignalFanout();
        VerifyBoundaryScopeValidation();
        VerifyDefaultLabels();
        VerifyMatrixBoundary();
        VerifyNestedMatrixBoundary();
        VerifyAssetUpdate();
        if (Environment.GetEnvironmentVariable("LIBERAFX_UNREAL_GROUP_FIXTURE") is { Length: > 0 } directory)
            ExportUnrealFixture(directory);
    }

    private static void VerifySignalConnectionOrder()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var source = graph.CreateNode(GraphNodeKind.Signal);
        var targets = Enumerable.Range(1, 3).Select(index =>
        {
            var target = graph.CreateNode(GraphNodeKind.Signal);
            graph.RenameNode(target, $"Target {index}");
            if (!graph.TryConnectNodes(source, target, out _))
                throw new InvalidOperationException("Cannot create Signal fan-out.");
            return target;
        }).ToArray();
        graph.SetSelection([source, .. targets]);
        var selection = graph.CreateSelectionSnapshot();
        graph.PasteSnapshot(selection, 200, 200);
        var pastedSource = graph.CreateSelectionSnapshot().Nodes.Single(node => node.Name == source.Name).Id;
        void Check()
        {
            var snapshot = graph.CreateSnapshot();
            var names = snapshot.Connections.Where(edge => edge.SourceNodeId == pastedSource).Select(edge =>
                snapshot.Nodes.Single(node => node.Id == edge.TargetNodeId).Name).ToArray();
            if (!names.SequenceEqual(new[] { "Target 1", "Target 2", "Target 3" }))
                throw new InvalidOperationException("Pasting changed the Signal connection order.");
        }
        Check();
        if (!graph.History.Undo() || !graph.History.Redo())
            throw new InvalidOperationException("Signal fan-out paste must support undo and redo.");
        Check();
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph;
        if (!GraphSnapshot.ContentEquals(graph.CreateSnapshot(), loaded))
            throw new InvalidOperationException("Saving changed the Signal connection order.");
        var destroyGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var parent = destroyGraph.CreateNode(GraphNodeKind.SpawnableGroup);
        var firstChild = destroyGraph.CreateNode(GraphNodeKind.SpawnableGroup);
        var secondChild = destroyGraph.CreateNode(GraphNodeKind.SpawnableGroup);
        if (!destroyGraph.TryConnectNodes(parent, firstChild, out _, "spawn", "destroy") ||
            !destroyGraph.TryConnectNodes(parent, secondChild, out _, "spawn", "destroy") ||
            destroyGraph.Connections.Any(connection => connection.SignalOrder != 0))
            throw new InvalidOperationException("Host-routed Destroy must not be labeled as depth-first Signal dispatch.");
    }

    private static void VerifySignalFanout()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(group);
        var input = graph.CreateNode(new GraphNodeKind("GroupInputSignal"));
        var output = graph.CreateNode(new GraphNodeKind("GroupOutputSignal"));
        if (!graph.TryConnectNodes(input, output, out _))
            throw new InvalidOperationException("Cannot connect Signal fan-out Group boundaries.");
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        var source = graph.CreateNode(GraphNodeKind.Signal);
        var first = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        var second = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        if (!graph.TryConnectNodes(source, group, out _, GroupBoundarySchema.InputPortId(input.Id)) ||
            !graph.TryConnectNodes(group, first, out _, "spawn", GroupBoundarySchema.OutputPortId(output.Id)) ||
            !graph.TryConnectNodes(source, second, out _, "spawn"))
            throw new InvalidOperationException("Cannot connect Signal fan-out.");
        graph.CreateNode(GraphNodeKind.FloatFCurve);
        var snapshot = graph.CreateSnapshot();
        var package = SpawnableGroupPackage.Compile(snapshot);
        if (package.ParentBytecode[4] != 5 ||
            !package.ParentBytecode.SequenceEqual(SpawnableGroupPackage.Compile(snapshot).ParentBytecode))
            throw new InvalidOperationException("Signal fan-out and FCurve must compile deterministically to v5.");
        VerifyNativeFanout(package, source.Id, first.Id, second.Id);
        var plain = new GraphWorkspaceViewModel(new UndoRedoManager());
        var plainSource = plain.CreateNode(GraphNodeKind.Signal);
        var a = plain.CreateNode(GraphNodeKind.Signal);
        var b = plain.CreateNode(GraphNodeKind.Signal);
        if (!plain.TryConnectNodes(plainSource, a, out _) || !plain.TryConnectNodes(plainSource, b, out _))
            throw new InvalidOperationException("Cannot connect plain Signal fan-out.");
        VerifyNativeBuiltinFanout(SpawnableGroupPackage.Compile(plain.CreateSnapshot()).ParentBytecode, plainSource.Id);
    }

    private static void VerifyNativeBuiltinFanout(byte[] bytes, Guid source)
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrEmpty(path)) return;
        var library = NativeLibrary.Load(path);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        static void Check(int result) { if (result != 0) throw new InvalidOperationException($"Builtin Signal runtime error {result}."); }
        ulong program = 0, instance = 0;
        try
        {
            Check(Fn<CreateProgram>("libra_program_create")(bytes, (ulong)bytes.Length, out program));
            Check(Fn<CreateInstance>("libra_instance_create")(program, out instance));
            Check(Fn<Evaluate>("libra_evaluate")(instance, 0, IntPtr.Zero, 0, IntPtr.Zero));
            if (Fn<Dispatch>("libra_dispatch_signal")(instance, source.ToByteArray(true), 0, 2) != 14)
                throw new InvalidOperationException("Builtin Signal fan-out must use a shared budget.");
            Check(Fn<Dispatch>("libra_dispatch_signal")(instance, source.ToByteArray(true), 0, 3));
        }
        finally
        {
            if (instance != 0) Fn<Release>("libra_instance_release")(instance);
            if (program != 0) Fn<Release>("libra_program_release")(program);
            NativeLibrary.Free(library);
        }
    }

    private static void VerifyNativeFanout(SpawnableGroupPackage package, Guid source, Guid first, Guid second)
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrEmpty(path)) return;
        var library = NativeLibrary.Load(path);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        static void Check(int result) { if (result != 0) throw new InvalidOperationException($"Native fan-out runtime error {result}."); }
        var programs = new List<ulong>();
        var pools = new List<ulong>();
        ulong parent = 0, instance = 0;
        try
        {
            var bindings = new List<NativeBinding>();
            foreach (var template in package.Templates)
            {
                Check(Fn<CreateProgram>("libra_program_create")(template.Bytecode, (ulong)template.Bytecode.Length, out var child));
                programs.Add(child);
                Check(Fn<CreatePool>("libra_generator_pool_create")(child, template.ContainerId.ToByteArray(true), 4, 65536, 8, out var pool));
                pools.Add(pool);
                bindings.Add(new NativeBinding { Container = template.ContainerId.ToByteArray(true),
                    Type = template.SignalTypeId.ToByteArray(true), Pool = pool,
                    Widths = new byte[4], Colors = new byte[4], Nodes = new byte[64] });
            }
            Check(Fn<CreateParent>("libra_program_create_spawnable")(package.ParentBytecode,
                (ulong)package.ParentBytecode.Length, bindings.ToArray(), (uint)bindings.Count, out parent));
            Check(Fn<CreateInstance>("libra_instance_create")(parent, out instance));
            Check(Fn<Evaluate>("libra_evaluate")(instance, 0, IntPtr.Zero, 0, IntPtr.Zero));
            var firstPool = pools[package.Templates.ToList().FindIndex(item => item.ContainerId == first)];
            var secondPool = pools[package.Templates.ToList().FindIndex(item => item.ContainerId == second)];
            if (Fn<Dispatch>("libra_dispatch_signal")(instance, source.ToByteArray(true), 0, 4) != 14)
                throw new InvalidOperationException("Fan-out must share its instruction budget across branches.");
            Check(Fn<ProcessPool>("libra_generator_process_spawns")(firstPool, 4, out var firstCount));
            Check(Fn<ProcessPool>("libra_generator_process_spawns")(secondPool, 4, out var secondCount));
            if (firstCount != 1 || secondCount != 0)
                throw new InvalidOperationException("Fan-out must finish its first branch before executing the second.");
            Check(Fn<Dispatch>("libra_dispatch_signal")(instance, source.ToByteArray(true), 0, 5));
            Check(Fn<ProcessPool>("libra_generator_process_spawns")(firstPool, 4, out firstCount));
            Check(Fn<ProcessPool>("libra_generator_process_spawns")(secondPool, 4, out secondCount));
            if (firstCount != 1 || secondCount != 1)
                throw new InvalidOperationException("Signal fan-out did not execute all connected branches.");
        }
        finally
        {
            if (instance != 0) Fn<Release>("libra_instance_release")(instance);
            if (parent != 0) Fn<Release>("libra_program_release")(parent);
            foreach (var pool in pools) Fn<Release>("libra_generator_pool_release")(pool);
            foreach (var program in programs) Fn<Release>("libra_program_release")(program);
            NativeLibrary.Free(library);
        }
    }

    private static void VerifyNumericBoundary()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        if (!graph.EnterGraph(group)) throw new InvalidOperationException("Cannot enter group.");
        var input = graph.CreateNode(new GraphNodeKind("GroupInputFloat"));
        var output = graph.CreateNode(new GraphNodeKind("GroupOutputFloat"));
        var localTarget = graph.CreateNode(GraphNodeKind.Float);
        if (output.HasLegacyOutput || output.HasCustomOutputs || graph.TryConnectNodes(output, localTarget, out _))
            throw new InvalidOperationException("A group output must not expose an internal output pin.");
        graph.DeleteNode(localTarget);
        graph.SetParameter(input, "value", JsonSerializer.SerializeToElement(2d));
        if (!graph.TryConnectNodes(input, output, out _))
            throw new InvalidOperationException("Cannot connect group boundaries internally.");
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        if (group.InputPorts.Single().Name != "Scalar" || group.OutputPorts.Single().Name != "Scalar" || group.ShowPortType)
            throw new InvalidOperationException("Default group pins are too verbose for the group node.");
        var describe = LiberaFxMcpTools.DescribeGroup(new ActiveGraphProvider(graph), group.Id.ToString());
        Dispatcher.UIThread.RunJobs();
        using (var json = JsonDocument.Parse(describe.GetAwaiter().GetResult()))
        {
            if (json.RootElement.GetProperty("inputs")[0].GetProperty("displayName").GetString() != group.InputPorts[0].Name ||
                json.RootElement.GetProperty("outputs")[0].GetProperty("displayName").GetString() != group.OutputPorts[0].Name)
                throw new InvalidOperationException("MCP group pin labels differ from the editor.");
        }
        var view = new GraphNodeView { DataContext = group };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (view.GetVisualDescendants().OfType<Button>().Count(item =>
                    item.Tag is GraphInputPortDefinition && item.IsEffectivelyVisible) != 1 ||
                view.GetVisualDescendants().OfType<Border>().Count(item =>
                    item.Tag is GraphOutputPortDefinition && item.IsEffectivelyVisible) != 1)
                throw new InvalidOperationException("Group input/output pins are not visible in the editor.");
        }
        finally { window.Close(); }
        var source = graph.CreateNode(GraphNodeKind.Float);
        graph.SetParameter(source, "value", JsonSerializer.SerializeToElement(7d));
        var target = graph.CreateNode(GraphNodeKind.Float);
        if (!graph.TryConnectNodes(source, group, out _, GroupBoundarySchema.InputPortId(input.Id)) ||
            !graph.TryConnectNodes(group, target, out _, sourcePortId: GroupBoundarySchema.OutputPortId(output.Id)))
            throw new InvalidOperationException("Cannot connect group boundaries in the parent.");
        var saved = graph.CreateSnapshot();
        GraphValidator.Validate(saved);
        var program = VfxProgram.Compile(saved);
        var code = program.TypedInstructions;
        var inputCode = code.Single(item => item.NodeId == input.Id);
        var outputCode = code.Single(item => item.NodeId == output.Id);
        var targetCode = code.Single(item => item.NodeId == target.Id);
        if (inputCode.DefaultA[0] != 2 || code[inputCode.InputA].NodeId != source.Id ||
            code[outputCode.InputA].NodeId != input.Id || code[targetCode.InputA].NodeId != output.Id)
            throw new InvalidOperationException("Group input/output did not compile to the parent and child sources.");
        VerifyNativeNumeric(program.ToBinary(), target.Id);
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
        if (!GraphSnapshot.ContentEquals(saved, loaded.Graph))
            throw new InvalidOperationException("Group boundary connections did not survive project persistence.");
        var copy = new GraphWorkspaceViewModel(new UndoRedoManager());
        copy.PasteSnapshot(saved, 100, 100);
        var pasted = copy.CreateSnapshot();
        GraphValidator.Validate(pasted);
        _ = VfxProgram.Compile(pasted);
        var copiedGroup = pasted.Nodes.Single(node => node.Kind == GraphNodeKind.GraphGroup);
        if (!pasted.Connections.Any(edge => edge.TargetNodeId == copiedGroup.Id &&
                edge.TargetPortId == GroupBoundarySchema.InputPortId(copiedGroup.ChildGraph!.Nodes[0].Id)) ||
            !pasted.Connections.Any(edge => edge.SourceNodeId == copiedGroup.Id &&
                edge.SourcePortId == GroupBoundarySchema.OutputPortId(copiedGroup.ChildGraph!.Nodes[1].Id)))
            throw new InvalidOperationException("Copy/paste did not remap group boundaries.");
        if (!graph.EnterGraph(group)) throw new InvalidOperationException("Cannot reenter group.");
        graph.DeleteNode(graph.GetNode(input.Id));
        if (graph.CreateSnapshot().Connections.Any(edge => edge.TargetNodeId == group.Id))
            throw new InvalidOperationException("Deleting an input left a dangling parent connection.");
        if (!graph.History.Undo() || !GraphSnapshot.ContentEquals(saved, graph.CreateSnapshot()))
            throw new InvalidOperationException("Undo did not restore the group and its parent connection.");
    }

    private static void VerifySignalBoundary()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(group);
        var input = graph.CreateNode(new GraphNodeKind("GroupInputSignal"));
        var output = graph.CreateNode(new GraphNodeKind("GroupOutputSignal"));
        if (output.HasLegacyOutput || output.HasCustomOutputs)
            throw new InvalidOperationException("A group signal output must not expose an internal output pin.");
        if (!graph.TryConnectNodes(input, output, out _))
            throw new InvalidOperationException("Cannot connect signal boundaries internally.");
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        var source = graph.CreateNode(GraphNodeKind.Signal);
        var target = graph.CreateNode(GraphNodeKind.Signal);
        if (!graph.TryConnectNodes(source, group, out _, GroupBoundarySchema.InputPortId(input.Id)) ||
            !graph.TryConnectNodes(group, target, out _, sourcePortId: GroupBoundarySchema.OutputPortId(output.Id)))
            throw new InvalidOperationException("Cannot connect signal boundaries in the parent.");
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        if (!graph.TryConnectNodes(target, spawnable, out _, "spawn"))
            throw new InvalidOperationException("Cannot connect group signal to Spawnable Group.");
        graph.CreateNode(GraphNodeKind.FloatFCurve);
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        if (package.ParentBytecode[4] != 4)
            throw new InvalidOperationException("Group signals and FCurves were not compiled together.");
        VerifyNativeSignal(package, source.Id);
    }

    private static void VerifyTypedDefaults()
    {
        foreach (var (suffix, expected) in new[]
        {
            ("Float", new[] { 0d }), ("Vector2", new[] { 0d, 0d }),
            ("Vector3", new[] { 0d, 0d, 0d }), ("Vector4", new[] { 0d, 0d, 0d, 0d }),
            ("Color", new[] { 0d, 0d, 0d, 1d })
        })
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var group = graph.CreateNode(GraphNodeKind.GraphGroup);
            graph.EnterGraph(group);
            var input = graph.CreateNode(new GraphNodeKind("GroupInput" + suffix));
            var output = graph.CreateNode(new GraphNodeKind("GroupOutput" + suffix));
            if (!graph.TryConnectNodes(input, output, out _))
                throw new InvalidOperationException($"Cannot connect {suffix} group boundaries.");
            graph.NavigateToGraph(0);
            var code = VfxProgram.Compile(graph.CreateSnapshot()).TypedInstructions;
            if (!code.Single(item => item.NodeId == input.Id).DefaultA.Take(expected.Length).SequenceEqual(expected) ||
                !code.Single(item => item.NodeId == output.Id).DefaultA.Take(expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException($"{suffix} boundary defaults did not survive compilation.");
        }
    }

    private static void VerifyTypedRuntimeRouting()
    {
        foreach (var (suffix, kind, expected) in new[]
        {
            ("Vector2", "FloatX2", new[] { -2d, 3d }),
            ("Vector3", "FloatX3", new[] { -2d, 3d, 5d }),
            ("Vector4", "FloatX4", new[] { -2d, 3d, 5d, 7d }),
            ("Color", "Color", new[] { 0.125d, 0.25d, 0.5d, 0.75d })
        })
        {
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var group = graph.CreateNode(GraphNodeKind.GraphGroup);
            graph.EnterGraph(group);
            var input = graph.CreateNode(new GraphNodeKind("GroupInput" + suffix));
            var output = graph.CreateNode(new GraphNodeKind("GroupOutput" + suffix));
            if (!graph.TryConnectNodes(input, output, out _))
                throw new InvalidOperationException($"Cannot connect {suffix} boundaries inside the group.");
            graph.NavigateToGraph(0);
            group = graph.GetNode(group.Id);
            var valueKind = GraphNodeDefinition.All.Single(item =>
                item.Namespace == "LiberaFx" && item.TypeName == kind).Kind;
            var source = graph.CreateNode(valueKind);
            var target = graph.CreateNode(valueKind);
            graph.SetParameter(source, "value", JsonSerializer.SerializeToElement(expected));
            if (!graph.TryConnectNodes(source, group, out _, GroupBoundarySchema.InputPortId(input.Id)) ||
                !graph.TryConnectNodes(group, target, out _, sourcePortId: GroupBoundarySchema.OutputPortId(output.Id)))
                throw new InvalidOperationException($"Cannot route {suffix} through the group.");
            var snapshot = graph.CreateSnapshot();
            GraphValidator.Validate(snapshot);
            var program = VfxProgram.Compile(snapshot);
            VerifyNativeNumeric(program.ToBinary(), target.Id, expected);
        }
    }

    private static void VerifyNestedScopes()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var outer = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(outer);
        var outerInput = graph.CreateNode(new GraphNodeKind("GroupInputFloat"));
        var outerOutput = graph.CreateNode(new GraphNodeKind("GroupOutputFloat"));
        var inner = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(inner);
        var innerInput = graph.CreateNode(new GraphNodeKind("GroupInputFloat"));
        var innerOutput = graph.CreateNode(new GraphNodeKind("GroupOutputFloat"));
        innerInput.Name = outerInput.Name;
        if (!graph.TryConnectNodes(innerInput, innerOutput, out _))
            throw new InvalidOperationException("Cannot connect nested child boundaries.");
        graph.NavigateToGraph(1);
        inner = graph.GetNode(inner.Id);
        if (!graph.TryConnectNodes(graph.GetNode(outerInput.Id), inner, out _, GroupBoundarySchema.InputPortId(innerInput.Id)) ||
            !graph.TryConnectNodes(inner, graph.GetNode(outerOutput.Id), out _,
                sourcePortId: GroupBoundarySchema.OutputPortId(innerOutput.Id)))
            throw new InvalidOperationException("Cannot connect nested group boundaries.");
        graph.NavigateToGraph(0);
        outer = graph.GetNode(outer.Id);
        var source = graph.CreateNode(GraphNodeKind.Float);
        var target = graph.CreateNode(GraphNodeKind.Float);
        if (!graph.TryConnectNodes(source, outer, out _, GroupBoundarySchema.InputPortId(outerInput.Id)) ||
            !graph.TryConnectNodes(outer, target, out _, sourcePortId: GroupBoundarySchema.OutputPortId(outerOutput.Id)))
            throw new InvalidOperationException("Cannot connect outer group boundaries.");
        var snapshot = graph.CreateSnapshot();
        var code = VfxProgram.Compile(snapshot).TypedInstructions;
        Guid Source(Guid id) => code[code.Single(item => item.NodeId == id).InputA].NodeId;
        if (Source(outerInput.Id) != source.Id || Source(innerInput.Id) != outerInput.Id ||
            Source(innerOutput.Id) != innerInput.Id || Source(outerOutput.Id) != innerOutput.Id ||
            Source(target.Id) != outerOutput.Id)
            throw new InvalidOperationException("Nested same-name boundaries leaked across group scopes.");
        var library = new GroupAssetLibrary(Path.Combine(AppDataDirectory.Root, "boundary-groups"));
        var child = snapshot.Nodes.Single(node => node.Id == outer.Id).ChildGraph!;
        var asset = library.Create("Nested boundary", child);
        var linked = snapshot with { Nodes = snapshot.Nodes.Select(node => node.Id == outer.Id
            ? node with { GroupAsset = new(asset.Id, asset.Revision, false) } : node).ToArray() };
        var resolved = new GroupCompilationResolver([library]).Resolve(linked);
        GraphValidator.Validate(resolved);
        _ = VfxProgram.Compile(resolved);
        var resolvedOuter = resolved.Nodes.Single(node => node.Id == outer.Id);
        if (!resolved.Connections.Any(edge => edge.TargetNodeId == outer.Id &&
            edge.TargetPortId == GroupBoundarySchema.InputPortId(resolvedOuter.ChildGraph!.Nodes[0].Id)))
            throw new InvalidOperationException("Linked group resolution lost its boundary mapping.");
    }

    private static void VerifyNestedSignalBoundary()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var outer = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(outer);
        var outerInput = graph.CreateNode(new GraphNodeKind("GroupInputSignal"));
        var outerOutput = graph.CreateNode(new GraphNodeKind("GroupOutputSignal"));
        var inner = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(inner);
        var innerInput = graph.CreateNode(new GraphNodeKind("GroupInputSignal"));
        var innerOutput = graph.CreateNode(new GraphNodeKind("GroupOutputSignal"));
        if (!graph.TryConnectNodes(innerInput, innerOutput, out _))
            throw new InvalidOperationException("Cannot connect nested Signal boundaries.");
        graph.NavigateToGraph(1);
        inner = graph.GetNode(inner.Id);
        if (!graph.TryConnectNodes(graph.GetNode(outerInput.Id), inner, out _,
                GroupBoundarySchema.InputPortId(innerInput.Id)) ||
            !graph.TryConnectNodes(inner, graph.GetNode(outerOutput.Id), out _,
                sourcePortId: GroupBoundarySchema.OutputPortId(innerOutput.Id)))
            throw new InvalidOperationException("Cannot connect nested Signal group.");
        graph.NavigateToGraph(0);
        outer = graph.GetNode(outer.Id);
        var signal = graph.CreateNode(GraphNodeKind.Signal);
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        graph.CreateNode(GraphNodeKind.FloatFCurve);
        if (!graph.TryConnectNodes(signal, outer, out _, GroupBoundarySchema.InputPortId(outerInput.Id)) ||
            !graph.TryConnectNodes(outer, spawnable, out _, "spawn", GroupBoundarySchema.OutputPortId(outerOutput.Id)))
            throw new InvalidOperationException("Cannot connect outer Signal group.");
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        VerifyNativeSignal(package, signal.Id);
    }

    private static void VerifyAssetUpdate()
    {
        var root = Path.Combine(AppDataDirectory.Root, "boundary-asset-updates");
        var profiles = new TeamProfilesService(Path.Combine(root, "profiles.json"));
        if (!profiles.Save(null, "Boundary test", Path.Combine(root, "library")))
            throw new InvalidOperationException(profiles.Status);
        using var catalog = new GroupAssetCatalog(profiles);
        var library = catalog.Library();
        var author = new GraphWorkspaceViewModel(new UndoRedoManager());
        var container = author.CreateNode(GraphNodeKind.GraphGroup);
        author.EnterGraph(container);
        var input = author.CreateNode(new GraphNodeKind("GroupInputFloat"));
        var output = author.CreateNode(new GraphNodeKind("GroupOutputFloat"));
        if (!author.TryConnectNodes(input, output, out _))
            throw new InvalidOperationException("Cannot author linked group.");
        var matrixInput = author.CreateNode(new GraphNodeKind("GroupInputMatrix"));
        var matrixOutput = author.CreateNode(new GraphNodeKind("GroupOutputMatrix"));
        if (!author.TryConnectNodes(matrixInput, matrixOutput, out _))
            throw new InvalidOperationException("Cannot author linked Matrix boundaries.");
        var asset = library.Create("Boundary", author.CreateCurrentSnapshot());
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var group = graph.CreateGroupAssetNode(asset.Id, 0, 0);
        var source = graph.CreateNode(GraphNodeKind.Float);
        var target = graph.CreateNode(GraphNodeKind.Float);
        var transform = graph.CreateNode(new GraphNodeKind("Transform"));
        var point = graph.CreateNode(new GraphNodeKind("TransformPoint"));
        if (!graph.TryConnectNodes(source, group, out _, group.InputPorts.Single(port => port.PortType == "Float").Id) ||
            !graph.TryConnectNodes(group, target, out _, sourcePortId: group.OutputPorts.Single(port => port.PortType == "Float").Id) ||
            !graph.TryConnectNodes(transform, group, out _, group.InputPorts.Single(port => port.PortType == "Matrix").Id, "value") ||
            !graph.TryConnectNodes(group, point, out _, "matrix", group.OutputPorts.Single(port => port.PortType == "Matrix").Id))
            throw new InvalidOperationException("Cannot connect linked group.");
        var before = graph.CreateSnapshot();
        var revision = asset.Graph with { Nodes = asset.Graph.Nodes.Select(node =>
            node.Id == input.Id ? node with { Name = "Renamed input" } : node).ToArray() };
        library.Save(asset.Id, asset.Revision, asset.Name, revision);
        catalog.Refresh();
        var updates = graph.FindGroupUpdates();
        if (updates.Count != 1 || updates[0].HasLocalChanges || graph.ApplyGroupUpdates(updates) != 1)
            throw new InvalidOperationException("An unchanged linked boundary could not be updated.");
        var after = graph.CreateSnapshot();
        var updated = after.Nodes.Single(node => node.Id == group.Id);
        if (!after.Connections.Any(edge => edge.TargetNodeId == group.Id &&
                edge.TargetPortId == GroupBoundarySchema.InputPortId(updated.ChildGraph!.Nodes[0].Id)) ||
            !after.Connections.Any(edge => edge.SourceNodeId == group.Id &&
                edge.SourcePortId == GroupBoundarySchema.OutputPortId(updated.ChildGraph!.Nodes[1].Id)) ||
            !after.Connections.Any(edge => edge.TargetNodeId == group.Id &&
                edge.TargetPortId == GroupBoundarySchema.InputPortId(updated.ChildGraph!.Nodes[2].Id)) ||
            !after.Connections.Any(edge => edge.SourceNodeId == group.Id &&
                edge.SourcePortId == GroupBoundarySchema.OutputPortId(updated.ChildGraph!.Nodes[3].Id)) ||
            updated.ChildGraph!.Nodes[0].Name != "Renamed input")
            throw new InvalidOperationException("Updating an asset did not preserve its boundary connections.");
        _ = VfxProgram.Compile(after);
        if (!graph.History.Undo() || !GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()))
            throw new InvalidOperationException("Undo did not restore the linked group revision and wiring.");
    }

    private static void VerifyBoundaryScopeValidation()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(group);
        graph.CreateNode(new GraphNodeKind("GroupInputFloat"));
        var child = graph.CreateCurrentSnapshot();
        GraphValidator.Validate(child);
        var rootNode = child.Nodes.Single();
        var document = new LiberaFxProjectDocument
        {
            FormatVersion = 1,
            Nodes = [new(rootNode.Id, rootNode.Name, rootNode.Kind.Value, rootNode.X, rootNode.Y,
                Parameters: rootNode.Parameters)]
        };
        using (var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document)))
        {
            try
            {
                _ = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
                throw new InvalidOperationException("A project with a root group boundary was accepted.");
            }
            catch (InvalidDataException error) when (error.Message.Contains("directly inside a Graph Group"))
            {
            }
        }
        graph.NavigateToGraph(0);
        var spawnable = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        var saved = graph.CreateSnapshot();
        var invalid = saved with { Nodes = saved.Nodes.Select(node => node.Id == spawnable.Id
            ? node with { ChildGraph = child } : node).Where(node => node.Id != group.Id).ToArray() };
        try
        {
            GraphValidator.Validate(invalid);
            throw new InvalidOperationException("A boundary directly inside a Spawnable Group was accepted.");
        }

        catch (InvalidDataException error) when (error.Message.Contains("directly inside a Graph Group"))
        {
        }
        var malformed = saved with { Nodes = saved.Nodes.Select(node => node.Id == group.Id
            ? node with { ChildGraph = new GraphSnapshot([null!], []) } : node).ToArray() };
        try
        {
            GraphValidator.Validate(malformed);
            throw new InvalidOperationException("A null group child node was accepted.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void VerifyDefaultLabels()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(group);
        graph.CreateNode(new GraphNodeKind("GroupInputFloat"));
        var second = graph.CreateNode(new GraphNodeKind("GroupInputFloat"));
        graph.CreateNode(new GraphNodeKind("GroupOutputFloat"));
        graph.CreateNode(new GraphNodeKind("GroupOutputFloat"));
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        if (!group.InputPorts.Select(port => port.Name).SequenceEqual(["Scalar", "Scalar 2"]) ||
            !group.OutputPorts.Select(port => port.Name).SequenceEqual(["Scalar", "Scalar 2"]))
            throw new InvalidOperationException("Same-type default group pins must be distinguishable.");
        graph.EnterGraph(group);
        graph.GetNode(second.Id).Name = "Speed";
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        if (group.InputPorts.Single(port => port.Id == GroupBoundarySchema.InputPortId(second.Id)).Name != "Speed")
            throw new InvalidOperationException("A renamed group boundary must use its authored name.");
    }

    private static void VerifyMatrixBoundary()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(group);
        var input = graph.CreateNode(new GraphNodeKind("GroupInputMatrix"));
        var output = graph.CreateNode(new GraphNodeKind("GroupOutputMatrix"));
        if (!graph.TryConnectNodes(input, output, out _))
            throw new InvalidOperationException("Cannot connect Matrix boundaries internally.");
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        var transform = graph.CreateNode(new GraphNodeKind("Transform"));
        graph.SetParameter(transform, "position", JsonSerializer.SerializeToElement(new[] { 10d, 20d, 30d }));
        var point = graph.CreateNode(new GraphNodeKind("TransformPoint"));
        graph.SetParameter(point, "point", JsonSerializer.SerializeToElement(new[] { 1d, 0d, 0d }));
        if (!graph.TryConnectNodes(transform, group, out _, GroupBoundarySchema.InputPortId(input.Id), "value") ||
            !graph.TryConnectNodes(group, point, out _, "matrix", GroupBoundarySchema.OutputPortId(output.Id)))
            throw new InvalidOperationException("Cannot connect Matrix boundaries in the parent.");
        var snapshot = graph.CreateSnapshot();
        var program = VfxProgram.Compile(snapshot);
        VerifyNativeNumeric(program.ToBinary(), point.Id, [11d, 20d, 30d]);
        var describe = LiberaFxMcpTools.DescribeRuntimeOutputs(new ActiveGraphProvider(graph), group.Id.ToString());
        Dispatcher.UIThread.RunJobs();
        using (var json = JsonDocument.Parse(describe.GetAwaiter().GetResult()))
            if (json.RootElement.GetProperty("outputs")[0].GetProperty("bindings").GetArrayLength() != 4)
                throw new InvalidOperationException("A Group Matrix output must expose four runtime rows.");
        var copy = new GraphWorkspaceViewModel(new UndoRedoManager());
        copy.PasteSnapshot(snapshot, 100, 100);
        _ = VfxProgram.Compile(copy.CreateSnapshot());
        using var stream = new MemoryStream();
        new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
        stream.Position = 0;
        if (!GraphSnapshot.ContentEquals(snapshot, ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult().Graph))
            throw new InvalidOperationException("Matrix boundaries did not survive project persistence.");
        graph.DisconnectConnection(graph.Connections.Single(edge => edge.Target == group));
        VerifyNativeNumeric(VfxProgram.Compile(graph.CreateSnapshot()).ToBinary(), point.Id, [1d, 0d, 0d]);
        if (!graph.History.Undo() || !GraphSnapshot.ContentEquals(snapshot, graph.CreateSnapshot()))
            throw new InvalidOperationException("Undo did not restore Matrix boundary wiring.");
        graph.EnterGraph(graph.GetNode(group.Id));
        var nested = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(nested);
        graph.CreateNode(new GraphNodeKind("GroupOutputMatrix"));
        graph.NavigateToGraph(1);
        var nestedDescribe = LiberaFxMcpTools.DescribeRuntimeOutputs(new ActiveGraphProvider(graph), nested.Id.ToString());
        Dispatcher.UIThread.RunJobs();
        using var nestedJson = JsonDocument.Parse(nestedDescribe.GetAwaiter().GetResult());
        if (nestedJson.RootElement.GetProperty("outputs")[0].GetProperty("bindings").GetArrayLength() != 4)
            throw new InvalidOperationException("MCP could not resolve a nested Matrix group output.");
        var localInput = graph.GetNode(input.Id);
        var localPoint = graph.CreateNode(new GraphNodeKind("TransformPoint"));
        if (!graph.TryConnectNodes(localInput, localPoint, out _, "matrix"))
            throw new InvalidOperationException("Cannot connect Matrix input to a local consumer.");
        var inputDescribe = LiberaFxMcpTools.DescribeRuntimeOutputs(new ActiveGraphProvider(graph), input.Id.ToString());
        var pointDescribe = LiberaFxMcpTools.DescribeRuntimeOutputs(new ActiveGraphProvider(graph), localPoint.Id.ToString());
        Dispatcher.UIThread.RunJobs();
        using var inputJson = JsonDocument.Parse(inputDescribe.GetAwaiter().GetResult());
        using var pointJson = JsonDocument.Parse(pointDescribe.GetAwaiter().GetResult());
        if (inputJson.RootElement.GetProperty("outputs")[0].GetProperty("bindings").GetArrayLength() != 4 ||
            pointJson.RootElement.GetProperty("outputs")[0].GetProperty("bindings").GetArrayLength() != 1)
            throw new InvalidOperationException("MCP could not resolve nodes connected to a local Matrix boundary.");
    }

    private static void VerifyNestedMatrixBoundary()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var outer = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(outer);
        var outerInput = graph.CreateNode(new GraphNodeKind("GroupInputMatrix"));
        var outerOutput = graph.CreateNode(new GraphNodeKind("GroupOutputMatrix"));
        var inner = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(inner);
        var innerInput = graph.CreateNode(new GraphNodeKind("GroupInputMatrix"));
        var innerOutput = graph.CreateNode(new GraphNodeKind("GroupOutputMatrix"));
        if (!graph.TryConnectNodes(innerInput, innerOutput, out _))
            throw new InvalidOperationException("Cannot connect nested Matrix boundaries.");
        graph.NavigateToGraph(1);
        inner = graph.GetNode(inner.Id);
        if (!graph.TryConnectNodes(graph.GetNode(outerInput.Id), inner, out _,
                GroupBoundarySchema.InputPortId(innerInput.Id)) ||
            !graph.TryConnectNodes(inner, graph.GetNode(outerOutput.Id), out _,
                sourcePortId: GroupBoundarySchema.OutputPortId(innerOutput.Id)))
            throw new InvalidOperationException("Cannot connect nested Matrix group.");
        graph.NavigateToGraph(0);
        outer = graph.GetNode(outer.Id);
        var transform = graph.CreateNode(new GraphNodeKind("Transform"));
        graph.SetParameter(transform, "position", JsonSerializer.SerializeToElement(new[] { 10d, 20d, 30d }));
        var point = graph.CreateNode(new GraphNodeKind("TransformPoint"));
        graph.SetParameter(point, "point", JsonSerializer.SerializeToElement(new[] { 1d, 0d, 0d }));
        if (!graph.TryConnectNodes(transform, outer, out _, GroupBoundarySchema.InputPortId(outerInput.Id), "value") ||
            !graph.TryConnectNodes(outer, point, out _, "matrix", GroupBoundarySchema.OutputPortId(outerOutput.Id)))
            throw new InvalidOperationException("Cannot connect outer Matrix group.");
        VerifyNativeNumeric(VfxProgram.Compile(graph.CreateSnapshot()).ToBinary(), point.Id, [11d, 20d, 30d]);
    }

    private static void ExportUnrealFixture(string directory)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var group = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(group);
        var input = graph.CreateNode(new GraphNodeKind("GroupInputVector4"));
        var output = graph.CreateNode(new GraphNodeKind("GroupOutputVector4"));
        if (!graph.TryConnectNodes(input, output, out _))
            throw new InvalidOperationException("Cannot connect render fixture group.");
        graph.NavigateToGraph(0);
        group = graph.GetNode(group.Id);
        var source = graph.CreateNode(GraphNodeKind.FloatX4);
        var color = new[] { 0.2d, 0.5d, 0.8d, 1d };
        graph.SetParameter(source, "value", JsonSerializer.SerializeToElement(color));
        var asset = new EngineAsset(SolarSystemSample.SphereAsset, "Group color target", "model", "Unreal",
            Materials: [new(0, "builtin:unlit-color", "Unlit color", [
                new("Color", "vector", JsonSerializer.SerializeToElement(new[] { 1d, 1d, 1d, 1d }))
            ])]);
        var draw = RenderNodeAuthoring.CreateModel(graph, asset);
        if (!graph.TryConnectNodes(source, group, out _, GroupBoundarySchema.InputPortId(input.Id)) ||
            !graph.TryConnectNodes(group, draw, out _, draw.InputPorts.Single(port => port.Name == "Color").Id,
                GroupBoundarySchema.OutputPortId(output.Id)))
            throw new InvalidOperationException("Cannot connect group color to render fixture.");
        var matrixGroup = graph.CreateNode(GraphNodeKind.GraphGroup);
        graph.EnterGraph(matrixGroup);
        var matrixInput = graph.CreateNode(new GraphNodeKind("GroupInputMatrix"));
        var matrixOutput = graph.CreateNode(new GraphNodeKind("GroupOutputMatrix"));
        if (!graph.TryConnectNodes(matrixInput, matrixOutput, out _))
            throw new InvalidOperationException("Cannot connect fixture Matrix boundaries.");
        graph.NavigateToGraph(0);
        matrixGroup = graph.GetNode(matrixGroup.Id);
        draw = graph.GetNode(draw.Id);
        var transform = graph.CreateNode(new GraphNodeKind("Transform"));
        var position = new[] { 10d, 20d, 30d };
        graph.SetParameter(transform, "position", JsonSerializer.SerializeToElement(position));
        var point = graph.CreateNode(new GraphNodeKind("TransformPoint"));
        if (!graph.TryConnectNodes(transform, matrixGroup, out _, GroupBoundarySchema.InputPortId(matrixInput.Id), "value") ||
            !graph.TryConnectNodes(matrixGroup, point, out _, "matrix", GroupBoundarySchema.OutputPortId(matrixOutput.Id)) ||
            !graph.TryConnectNodes(point, draw, out _, "position", "value"))
            throw new InvalidOperationException("Cannot connect fixture Matrix to the rendered position.");
        var snapshot = graph.CreateSnapshot();
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "program.lfxb"), VfxProgram.Compile(snapshot).ToBinary());
        File.WriteAllBytes(Path.Combine(directory, "render-config.json"), RenderProgramBindings.CreateScene(snapshot, 1));
        File.WriteAllText(Path.Combine(directory, "expected.json"), JsonSerializer.Serialize(new
        {
            frames = new[] { new { timeSeconds = 0d, nodes = new[] { new
            {
                nodeId = draw.Id, position, scale = new[] { 1d, 1d, 1d },
                rotation = new[] { 0d, 0d, 0d }, parameters = new[] { new { name = "Color", slot = 0, value = color } }
            } } } }
        }));
        var signalGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var signalGroup = signalGraph.CreateNode(GraphNodeKind.GraphGroup);
        signalGraph.EnterGraph(signalGroup);
        var signalInput = signalGraph.CreateNode(new GraphNodeKind("GroupInputSignal"));
        var signalOutput = signalGraph.CreateNode(new GraphNodeKind("GroupOutputSignal"));
        if (!signalGraph.TryConnectNodes(signalInput, signalOutput, out _))
            throw new InvalidOperationException("Cannot connect fixture Signal boundaries.");
        signalGraph.NavigateToGraph(0);
        signalGroup = signalGraph.GetNode(signalGroup.Id);
        var signalSource = signalGraph.CreateNode(GraphNodeKind.Signal);
        var signalTarget = signalGraph.CreateNode(GraphNodeKind.Signal);
        signalGraph.CreateNode(GraphNodeKind.FloatFCurve);
        if (!signalGraph.TryConnectNodes(signalSource, signalGroup, out _, GroupBoundarySchema.InputPortId(signalInput.Id)) ||
            !signalGraph.TryConnectNodes(signalGroup, signalTarget, out _,
                sourcePortId: GroupBoundarySchema.OutputPortId(signalOutput.Id)))
            throw new InvalidOperationException("Cannot connect fixture Signal in the parent.");
        File.WriteAllBytes(Path.Combine(directory, "signal-program.lfxb"),
            SpawnableGroupPackage.Compile(signalGraph.CreateSnapshot()).ParentBytecode);
        File.WriteAllText(Path.Combine(directory, "signal-entry.txt"), signalSource.Id.ToString("D"));
        var secondTarget = signalGraph.CreateNode(GraphNodeKind.Signal);
        if (!signalGraph.TryConnectNodes(signalSource, secondTarget, out _))
            throw new InvalidOperationException("Cannot connect fixture Signal fan-out.");
        File.WriteAllBytes(Path.Combine(directory, "signal-fanout.lfxb"),
            SpawnableGroupPackage.Compile(signalGraph.CreateSnapshot()).ParentBytecode);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NativeValue { public double X, Y, Z, W; public uint Count, Reserved; }
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProgram(byte[] bytes, ulong length, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateParent(byte[] bytes, ulong length, [In] NativeBinding[] bindings, uint count, out ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateInstance(ulong program, out ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Release(ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Bind(ulong instance, byte[] id, out uint binding);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EvaluateValue(ulong instance, double time, [In] uint[] bindings, uint count, out NativeValue value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Evaluate(ulong instance, double time, IntPtr bindings, uint count, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreatePool(ulong program, byte[] container, uint max, ulong budget, uint events, out ulong pool);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Dispatch(ulong instance, byte[] id, double time, uint budget);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProcessPool(ulong pool, uint max, out uint processed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextEvent(ulong pool, out NativeEvent value, out uint hasEvent);

    private static void VerifyNativeNumeric(byte[] bytes, Guid target, double[]? expected = null)
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrEmpty(path)) return;
        var library = NativeLibrary.Load(path);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        static void Check(int result) { if (result != 0) throw new InvalidOperationException($"Native group runtime error {result}."); }
        ulong program = 0, instance = 0;
        try
        {
            Check(Fn<CreateProgram>("libra_program_create")(bytes, (ulong)bytes.Length, out program));
            Check(Fn<CreateInstance>("libra_instance_create")(program, out instance));
            Check(Fn<Bind>("libra_bind_value")(instance, target.ToByteArray(true), out var binding));
            Check(Fn<EvaluateValue>("libra_evaluate")(instance, 0, [binding], 1, out var value));
            expected ??= [7d];
            var actual = new[] { value.X, value.Y, value.Z, value.W };
            if (value.Count != expected.Length || !actual.Take(expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("Native runtime did not propagate the parent value through the group.");
        }
        finally
        {
            if (instance != 0) Fn<Release>("libra_instance_release")(instance);
            if (program != 0) Fn<Release>("libra_program_release")(program);
            NativeLibrary.Free(library);
        }
    }

    private static void VerifyNativeSignal(SpawnableGroupPackage package, Guid source)
    {
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY");
        if (string.IsNullOrEmpty(path)) return;
        var library = NativeLibrary.Load(path);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        static void Check(int result) { if (result != 0) throw new InvalidOperationException($"Native group runtime error {result}."); }
        ulong child = 0, pool = 0, parent = 0, instance = 0;
        try
        {
            var template = package.Templates.Single();
            Check(Fn<CreateProgram>("libra_program_create")(template.Bytecode, (ulong)template.Bytecode.Length, out child));
            Check(Fn<CreatePool>("libra_generator_pool_create")(child, template.ContainerId.ToByteArray(true), 2, 65536, 8, out pool));
            var binding = new NativeBinding { Container = template.ContainerId.ToByteArray(true),
                Type = template.SignalTypeId.ToByteArray(true), Pool = pool,
                Widths = new byte[4], Colors = new byte[4], Nodes = new byte[64] };
            var malformed = package.ParentBytecode[..^1];
            if (Fn<CreateParent>("libra_program_create_spawnable")(malformed, (ulong)malformed.Length,
                    [binding], 1, out var rejected) == 0 || rejected != 0)
                throw new InvalidOperationException("Malformed v4 group signal bytecode was accepted.");
            Check(Fn<CreateParent>("libra_program_create_spawnable")(package.ParentBytecode,
                (ulong)package.ParentBytecode.Length, [binding], 1, out parent));
            Check(Fn<CreateInstance>("libra_instance_create")(parent, out instance));
            Check(Fn<Evaluate>("libra_evaluate")(instance, 0, IntPtr.Zero, 0, IntPtr.Zero));
            Check(Fn<Dispatch>("libra_dispatch_signal")(instance, source.ToByteArray(true), 0, 20));
            Check(Fn<ProcessPool>("libra_generator_process_spawns")(pool, 2, out var processed));
            Check(Fn<NextEvent>("libra_generator_next_event")(pool, out var spawned, out var hasEvent));
            if (processed != 1 || hasEvent != 1 || spawned.Kind != 1)
                throw new InvalidOperationException("Native group signal did not reach the explicitly connected Spawnable Group.");
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
