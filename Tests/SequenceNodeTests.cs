using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Mcp;
using LiberaFx.Services.Project;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Views.Controls;
using LiberaFx.Views.Graph;
using LiberaFx.Services.Compilation;

internal static class SequenceNodeTests
{
    public static void Run(string? directory = null)
    {
        VerifyAuthoring(directory);
        VerifyDynamicPins();
        VerifyNativeExecution();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void VerifyDynamicPins()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"liberafx-dynpin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var typeId = Guid.NewGuid();
            var json = $$"""
            {
              "schemaVersion": 1,
              "typeId": "{{typeId:D}}",
              "namespace": "ProjectTest",
              "kind": "DynamicMix",
              "name": "Dynamic Mix",
              "group": "Test",
              "portType": "Float",
              "portCategory": "Parameter",
              "dynamicPins": [
                { "parameterId": "inputCount", "direction": "input", "idPrefix": "in_", "nameTemplate": "In {1}", "portType": "Float", "portCategory": "Parameter", "defaultCount": 2, "minCount": 1, "maxCount": 4 },
                { "parameterId": "outputCount", "direction": "output", "idPrefix": "out_", "nameTemplate": "Out {1}", "portType": "Float", "portCategory": "Parameter", "defaultCount": 1, "minCount": 1, "maxCount": 3 }
              ]
            }
            """;
            File.WriteAllText(Path.Combine(tempDirectory, "DynamicMix.json"), json);
            var profiles = new TeamProfilesService(Path.Combine(tempDirectory, "profiles.json"));
            Require(profiles.Save(null, "DynamicPinTest", "", tempDirectory), profiles.Status);
            using var catalog = new NodeDefinitionCatalog(profiles);
            var def = catalog.AvailableDefinitions.Single(item => item.TypeId == typeId);
            var graph = new GraphWorkspaceViewModel(new UndoRedoManager(), nodeDefinitions: catalog);
            var node = graph.CreateNode(def.Kind);
            var source = graph.CreateNode(GraphNodeKind.Float);
            Require(node.HasDynamicInputs && node.HasDynamicOutputs && node.HasDynamicPins &&
                    node.InputPorts.Count == 2 && node.OutputPorts.Count == 1,
                "Dynamic pin node did not initialize default ports.");
            node.AddDynamicInputCommand.Execute(null);
            node.AddDynamicOutputCommand.Execute(null);
            Require(node.InputPorts.Count == 3 && node.InputPorts[2].Id == "in_2" && node.InputPorts[2].Name == "In 3" &&
                    node.OutputPorts.Count == 2 && node.OutputPorts[1].Id == "out_1" && node.OutputPorts[1].Name == "Out 2",
                "Dynamic pin add commands did not append expected ports.");
            Require(graph.TryConnectNodes(source, node, out _, "in_2"), "Could not connect to dynamic input port.");
            node.RemoveDynamicInputCommand.Execute(null);
            Require(node.InputPorts.Count == 2 && graph.Connections.Count == 0,
                "Removing dynamic input port did not prune its connection.");
            Require(graph.History.Undo() && node.InputPorts.Count == 3 && graph.Connections.Count == 1,
                "Undoing dynamic pin removal did not restore port and connection.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
        }
    }

