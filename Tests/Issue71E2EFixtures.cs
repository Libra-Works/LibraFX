using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Project;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Project;
using LiberaFx.ViewModels.Graph;
using LiberaFx.Services.Compilation;

internal static class Issue71E2EFixtures
{
    public static void Write(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var undo = new UndoRedoManager();
        var workspace = new GraphWorkspaceViewModel(undo);
        var draw = workspace.CreateNode(new GraphNodeKind("ModelDraw"));
        var resourceId = Guid.NewGuid();
        workspace.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Sphere.Sphere"));

        // Verify Undo/Redo preserves edits cleanly.
        workspace.SetParameter(draw, "assetPath", JsonSerializer.SerializeToElement("/Engine/BasicShapes/Cube.Cube"));
        if (!undo.CanUndo) throw new InvalidOperationException("Undo was not recorded.");
        undo.Undo();
        if (workspace.CreateSnapshot().Nodes.Single(n => n.Id == draw.Id).Parameters!
            .Single(p => p.Id == "assetPath").Value.GetString() != "/Engine/BasicShapes/Sphere.Sphere")
            throw new InvalidOperationException("Undo did not restore assetPath.");
        undo.Redo();
        if (workspace.CreateSnapshot().Nodes.Single(n => n.Id == draw.Id).Parameters!
            .Single(p => p.Id == "assetPath").Value.GetString() != "/Engine/BasicShapes/Cube.Cube")
            throw new InvalidOperationException("Redo did not reapply assetPath.");

        // Attach Resource Identity metadata, save and reload .lfxproj.
        var embedded = new EmbeddedResourceIdentities(1, [new("assetPath", resourceId)], []);
        var baseSnapshot = workspace.CreateSnapshot();
        var snapshot = baseSnapshot with
        {
            Nodes = baseSnapshot.Nodes.Select(n => n.Id != draw.Id ? n : n with
            {
                Parameters = (n.Parameters ?? []).Append(new NodeParameterSnapshot(
                    ResourceIdentityExport.EmbeddedParameterId,
                    ResourceIdentityExport.EmbeddedParameterType,
                    JsonSerializer.SerializeToElement(embedded))).ToArray()
            }).ToArray()
        };
        var doc = new LiberaFxProjectDocument
        {
            FormatVersion = 1,
            Nodes = snapshot.Nodes.Select(n => new LiberaFxProjectNode(
                n.Id, n.Name, n.Kind.Value, n.X, n.Y, Parameters: n.Parameters)).ToList(),
            Connections = []
        };
        var projectPath = Path.Combine(outputDirectory, "issue71-e2e.lfxproj");
        File.WriteAllBytes(projectPath, JsonSerializer.SerializeToUtf8Bytes(doc));
        var reloaded = Task.Run(async () =>
        {
            await using var stream = File.OpenRead(projectPath);
            return (await ProjectDocumentReader.LoadAsync(stream)).Graph;
        }).GetAwaiter().GetResult();
        if (!GraphSnapshot.ContentEquals(snapshot, reloaded))
            throw new InvalidOperationException("Reloaded .lfxproj graph differed from saved snapshot.");

        var targets = new (string FileName, string Engine, string Format, string Value)[]
        {
            ("unreal.lfxp", "Unreal", "unreal.object-path", "/Engine/BasicShapes/Cube.Cube"),
            ("unity.lfxp", "Unity", "unity.asset-guid-local-id.v1", "01234567-89ab-4def-8123-456789abcdef:10202"),
            ("godot.lfxp", "Godot", "godot.res-path.v1", "res://test_mesh.tres")
        };

        foreach (var (fileName, engine, format, value) in targets)
        {
            var project = new EngineProjectIdentity(engine, Guid.NewGuid());
            var wrongProject = new EngineProjectIdentity(engine, Guid.NewGuid());
            var binding = new OpaqueBinding(format, value);
            var map = new ResourceIdentityDocument(1, [new(resourceId, "model", "TestCube")],
                [new(project, resourceId, "model", binding)], [], [], []);
            var probe = new ResourceIdentitySnapshotProbe(new(1, project, true,
                [new(binding, ResourceResolutionStatus.Resolved, "model")], []));

            // Reject mismatched project and wrong resource kind before exporting.
            AssertReject(() => ResourceIdentityExport.CompileMappedBinary(reloaded, map, wrongProject, probe),
                "Wrong project must be rejected.");
            var wrongKindProbe = new ResourceIdentitySnapshotProbe(new(1, project, true,
                [new(binding, ResourceResolutionStatus.Resolved, "texture")], []));
            AssertReject(() => ResourceIdentityExport.CompileMappedBinary(reloaded, map, project, wrongKindProbe),
                "Type mismatch must be rejected.");

            var packageBytes = ResourceIdentityExport.CompileMappedBinary(reloaded, map, project, probe);
            File.WriteAllBytes(Path.Combine(outputDirectory, fileName), packageBytes);
        }
        Console.WriteLine("ISSUE71_FIXTURES_PASS: " + outputDirectory);
    }

    private static void AssertReject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
}
