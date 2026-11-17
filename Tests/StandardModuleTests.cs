using System.Security.Cryptography;
using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels;

internal static class StandardModuleTests
{
    private static readonly Guid PositionStepId = Guid.Parse("29000000-0000-4000-8000-000000000001");
    private static readonly Guid VelocityStepId = Guid.Parse("29000000-0000-4000-8000-000000000002");
    private static readonly Guid InitialVelocityId = Guid.Parse("29000000-0000-4000-8000-000000000003");
    private static readonly Guid EffectCurveId = Guid.Parse("29000000-0000-4000-8000-000000000004");
    private static readonly Guid WorldLookAtId = Guid.Parse("b17b0000-0000-4000-8000-000000000001");
    private static readonly Guid SphericalBillboardId = Guid.Parse("b17b0000-0000-4000-8000-000000000002");
    private static readonly Guid TreeBillboardId = Guid.Parse("b17b0000-0000-4000-8000-000000000003");

    public static void Run(string? fixtureDirectory = null)
    {
        if (fixtureDirectory is not null && !Path.IsPathFullyQualified(fixtureDirectory))
            throw new ArgumentException("Use an absolute isolated standard-module fixture directory.", nameof(fixtureDirectory));
        var ownsRoot = fixtureDirectory is null;
        var root = ownsRoot
            ? Path.Combine(Path.GetTempPath(), "liberafx-standard-modules-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(fixtureDirectory!);
        Directory.CreateDirectory(root);
        try { Verify(root, exportNativeFixtures: !ownsRoot); }
        finally
        {
            if (ownsRoot && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Verify(string root, bool exportNativeFixtures)
    {
        var modulesDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Modules");
        var profiles = new TeamProfilesService(Path.Combine(root, "profiles.json"));
        using var catalog = new GroupAssetCatalog(profiles, modulesDirectory);
        var expectedIds = new HashSet<Guid>
        {
            PositionStepId, VelocityStepId, InitialVelocityId, EffectCurveId,
            WorldLookAtId, SphericalBillboardId, TreeBillboardId
        };
        var standard = catalog.Assets.Where(asset => expectedIds.Contains(asset.Id)).ToArray();
        if (standard.Length != expectedIds.Count || standard.Any(asset =>
                asset.Origin != GroupAssetOrigin.Standard || !asset.Locked || asset.Revision != 1) ||
            catalog.Warnings.Length != 0 || catalog.HasLibraryDirectory)
            throw new InvalidOperationException("The read-only standard assets did not load into a fresh project catalog.");
        if (standard.Any(asset => asset.Graph.Nodes.Any(node => node.Kind.Value == "LifeTime")))
            throw new InvalidOperationException("A standard module silently introduced a particle LifeTime.");
        VerifyBillboardAssets(catalog);

        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var nodeList = new NodeCatalogViewModel(new ActiveGraphProvider(workspace), catalog);
        nodeList.SearchText = "euler";
        var eulerResults = nodeList.Groups.SelectMany(group => group.Nodes).Where(entry => entry.GroupAssetId is not null).ToArray();
        var eulerIds = eulerResults.Select(entry => entry.GroupAssetId.GetValueOrDefault()).ToHashSet();
        if (!eulerIds.SetEquals([PositionStepId, VelocityStepId]))
            throw new InvalidOperationException("Standard motion groups were not searchable by their English terms.");
        nodeList.SearchText = "加速度";
        if (!nodeList.Groups.SelectMany(group => group.Nodes).Any(entry => entry.GroupAssetId == VelocityStepId))
            throw new InvalidOperationException("Standard modules were not searchable by Japanese keywords.");
        nodeList.SearchText = "spherical";
        if (!nodeList.Groups.SelectMany(group => group.Nodes).Any(entry => entry.GroupAssetId == SphericalBillboardId))
            throw new InvalidOperationException("The spherical billboard standard group was not searchable.");
        nodeList.SearchText = "樹木";
        if (!nodeList.Groups.SelectMany(group => group.Nodes).Any(entry => entry.GroupAssetId == TreeBillboardId))
            throw new InvalidOperationException("The tree billboard standard group was not searchable by Japanese keywords.");

        foreach (var id in new[] { WorldLookAtId, SphericalBillboardId, TreeBillboardId })
        {
            var inserted = workspace.CreateGroupAssetNode(id, 120, 100);
            var billboardPane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(workspace));
            var localizedDescriptions = catalog.Metadata(id)!.Parameters
                .Select(item => LocalizationService.Shared.GetOrDefault(item.DescriptionKey, string.Empty))
                .ToHashSet(StringComparer.Ordinal);
            if (inserted.GroupAsset?.Id != id || inserted.ChildGraph?.ExposedItems?.Count != 8 ||
                billboardPane.Parameters.Count != 8 ||
                billboardPane.Parameters.Any(parameter => !localizedDescriptions.Contains(parameter.Description)))
                throw new InvalidOperationException($"Standard billboard {id} was not insertable with all public parameters.");
        }

        var positionAsset = catalog.Load(PositionStepId);
        var sourceHashes = expectedIds.ToDictionary(id => id, id => SHA256.HashData(File.ReadAllBytes(
            Path.Combine(modulesDirectory, id.ToString("N") + GroupAssetLibrary.FileExtension))));
        var group = workspace.CreateGroupAssetNode(PositionStepId, 120, 100);
        var groupId = group.Id;
        if (group.GroupAsset?.Id != PositionStepId || group.GroupAsset.Revision != positionAsset.Revision ||
            group.ChildGraph is null || group.ChildGraph.Nodes.Any(node =>
                positionAsset.Graph.Nodes.Any(original => original.Id == node.Id)))
            throw new InvalidOperationException("Inserting a standard module did not create an isolated linked group instance.");

        var pane = new NodePropertiesPaneViewModel(new ActiveGraphProvider(workspace));
        var scaleName = LocalizationService.Shared.Get("Modules.Parameter.TimeScale");
        if (!pane.Parameters.Any(parameter => parameter.Name == scaleName ||
                parameter.Name.EndsWith(" / " + scaleName, StringComparison.Ordinal)) ||
            !pane.IsStandardGroup || pane.CanCloneGroup ||
            pane.CloneGroupCommand.CanExecute(null))
            throw new InvalidOperationException("Standard public parameters or read-only clone affordance were not exposed.");
        if (workspace.EnterGraph(group))
            throw new InvalidOperationException("A built-in group asset was editable in place.");

        VerifyCompiledModules(catalog, positionAsset, root, exportNativeFixtures);
        VerifyExtensionRegistration(catalog, positionAsset, nodeList);

        var projectLibraryDirectory = Path.Combine(root, "project-groups");
        if (!profiles.Save(null, "Issue 29 isolated project", projectLibraryDirectory))
            throw new InvalidOperationException(profiles.Status);
        if (!pane.CanCloneGroup || !pane.CloneGroupCommand.CanExecute(null))
            throw new InvalidOperationException("A standard group could not be cloned after a project library was configured.");
        pane.CloneGroupCommand.Execute(null);
        var clonedReference = workspace.GetNode(groupId).GroupAsset
            ?? throw new InvalidOperationException("Cloning removed the linked group reference.");
        var projectLibrary = new GroupAssetLibrary(projectLibraryDirectory);
        var clone = projectLibrary.Load(clonedReference.Id);
        if (clone.Origin != GroupAssetOrigin.Project || clone.Id == PositionStepId ||
            clone.Revision != 1 || clone.Locked ||
            catalog.Load(PositionStepId).Revision != 1 || !workspace.EnterGraph(workspace.GetNode(groupId)))
            throw new InvalidOperationException("The clone was not independently editable or changed the standard asset.");
        workspace.NavigateToGraph(0);

        foreach (var id in expectedIds)
        {
            try
            {
                projectLibrary.Save(id, 1, "attempted standard overwrite", positionAsset.Graph);
                throw new InvalidOperationException("The project API overwrote a registered standard group ID.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Standard group", StringComparison.Ordinal)) { }
            try
            {
                projectLibrary.SetLocked(id, false);
                throw new InvalidOperationException("The project API unlocked a registered standard group ID.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Standard group", StringComparison.Ordinal)) { }
            if (File.Exists(Path.Combine(projectLibraryDirectory, id.ToString("N") + GroupAssetLibrary.FileExtension)))
                throw new InvalidOperationException("A standard asset ID appeared in the project library.");
        }

        if (!workspace.History.Undo() || workspace.GetNode(groupId).GroupAsset?.Id != PositionStepId ||
            !workspace.History.Redo() || workspace.GetNode(groupId).GroupAsset?.Id != clone.Id)
            throw new InvalidOperationException("The standard-to-project clone link was not undoable.");
        for (var index = 0; index < sourceHashes.Count; ++index)
        {
            var id = sourceHashes.Keys.ElementAt(index);
            var current = SHA256.HashData(File.ReadAllBytes(Path.Combine(modulesDirectory,
                id.ToString("N") + GroupAssetLibrary.FileExtension)));
            if (!sourceHashes[id].AsSpan().SequenceEqual(current))
                throw new InvalidOperationException("Cloning changed a bundled standard asset file.");
        }
    }

    private static void VerifyCompiledModules(
        GroupAssetCatalog catalog, GroupAsset positionAsset, string root, bool exportNativeFixtures)
    {
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
        var previousPosition = workspace.CreateNode(GraphNodeKind.FloatX3);
        var velocity = workspace.CreateNode(GraphNodeKind.FloatX3);
        var deltaTime = workspace.CreateNode(GraphNodeKind.Float);
        workspace.SetParameter(previousPosition, "value", JsonSerializer.SerializeToElement(new[] { 1d, 2d, 3d }));
        workspace.SetParameter(velocity, "value", JsonSerializer.SerializeToElement(new[] { 4d, 5d, 6d }));
        workspace.SetParameter(deltaTime, "value", JsonSerializer.SerializeToElement(0.25d));
        var group = workspace.CreateGroupAssetNode(PositionStepId, 520, 80);
        var inputs = group.ChildGraph!.Nodes.Where(node => GroupBoundarySchema.IsInput(node.Kind)).ToArray();
        var inputPosition = inputs.Single(node => node.Name == "Previous Position");
        var inputVelocity = inputs.Single(node => node.Name == "Velocity");
        var inputDeltaTime = inputs.Single(node => node.Name == "Delta Time (seconds)");
        if (!workspace.TryConnectNodes(previousPosition, group, out var error,
                GroupBoundarySchema.InputPortId(inputPosition.Id)) ||
            !workspace.TryConnectNodes(velocity, group, out error, GroupBoundarySchema.InputPortId(inputVelocity.Id)) ||
            !workspace.TryConnectNodes(deltaTime, group, out error, GroupBoundarySchema.InputPortId(inputDeltaTime.Id)))
            throw new InvalidOperationException($"Could not connect standard module inputs: {error}");

        var scaleItem = positionAsset.Graph.ExposedItems!.Single(item => item.Name == "Time Scale");
        workspace.SetGroupInstanceValue(group,
            new(scaleItem.Id, [new(JsonSerializer.SerializeToElement(1.5d))]));
        var program = VfxProgram.Compile(workspace.CreateSnapshot());
        var childIds = group.ChildGraph.Nodes.Select(node => node.Id).ToHashSet();
        var operations = program.TypedInstructions.Where(instruction => childIds.Contains(instruction.NodeId))
            .Select(instruction => instruction.Opcode).ToArray();
        if (!operations.Contains(VfxOpcode.Multiply) ||
            !operations.Contains(VfxOpcode.ScalarToVector3) ||
            !operations.Contains(VfxOpcode.MultiplyVector3) ||
            !operations.Contains(VfxOpcode.AddVector3))
            throw new InvalidOperationException("The position-step graph did not compile to its actual primitive composition.");
        var timeScaleNode = group.ChildGraph.Nodes.Single(node => node.Name == "Time Scale");
        var scaleInstruction = program.TypedInstructions.Single(instruction => instruction.NodeId == timeScaleNode.Id);
        if (scaleInstruction.DefaultA[0] != 1.5d)
            throw new InvalidOperationException("A public Time Scale instance value did not reach compiled bytecode.");
        var resolved = new GroupCompilationResolver([]).Resolve(workspace.CreateSnapshot());
        var resolvedProgram = VfxProgram.Compile(resolved);
        var resolvedScale = resolved.Nodes.Single(node => node.GroupAsset?.Id == PositionStepId)
            .ChildGraph!.Nodes.Single(node => node.Name == "Time Scale");
        if (resolvedProgram.TypedInstructions.Single(instruction => instruction.NodeId == resolvedScale.Id).DefaultA[0] != 1.5d)
            throw new InvalidOperationException("The built-in library resolver lost a standard module or its exposed instance value.");

        foreach (var id in new[] { VelocityStepId, InitialVelocityId, EffectCurveId })
        {
            var candidate = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            candidate.CreateGroupAssetNode(id, 100, 100);
            var bytecode = VfxProgram.Compile(candidate.CreateSnapshot());
            if (id == EffectCurveId)
            {
                var curve = catalog.Load(id).Graph.Nodes.Single(node => node.Kind == GraphNodeKind.FloatFCurve).Curve!;
                if (bytecode.CurveInstructions.Count != 1 || FCurve.Evaluate(curve, 0.25d) != 0.25d)
                    throw new InvalidOperationException("The effect-time F-Curve did not compile with seconds-based key data.");
            }
            else if (bytecode.TypedInstructions.Count == 0)
                throw new InvalidOperationException($"Standard module {id} compiled to no executable instructions.");
        }

        if (exportNativeFixtures)
            ExportNativeFixtures(catalog, root);
    }

    private static void VerifyBillboardAssets(GroupAssetCatalog catalog)
    {
        foreach (var (id, kind, expectedName) in new[]
                 {
                     (WorldLookAtId, "WorldLookAt", "World Look At"),
                     (SphericalBillboardId, "SphericalBillboard", "Spherical Billboard"),
                     (TreeBillboardId, "TreeBillboard", "Tree Billboard")
                 })
        {
            var asset = catalog.Load(id);
            var metadata = catalog.Metadata(id)
                ?? throw new InvalidOperationException($"Billboard metadata for {kind} was not loaded.");
            if (asset.Name != expectedName ||
                !GraphSnapshot.ContentEquals(asset.Graph, BillboardGraphLibrary.Create(kind)) ||
                asset.Graph.ExposedItems?.Count != 8 || metadata.Parameters.Count != 8 ||
                !metadata.Parameters.Select(item => item.ItemId).ToHashSet()
                    .SetEquals(asset.Graph.ExposedItems!.Select(item => item.Id)) ||
                !asset.Graph.Nodes.Any(node => GroupBoundarySchema.IsOutput(node.Kind) && node.Name == "World Matrix") ||
                !asset.Graph.Nodes.Any(node => GroupBoundarySchema.IsOutput(node.Kind) && node.Name == "Valid"))
                throw new InvalidOperationException($"The registered {kind} asset does not match its public graph interface.");

            var defaults = asset.Graph.Nodes.Where(node => GroupBoundarySchema.IsInput(node.Kind))
                .ToDictionary(node => node.Name);
            AssertVectorInput(defaults, "Target", [1, 0, 0]);
            AssertVectorInput(defaults, "World Up", [0, 0, 1]);
            AssertVectorInput(defaults, "Local Forward", [1, 0, 0]);
            AssertVectorInput(defaults, "Local Up", [0, 0, 1]);
        }

        static void AssertVectorInput(
            IReadOnlyDictionary<string, GraphNodeSnapshot> inputs, string name, double[] expected)
        {
            var node = inputs[name];
            var value = node.Parameters?.Single(parameter => parameter.Id == "value").Value
                ?? throw new InvalidOperationException($"Billboard input {name} has no default.");
            if (!value.EnumerateArray().Select(item => item.GetDouble()).SequenceEqual(expected))
                throw new InvalidOperationException($"Billboard input {name} has unexpected axis defaults.");
        }
    }

    private static void ExportNativeFixtures(GroupAssetCatalog catalog, string directory)
    {
        Directory.CreateDirectory(directory);
        Export(PositionStepId, "position-step", new Dictionary<string, object>
        {
            ["Previous Position"] = new[] { 1d, 2, 3 },
            ["Velocity"] = new[] { 4d, 5, 6 },
            ["Delta Time (seconds)"] = 0.25d
        }, "Time Scale", 1.5d, "Next Position", 0, [2.5, 3.875, 5.25]);
        Export(VelocityStepId, "velocity-step", new Dictionary<string, object>
        {
            ["Previous Velocity"] = new[] { 1d, 2, 3 },
            ["Acceleration"] = new[] { 2d, -4, 8 },
            ["Delta Time (seconds)"] = 0.5d
        }, "Time Scale", 1.5d, "Next Velocity", 0, [2.5, -1, 9]);
        Export(InitialVelocityId, "initial-velocity", new Dictionary<string, object>
        {
            ["Direction"] = new[] { 0d, 0, 1 },
            ["Speed"] = 2d
        }, "Speed Scale", 1.5d, "Velocity", 0, [0, 0, 3]);
        Export(EffectCurveId, "effect-time-curve", new Dictionary<string, object>(),
            null, null, "Curve Value", 0.25, [0.25]);

        void Export(
            Guid moduleId,
            string name,
            IReadOnlyDictionary<string, object> values,
            string? exposedName,
            double? exposedValue,
            string outputName,
            double seconds,
            double[] expected)
        {
            var workspace = new GraphWorkspaceViewModel(new UndoRedoManager(), catalog);
            var group = workspace.CreateGroupAssetNode(moduleId, 500, 100);
            foreach (var input in group.ChildGraph!.Nodes.Where(node => GroupBoundarySchema.IsInput(node.Kind)))
            {
                if (!values.TryGetValue(input.Name, out var value)) continue;
                var type = input.Kind.Value == "GroupInputFloat" ? GraphNodeKind.Float : GraphNodeKind.FloatX3;
                var source = workspace.CreateNode(type);
                workspace.SetParameter(source, "value", JsonSerializer.SerializeToElement(value));
                if (!workspace.TryConnectNodes(source, group, out var error, GroupBoundarySchema.InputPortId(input.Id)))
                    throw new InvalidOperationException($"Could not connect native fixture input {input.Name}: {error}");
            }

            if (exposedName is not null)
            {
                var exposed = catalog.Load(moduleId).Graph.ExposedItems!.Single(item => item.Name == exposedName);
                workspace.SetGroupInstanceValue(group,
                    new(exposed.Id, [new(JsonSerializer.SerializeToElement(exposedValue!.Value))]));
            }

            var graph = workspace.CreateSnapshot();
            var program = VfxProgram.Compile(graph);
            var lowered = GraphPolymorphism.LowerWithOutputs(
                GroupBoundaryLowering.Lower(GroupExposedItems.Materialize(graph)));
            var boundary = group.ChildGraph!.Nodes.Single(node =>
                GroupBoundarySchema.IsOutput(node.Kind) && node.Name == outputName);
            var definition = GraphNodeDefinition.All.Single(item => item.Kind == boundary.Kind);
            var outputPort = definition.OutputPorts?.FirstOrDefault()?.Id;
            var bindings = lowered.Outputs[(boundary.Id, outputPort)];
            if (bindings.Count == 0) throw new InvalidOperationException($"Native fixture output {outputName} has no runtime binding.");

            File.WriteAllBytes(Path.Combine(directory, name + ".lfxb"), program.ToBinary());
            using var writer = new StreamWriter(Path.Combine(directory, name + ".checks"));
            for (var row = 0; row < bindings.Count; row++)
            {
                var index = program.TypedInstructions.ToList().FindIndex(item => item.NodeId == bindings[row].NodeId);
                if (index < 0) throw new InvalidOperationException($"Native fixture output {outputName} is not compiled.");
                var lanes = bindings.Count == 4 ? expected.Skip(row * 4).Take(4).ToArray() : expected;
                writer.WriteLine($"{seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {index} {lanes.Length} " +
                    string.Join(" ", lanes.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
            }
        }
    }

    private static void VerifyExtensionRegistration(
        GroupAssetCatalog catalog,
        GroupAsset source,
        NodeCatalogViewModel nodeList)
    {
        var id = Guid.Parse("29990000-0000-4000-8000-000000000001");
        var metadata = new StandardGroupMetadata(id,
            "Modules.PositionStep.Name", "Modules.PositionStep.Description", "Modules.Category.Motion",
            "Modules.Phase.UpdateStep", "Modules.Units.PositionStep", "Modules.Inputs.PositionStep",
            "Modules.Outputs.PositionStep", "Modules.State.CallerOwned", "Modules.Backend.GenericPortable",
            ["standard registration", "registry-fixture"], []);
        var asset = source with { Id = id, Name = "Registered extension" };
        catalog.RegisterStandardModule(new StandardGroupModuleDefinition(asset, metadata));
        nodeList.SearchText = "registry-fixture";
        if (catalog.Load(id).Origin != GroupAssetOrigin.Standard ||
            catalog.Metadata(id)?.Id != id ||
            !nodeList.Groups.SelectMany(group => group.Nodes).Any(entry => entry.GroupAssetId == id))
            throw new InvalidOperationException("The public standard-module registration API did not refresh search and insertion entries.");
    }
}