    private static void VerifyAuthoring(string? directory)
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var sequence = graph.CreateNode(GraphNodeKind.Sequence);
        var id = sequence.Id;
        var view = new GraphNodeView { DataContext = sequence };
        var window = new Window { Width = 500, Height = 600, Content = view };
        window.Show();
        try
        {
            Pump(window);
            Click(view.FindControl<IconButton>("SequenceAddOutputButton")!, window);
            Require(sequence.OutputPorts.Select(port => port.Id).SequenceEqual(["then_0", "then_1", "then_2"]),
                "The real add button did not create stable ordered Sequence outputs.");
            Require(view.GetVisualDescendants().OfType<Border>().Count(border =>
                border.IsEffectivelyVisible && border.Tag is GraphOutputPortDefinition) == 3,
                "Sequence's dynamic output pins are not rendered.");
            var sink = graph.CreateNode(GraphNodeKind.Signal);
            graph.RenameNode(sink, "Sink");
            Require(graph.TryConnectNodes(sequence, sink, out _, sourcePortId: "then_2"), "Cannot wire the added pin.");
            var other = graph.CreateNode(GraphNodeKind.Signal);
            var beforeRejected = graph.History.CurrentNodeId;
            Require(!graph.TryConnectNodes(sequence, other, out _, sourcePortId: "then_2") &&
                graph.History.CurrentNodeId == beforeRejected, "A Signal output accepted a second destination.");
            Require(graph.TryConnectNodes(sequence, sink, out _, sourcePortId: "then_0"),
                "A Signal input did not accept a second sender.");
            Click(view.FindControl<IconButton>("SequenceRemoveOutputButton")!, window);
            Require(sequence.OutputPorts.Count == 2 && graph.Connections.Count == 1 &&
                graph.Connections.Single().SourcePortId == "then_0",
                "Removing the last pin did not remove only its own connection.");
            Require(graph.History.Undo() && graph.GetNode(id).OutputPorts.Count == 3 && graph.Connections.Count == 2 &&
                graph.History.Redo() && graph.GetNode(id).OutputPorts.Count == 2 && graph.Connections.Count == 1,
                "Sequence pin removal and its connection did not undo/redo together.");
            var provider = new ActiveGraphProvider(graph);
            var set = LiberaFxMcpTools.SetParameter(provider, id.ToString(), "outputs", JsonSerializer.SerializeToElement(6));
            Dispatcher.UIThread.RunJobs();
            set.GetAwaiter().GetResult();
            Require(graph.GetNode(id).OutputPorts.Count == 6, "MCP did not resize Sequence outputs.");
            var snapshot = graph.CreateSnapshot();
            var pasted = new GraphWorkspaceViewModel(new UndoRedoManager());
            pasted.PasteSnapshot(snapshot, 100, 100);
            Require(pasted.Nodes.Single(node => node.IsSequence).OutputPorts.Count == 6 &&
                pasted.Connections.Count == 1 && pasted.Connections.Single().SourcePortId == "then_0" &&
                pasted.Connections.Single().Target.Name == "Sink",
                "Copy/paste changed Sequence pin identities or connections.");
            using var stream = new MemoryStream();
            new ProjectFileService().SaveAsync(stream, graph).GetAwaiter().GetResult();
            stream.Position = 0;
            var loaded = ProjectDocumentReader.LoadAsync(stream).GetAwaiter().GetResult();
            Require(GraphSnapshot.ContentEquals(snapshot, loaded.Graph), "Sequence did not persist.");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "Sequence.lfxproj"), stream.ToArray());
            }
            var invalid = snapshot with { Connections = snapshot.Connections.Concat([
                new GraphConnectionSnapshot(id, other.Id, SourcePortId: "then_0")]).ToArray() };
            try { GraphValidator.Validate(invalid); throw new Exception("Invalid Signal fan-out was accepted."); }
            catch (InvalidDataException) { }
            var parameterGraph = new GraphWorkspaceViewModel(new UndoRedoManager());
            var value = parameterGraph.CreateNode(GraphNodeKind.Float);
            Require(parameterGraph.TryConnectNodes(value, parameterGraph.CreateNode(GraphNodeKind.Float), out _) &&
                parameterGraph.TryConnectNodes(value, parameterGraph.CreateNode(GraphNodeKind.Float), out _),
                "Parameter output fan-out must remain unchanged.");
            Require(graph.History.Undo() && graph.GetNode(id).OutputPorts.Count == 2,
                "MCP Sequence resizing did not use history.");
            foreach (var invalidCount in new[] { 0, SequenceSchema.MaximumOutputs + 1 })
            {
                var before = graph.CreateSnapshot();
                var historyBefore = graph.History.CurrentNodeId;
                try
                {
                    graph.SetParameter(graph.GetNode(id), "outputs", JsonSerializer.SerializeToElement(invalidCount));
                    throw new Exception("Invalid Sequence output count was accepted.");
                }
                catch (InvalidDataException) { }
                Require(GraphSnapshot.ContentEquals(before, graph.CreateSnapshot()) &&
                    graph.History.CurrentNodeId == historyBefore,
                    "Rejected Sequence resizing changed the graph or history.");
            }
        }
        finally { window.Close(); }
    }

    private static void VerifyNativeExecution()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager()) { AutoDestroyWhenIdle = false };
        var start = graph.CreateNode(new GraphNodeKind("OnStart"));
        var sequence = graph.CreateNode(GraphNodeKind.Sequence);
        graph.SetParameter(sequence, "outputs", JsonSerializer.SerializeToElement(6));
        var delay = graph.CreateNode(new GraphNodeKind("Delay"));
        graph.SetParameter(delay, "seconds", JsonSerializer.SerializeToElement(0.5));
        var a = graph.CreateNode(GraphNodeKind.OutputSignal);
        var b = graph.CreateNode(GraphNodeKind.OutputSignal);
        graph.SetParameter(a, "name", JsonSerializer.SerializeToElement("First"));
        graph.SetParameter(b, "name", JsonSerializer.SerializeToElement("Second"));
        var external = graph.CreateNode(GraphNodeKind.InputSignal);
        graph.SetParameter(external, "name", JsonSerializer.SerializeToElement("Again"));
        var group = graph.CreateNode(GraphNodeKind.SpawnableGroup);
        var groupId = group.Id;
        graph.EnterGraph(group);
        var life = graph.CreateNode(new GraphNodeKind("LifeTime"));
        graph.SetParameter(life, "seconds", JsonSerializer.SerializeToElement(2));
        var childSequence = graph.CreateNode(GraphNodeKind.Sequence);
        var childA = graph.CreateNode(new GraphNodeKind("SpawnOutputSignal"));
        var childB = graph.CreateNode(new GraphNodeKind("SpawnOutputSignal"));
        Require(graph.TryConnectNodes(graph.Nodes.Single(node => node.Definition.Kind.Value == "OnStart"),
                    childSequence, out _, "signal", "then") &&
                graph.TryConnectNodes(childSequence, childB, out _, "signal", "then_1") &&
                graph.TryConnectNodes(childSequence, childA, out _, "signal", "then_0"), "Cannot wire child Sequence.");
        graph.NavigateToGraph(0);
        group = graph.GetNode(groupId);
        start = graph.GetNode(start.Id);
        sequence = graph.GetNode(sequence.Id);
        delay = graph.GetNode(delay.Id);
        a = graph.GetNode(a.Id);
        b = graph.GetNode(b.Id);
        external = graph.GetNode(external.Id);
        Require(graph.TryConnectNodes(start, sequence, out _, "signal", "then") &&
            graph.TryConnectNodes(sequence, b, out _, "signal", "then_3") &&
            graph.TryConnectNodes(sequence, a, out _, "signal", "then_2") &&
            graph.TryConnectNodes(sequence, group, out _, "spawn", "then_1") &&
            graph.TryConnectNodes(sequence, delay, out _, "signal", "then_0") &&
            graph.TryConnectNodes(delay, a, out _, "signal", "then") &&
            graph.TryConnectNodes(external, a, out _, "signal", "then"),
            "Cannot wire root Sequence with merged Signal inputs.");
        var package = SpawnableGroupPackage.Compile(graph.CreateSnapshot());
        var binary = SpawnableGroupPackageBinary.Serialize(package);
        var path = Environment.GetEnvironmentVariable("LIBERAFX_NATIVE_TEST_LIBRARY")
            ?? throw new InvalidOperationException("Set LIBERAFX_NATIVE_TEST_LIBRARY for Sequence execution verification.");
        var library = NativeLibrary.Load(path);
        T Fn<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        void Check(int result) => Require(result == 0, $"Native Sequence error {result}.");
        ulong host = 0;
        try
        {
            Check(Fn<CreateHost>("libra_spawnable_host_create")(binary, (ulong)binary.Length,
                32, 4 * 1024 * 1024, 128, 10000, out host));
            Check(Fn<Advance>("libra_spawnable_host_advance")(host, 0, 0));
            var firstIndex = (uint)package.GameIoPorts.ToList().FindIndex(port => port.Name == "First");
            var secondIndex = (uint)package.GameIoPorts.ToList().FindIndex(port => port.Name == "Second");
            var games = new List<uint>();
            void DrainGames()
            {
                for (var index = 0; index < 128; index++)
                {
                    Check(Fn<PopGame>("libra_spawnable_host_next_game_io_event")(host, out var item, out var has));
                    if (has == 0) return;
                    games.Add(item.PortIndex);
                }
                throw new InvalidOperationException("Unexpected unbounded game Signal events.");
            }
            DrainGames();
            Require(games.SequenceEqual([firstIndex, secondIndex]),
                "Sequence followed wire insertion order or waited for Delay/Spawnable completion.");
            var local = new List<Guid>();
            for (var index = 0; index < 128; index++)
            {
                Check(Fn<PopEvent>("libra_spawnable_host_next_event")(host, out var item, out var has));
                if (has == 0) break;
                if (item.Kind == 4) local.Add(new Guid(item.Signal.ToByteArray(), bigEndian: true));
            }
            Require(local.SequenceEqual([childA.Id, childB.Id]), "Spawnable Sequence did not execute top to bottom.");
            Check(Fn<FireSignal>("libra_spawnable_host_fire_game_signal")(host, Encoding.UTF8.GetBytes("Again\0")));
            DrainGames();
            Require(games.SequenceEqual([firstIndex, secondIndex, firstIndex]),
                "A merged Signal input must execute immediately on each independent arrival.");
            Check(Fn<Advance>("libra_spawnable_host_advance")(host, 0.5, 0.5));
            DrainGames();
            Require(games.SequenceEqual([firstIndex, secondIndex, firstIndex, firstIndex]),
                "Delay did not resume independently after the other Sequence outputs.");
        }
        finally
        {
            if (host != 0) Check(Fn<Release>("libra_spawnable_host_release")(host));
            NativeLibrary.Free(library);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 160)]
    private struct GameEvent { [FieldOffset(24)] public uint PortIndex; }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HostEvent { [FieldOffset(0)] public uint Kind; [FieldOffset(48)] public Guid Signal; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateHost(
        byte[] bytes, ulong length, uint active, ulong memory, uint events, uint budget, out ulong host);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Advance(ulong host, double time, double delta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PopGame(ulong host, out GameEvent item, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PopEvent(ulong host, out HostEvent item, out uint has);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FireSignal(ulong host, byte[] name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Release(ulong host);

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
        Require(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Width > 0,
            "The Sequence output button is not usable.");
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        var position = new NativePoint { X = point.X, Y = point.Y };
        var handle = window.TryGetPlatformHandle()!.Handle;
        Require(ScreenToClient(handle, ref position), "Invalid Sequence button coordinates.");
        var packed = (nint)((position.Y << 16) | (position.X & 0xffff));
        SendMessage(handle, 0x0200, 0, packed);
        SendMessage(handle, 0x0201, 1, packed);
        Pump(window);
        SendMessage(handle, 0x0202, 0, packed);
        Pump(window);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nuint wParam, nint lParam);
}
