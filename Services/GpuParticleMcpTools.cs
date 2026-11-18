using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using LibraGpu.Core;
using LiberaFx.ViewModels;
using ModelContextProtocol.Server;
using ModelContextProtocol;

namespace LiberaFx.Services;

[McpServerToolType]
public sealed class GpuParticleMcpTools
{
    private static string Json(object value) => JsonSerializer.Serialize(value, GpuParticleEditorService.Json);
    private static async Task<string> OnUi(Func<string> action) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try { return action(); }
            catch (GpuValidationException e) { throw new McpException(Json(new { diagnostics = e.Diagnostics })); }
            catch (Exception e) when (e is ArgumentException or FormatException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { throw new McpException(e.Message); }
        });
    [McpServerTool(Name = "gpu_describe"), Description("Discover GPU-only operation arities/type policies, fixed attributes/uniforms, limits and source-export capability. No CPU Signal/host nodes.")]
    public static string Describe() => Json(new
    {
        domain = "GpuParticle", purpose = "SourceExport", runtimeExecuted = false,
        operations = GpuOperationRegistry.Operations.Values,
        attributes = ParticleAbi.Attributes, uniforms = ParticleAbi.Uniforms, limits = new GpuLimits(),
        flowKinds = new[] { "Spawn", "Update", "RenderBillboard" },
        futureUnsupported = new[] { "RenderStartTexture", "ComputePass", "Quaternion" },
        resourceKinds = Enum.GetNames<ResourceKind>(),
        backendCapabilities = new[] { GpuBackendCapabilities.Unreal, GpuBackendCapabilities.Unity },
        textGrammar = "Restricted original typed DSL: let, @Attribute assignment, param(\"deltaTime\"), float2/3/4 constructors, registered math. Not VEX/HLSL.",
        editActions = new[] { "add", "remove", "update", "move", "connect", "disconnect", "write", "write-remove",
            "text", "draft", "discard-draft", "capacity", "flow-add", "flow-remove", "flow-move", "flow-update", "resource", "renderer" },
        billboardMaterials = new[] { "builtin:unlit-alpha", "builtin:unlit-additive" },
        builtinTextures = new[] { "builtin:white" },
        editSchema = new
        {
            action = "required string", kernelId = "kernel ID for node/text/write actions",
            node = "{id,op,type,inputs:string[],value:number[],attribute?,uniform?,componentIndex?}",
            nodeId = "node / write / flow ID", sourceId = "connection source node ID", input = "zero-based input / flow index",
            position = "{x,y}", write = "{id,attribute,value}", text = "DSL",
            flow = "{id,kind,kernel?,resource?}", capacity = "uint",
            resource = "{id,kind,width,height}", assetPath = "Engine asset object path or explicit builtin:white",
            material = "Billboard material preset for renderer action", sizeScale = "Finite draw-size multiplier in (0,10000]"
        }
    });

    [McpServerTool(Name = "gpu_create_particle"), Description("Create a persistent GPU Particle CPU container with valid Spawn/Update/Billboard starter IR. Returns stable node ID; no GPU execution.")]
    public static async Task<string> Create(ActiveGraphProvider activeGraph,
        [Description("Horizontal graph coordinate.")] double x = 80,
        [Description("Vertical graph coordinate.")] double y = 80) =>
        await OnUi(() =>
        {
            var node = GpuParticleEditorService.Create(activeGraph.Graph, x, y);
            return Json(new { nodeId = node.Id, state = GpuParticleEditorService.ReadState(node), runtimeExecuted = false });
        });

    [McpServerTool(Name = "gpu_read_program"), Description("Read one GPU Particle's portable IR, authoring positions/text drafts and node/kernel/span diagnostics in the active graph.")]
    public static async Task<string> Read(ActiveGraphProvider activeGraph,
        [Description("GUID of a GPU container node in the active graph.")] string nodeId) =>
        await OnUi(() =>
        {
            var state = GpuParticleEditorService.ReadState(activeGraph.Graph.GetNode(Guid.Parse(nodeId)));
            return Json(new { nodeId, state, diagnostics = GpuParticleEditorService.Diagnostics(state) });
        });

    [McpServerTool(Name = "gpu_apply_plan"), Description("Apply a bounded JSON array of GPU edits as one undo operation. Read editSchema from gpu_describe before constructing the plan. Invalid typed visual graphs may be retained; requireValid rejects the entire plan. Failed text replacement preserves original IR plus draft diagnostics.")]
    public static async Task<string> ApplyPlan(ActiveGraphProvider activeGraph,
        [Description("GUID of a GPU container node in the active graph.")] string nodeId,
        [Description("JSON string containing an array of edit objects matching gpu_describe's editSchema.")] string editsJson,
        [Description("Reject the whole plan if its typed visual graph cannot compile.")] bool requireValid = false) =>
        await OnUi(() =>
        {
            if (System.Text.Encoding.UTF8.GetByteCount(editsJson) > GpuParticleEditorService.MaxStateBytes)
                throw new ArgumentException("Plan exceeds byte limit.");
            var edits = JsonSerializer.Deserialize<List<GpuEdit>>(editsJson, GpuParticleEditorService.Json)
                ?? throw new ArgumentException("An edit array is required.");
            var state = GpuParticleEditorService.Apply(activeGraph.Graph, Guid.Parse(nodeId), edits, requireValid);
            return Json(new { nodeId, diagnostics = GpuParticleEditorService.Diagnostics(state), runtimeExecuted = false });
        });

    [McpServerTool(Name = "gpu_compile"), Description("Validate/compile one GPU Particle for SourceExport only. Returns node-specific errors, hashes and export metadata; source included only if requested. Does not execute GPU or send unknown engine commands.")]
    public static async Task<string> Compile(ActiveGraphProvider activeGraph,
        [Description("GUID of a GPU container node in the active graph.")] string nodeId,
        [Description("Shader format: usf, hlsl, or compute.")] string format = "usf",
        [Description("Include generated shader text in addition to metadata.")] bool includeSource = false) =>
        await OnUi(() =>
        {
            var state = GpuParticleEditorService.ReadState(activeGraph.Graph.GetNode(Guid.Parse(nodeId)));
            var errors = GpuParticleEditorService.Diagnostics(state);
            if (errors.Count != 0) return Json(new { nodeId, success = false, diagnostics = errors, runtimeExecuted = false });
            var compiled = GpuParticleEditorService.Compile(state);
            var shaderFormat = format switch
            {
                "usf" => ShaderFormat.Usf, "hlsl" => ShaderFormat.HlslInclude, "compute" => ShaderFormat.UnityCompute,
                _ => throw new ArgumentException("Shader format must be usf, hlsl or compute.")
            };
            return Json(new
            {
                nodeId, success = true, purpose = "SourceExport", runtimeExecuted = false,
                compiled.ContentHash, compiled.Capacity, metadata = compiled.GetExportMetadata(shaderFormat),
                source = includeSource ? GpuParticleEditorService.Export(state, format) : null
            });
        });

    [McpServerTool(Name = "gpu_export_source"), Description("Return explicitly requested IR/shader source and matching metadata for caller-chosen export. Does not write unsolicited files, execute GPU, or publish anything.")]
    public static async Task<string> Export(ActiveGraphProvider activeGraph,
        [Description("GUID of a GPU container node in the active graph.")] string nodeId,
        [Description("Output format: json for IR, or usf, hlsl, or compute for shader source.")] string format = "json") =>
        await OnUi(() =>
        {
            var state = GpuParticleEditorService.ReadState(activeGraph.Graph.GetNode(Guid.Parse(nodeId)));
            var source = GpuParticleEditorService.Export(state, format);
            var shaderFormat = format switch { "usf" => ShaderFormat.Usf, "hlsl" => ShaderFormat.HlslInclude, _ => ShaderFormat.UnityCompute };
            return Json(new { nodeId, format, source,
                suggestedFileName = GpuParticleEditorService.SafeFileName(state.Program.Id) + "." + format,
                metadata = format == "json" ? null : GpuParticleEditorService.Compile(state).GetExportMetadata(shaderFormat),
                purpose = "SourceExport", runtimeExecuted = false });
        });

    [McpServerTool(Name = "gpu_export_bundle"), Description("Explicitly request one GPU Particle source delivery bundle: canonical IR, matching USF/metadata and Core LGPK packets, UI texture path metadata and caller programSequence. SourceExport only, not runtime execution.")]
    public static async Task<string> ExportBundle(ActiveGraphProvider activeGraph,
        [Description("GUID of a GPU container node in the active graph.")] string nodeId,
        [Description("Caller-provided sequence number for delivery metadata.")] long programSequence = 0) =>
        await OnUi(() =>
        {
            var node = activeGraph.Graph.CreateCurrentSnapshot().Nodes.Single(n => n.Id == Guid.Parse(nodeId));
            if (node.KindDomain != "GpuParticle") throw new ArgumentException("Node is not a GPU Particle.");
            return Json(GpuProgramDeliveryService.Create(new Models.Graph.GraphSnapshot([node], []), programSequence).Single());
        });

    [McpServerTool(Name = "gpu_open_workspace"), Description("Open the dedicated GPU workspace overlay for one active-graph GPU Particle, preserving CPU document and dock layout. This authoring mode does not execute GPU.")]
    public static async Task<string> Open(ActiveGraphProvider activeGraph,
        [Description("GUID of a GPU container node in the active graph.")] string nodeId) =>
        await OnUi(() =>
        {
            var node = activeGraph.Graph.GetNode(Guid.Parse(nodeId));
            if (!node.IsGpuParticle) throw new ArgumentException("Node is not a GPU Particle.");
            activeGraph.Graph.OpenGpuEditor(node);
            return Json(new { nodeId, requestedMode = "GpuParticleFlow" });
        });
}
