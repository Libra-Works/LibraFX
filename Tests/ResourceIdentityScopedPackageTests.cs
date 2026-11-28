using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Assets;
using LiberaFx.Services.Compilation;

// Parent test runner should call Run(); uses only an explicit in-memory binding snapshot.
internal static class ResourceIdentityScopedPackageTests
{
    public static void Run()
    {
        var project = new EngineProjectIdentity("Unreal", Guid.NewGuid());
        var resourceId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var modelDefinition = GraphNodeDefinition.All.Single(item => item.TypeName == "ModelDraw");
        var groupDefinition = GraphNodeDefinition.All.Single(item => item.Kind == GraphNodeKind.SpawnableGroup);
        const string legacyPath = "/Game/Legacy/Model.Model";
        const string movedPath = "/Game/Moved/Model.Model";
        var resourceBinding = new OpaqueBinding("unreal.object-path", movedPath);
        var child = new GraphSnapshot(
        [
            new GraphNodeSnapshot(modelId, modelDefinition.Kind, "Model", 0, 0,
                modelDefinition.PortType, modelDefinition.PortCategory, Parameters:
                [
                    new("assetPath", "string", JsonSerializer.SerializeToElement(legacyPath))
                ])
        ], []);
        var graph = new GraphSnapshot(
        [
            new GraphNodeSnapshot(groupId, groupDefinition.Kind, "Effect Model", 0, 0,
                groupDefinition.PortType, groupDefinition.PortCategory, ChildGraph: child)
        ], []);
        var document = new ResourceIdentityDocument(1,
            [new ResourceDefinition(resourceId, "model", "Explicit model")],
            [new ProjectResourceBinding(project, resourceId, "model", resourceBinding)],
            [], [new ResourceUse(modelId, "assetPath", resourceId, [groupId])], []);
        var snapshot = new ResourceBindingSnapshot(1, project, true,
            [new ResourceSnapshotEntry(resourceBinding, ResourceResolutionStatus.Resolved, "model")], []);
        var context = new ResourceIdentityRenderContext(new ResourceIdentityResolver(
            document, project, new ResourceIdentitySnapshotProbe(snapshot)), requireMappings: true, graph: graph);

        var package = SpawnableGroupPackage.Compile(graph, resourceContext: context);
        using var rendered = JsonDocument.Parse(package.Templates.Single().RenderConfiguration);
        Require(rendered.RootElement.GetProperty("nodes")[0].GetProperty("assetPath").GetString() == movedPath,
            "Spawnable child render did not resolve its explicitly scoped binding.");
        Require(package.RootRenderConfiguration.Length > 0,
            "Spawnable package root render configuration was not produced.");

        var manifestBytes = ResourceIdentityExport.CreateManifest(graph, document, project,
            new ResourceIdentitySnapshotProbe(snapshot));
        using var manifest = JsonDocument.Parse(manifestBytes);
        Require(manifest.RootElement.GetProperty("uses")[0].GetProperty("instancePath")[0].GetGuid() == groupId,
            "Manifest omitted the containing Group instance path.");
        Require(manifest.RootElement.GetProperty("spawnablePackage").GetBytesFromBase64().Length > 0,
            "Manifest omitted the executable Spawnable package.");
        Require(manifest.RootElement.GetProperty("parameterUses").GetArrayLength() == 0,
            "An absent material parameter mapping was invented.");
        Require(legacyPath != movedPath, "Fixture must distinguish old and explicitly registered asset paths.");

        var unityProject = new EngineProjectIdentity("Unity", Guid.NewGuid());
        var unityResourceId = Guid.NewGuid();
        var unityNodeId = Guid.NewGuid();
        var unityAssetGuid = Guid.NewGuid();
        var unityBinding = new OpaqueBinding("unity.asset-guid-local-id.v1",
            $"{unityAssetGuid:D}".ToUpperInvariant() + ":0004300000");
        var unityCanonicalBinding = $"{unityAssetGuid:D}:4300000";
        var unityGraph = new GraphSnapshot(
        [
            new GraphNodeSnapshot(unityNodeId, modelDefinition.Kind, "Unity Model", 0, 0,
                modelDefinition.PortType, modelDefinition.PortCategory, Parameters:
                [new("assetPath", "string", JsonSerializer.SerializeToElement(legacyPath))])
        ], []);
        var unityDocument = new ResourceIdentityDocument(1,
            [new ResourceDefinition(unityResourceId, "model", "Unity Model")],
            [new ProjectResourceBinding(unityProject, unityResourceId, "model", unityBinding)],
            [], [new ResourceUse(unityNodeId, "assetPath", unityResourceId)], []);
        var unityProbe = new ResourceIdentitySnapshotProbe(new ResourceBindingSnapshot(
            1, unityProject, true,
            [new ResourceSnapshotEntry(unityBinding, ResourceResolutionStatus.Resolved, "model")], []));
        var unityContext = new ResourceIdentityRenderContext(
            new ResourceIdentityResolver(unityDocument, unityProject, unityProbe),
            requireMappings: true, graph: unityGraph);
        using var unityScene = JsonDocument.Parse(RenderProgramBindings.CreateScene(unityGraph, 1, unityContext));
        Require(unityScene.RootElement.GetProperty("nodes")[0].GetProperty("assetPath").GetString() == unityCanonicalBinding,
            "Unity render_config identity must use the same canonical GUID/local-file-ID key as binding delivery.");

        var unityMaterialId = Guid.NewGuid();
        var unityParameterId = Guid.NewGuid();
        var unityMaterialAssetGuid = Guid.NewGuid();
        var unityMaterialBinding = new OpaqueBinding("unity.asset-guid-local-id.v1",
            $"{unityMaterialAssetGuid:D}".ToUpperInvariant() + ":0005300000");
        var unityMaterialCanonicalBinding = $"{unityMaterialAssetGuid:D}:5300000";
        var unityParameterBinding = new OpaqueBinding("unity.shader-property.v1",
            """{"name":"_Glossiness","association":2,"index":-1}""");
        // Preserve legacy authoring references; only explicit bindings supply Unity runtime identities.
        const string legacyMaterialPath = "/Game/Legacy/Material.Material";
        var unitySchema = RenderNodeSchema.Encode(new EngineAsset(legacyPath, "Legacy Model",
            "model", "Unreal", Materials:
            [
                new EngineMaterial(0, legacyMaterialPath, "Legacy Material",
                    [new EngineMaterialParameter("Glossiness", "scalar",
                        JsonSerializer.SerializeToElement(0.35))])
            ]));
        var unityParameterPin = RenderNodeSchema.Describe(unitySchema).Single().Id;
        var unityMaterialGraph = new GraphSnapshot(
        [
            new GraphNodeSnapshot(unityNodeId, modelDefinition.Kind, "Unity Model", 0, 0,
                modelDefinition.PortType, modelDefinition.PortCategory, Parameters:
                [
                    new("assetPath", "string", JsonSerializer.SerializeToElement(legacyPath)),
                    new("materialSchema", "string", JsonSerializer.SerializeToElement(unitySchema)),
                    new(unityParameterPin, "float", JsonSerializer.SerializeToElement(0.35))
                ])
        ], []);
        var unityParameterDocument = new ResourceIdentityDocument(1,
            [
                new ResourceDefinition(unityResourceId, "model", "Unity Model"),
                new ResourceDefinition(unityMaterialId, "material", "Unity material",
                    [new MaterialParameterDefinition(unityParameterId, "scalar", "Author Roughness",
                        JsonSerializer.SerializeToElement(0.35))])
            ],
            [
                new ProjectResourceBinding(unityProject, unityResourceId, "model", unityBinding),
                new ProjectResourceBinding(unityProject, unityMaterialId, "material", unityMaterialBinding)
            ],
            [new ProjectParameterBinding(unityProject, unityMaterialId, unityParameterId, "scalar",
                unityParameterBinding)],
            [
                new ResourceUse(unityNodeId, "assetPath", unityResourceId),
                new ResourceUse(unityNodeId, "material:0", unityMaterialId)
            ],
            [new ParameterUse(unityNodeId, unityParameterPin, unityMaterialId, unityParameterId)]);
        var unityParameterProbe = new ResourceIdentitySnapshotProbe(new ResourceBindingSnapshot(
            1, unityProject, true,
            [
                new ResourceSnapshotEntry(unityBinding, ResourceResolutionStatus.Resolved, "model"),
                new ResourceSnapshotEntry(unityMaterialBinding, ResourceResolutionStatus.Resolved, "material")
            ],
            [new ParameterSnapshotEntry(unityMaterialBinding, unityParameterBinding,
                ResourceResolutionStatus.Resolved, "scalar")]));
        var unityParameterContext = new ResourceIdentityRenderContext(
            new ResourceIdentityResolver(unityParameterDocument, unityProject, unityParameterProbe),
            requireMappings: true, graph: unityMaterialGraph);
        using var unityMaterialScene = JsonDocument.Parse(RenderProgramBindings.CreateScene(
            unityMaterialGraph, 2, unityParameterContext));
        var compiledUnityNode = unityMaterialScene.RootElement.GetProperty("nodes")[0];
        var compiledUnityInput = compiledUnityNode.GetProperty("inputs").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == unityParameterPin);
        using var legacyMaterialScene = JsonDocument.Parse(RenderProgramBindings.CreateScene(unityMaterialGraph, 2));
        var legacyMaterialNode = legacyMaterialScene.RootElement.GetProperty("nodes")[0];
        Require(legacyMaterialNode.GetProperty("assetPath").GetString() == legacyPath &&
            legacyMaterialNode.GetProperty("materials")[0].GetProperty("path").GetString() == legacyMaterialPath,
            "Unity delivery must not replace the authored graph's legacy paths.");
        Require(compiledUnityNode.GetProperty("assetPath").GetString() == unityCanonicalBinding &&
            compiledUnityNode.GetProperty("materials")[0].GetProperty("path").GetString() ==
                unityMaterialCanonicalBinding &&
            compiledUnityInput.GetProperty("materialParameter").GetProperty("name").GetString() ==
                "Author Roughness" &&
            compiledUnityInput.GetProperty("materialParameter").GetProperty("association").GetInt32() == 2 &&
            compiledUnityInput.GetProperty("materialParameter").GetProperty("index").GetInt32() == -1,
            "Compiled Unity resource/parameter identities do not match canonical binding delivery and authored-name runtime lookup.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
