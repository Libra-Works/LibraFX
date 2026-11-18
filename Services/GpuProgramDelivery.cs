using LibraGpu.Core;
using LiberaFx.Models.Graph;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiberaFx.Services;

public sealed record GpuKernelPacket(string Id, KernelStage Stage, string Base64);
public sealed record GpuRendererBinding(string FlowId, string Renderer, string Material, double SizeScale);
public sealed record GpuProgramDelivery(
    long ProgramSequence, Guid NodeId, string CanonicalJson, GpuExportMetadata Metadata,
    string ShaderSource, IReadOnlyList<GpuKernelPacket> RegisterKernels,
    IReadOnlyDictionary<string, string> ResourceAssetPaths,
    int RendererBindingsVersion, IReadOnlyList<GpuRendererBinding> RendererBindings);
public sealed record GpuExecutableProgram(Guid NodeId, GpuProgramDelivery Unreal,
    string UnityMetadataJson, string UnityShaderSource);
public sealed record GpuExecutablePayload(int Version, IReadOnlyList<GpuExecutableProgram> Programs);
public sealed record GpuContainerDiagnostic(Guid NodeId, GpuDiagnostic Diagnostic);

public static class GpuProgramDeliveryService
{
    private const int MaximumProgramsPerScope = 16;
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IEnumerable<GraphNodeSnapshot> Containers(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            if (node.KindDomain == "GpuParticle") yield return node;
            else if (node.Kind != GraphNodeKind.SpawnableGroup && node.ChildGraph is { } child)
                foreach (var nested in Containers(child)) yield return nested;
        }
    }
    public static IReadOnlyList<GpuContainerDiagnostic> Validate(GraphSnapshot graph) =>
        Containers(graph).SelectMany(node =>
            GpuParticleEditorService.Diagnostics(GpuParticleEditorService.ReadState(node))
                .Select(d => new GpuContainerDiagnostic(node.Id, d))).ToArray();
    public static IReadOnlyList<GpuProgramDelivery> Create(GraphSnapshot graph, long programSequence,
        ShaderFormat format = ShaderFormat.Usf)
    {
        return Containers(graph).Select(node =>
        {
            var state = GpuParticleEditorService.ReadState(node);
            var compiled = GpuParticleEditorService.Compile(state);
            return CreateDelivery(node.Id, state, compiled, programSequence, format);
        }).ToArray();
    }

    public static byte[] CreateExecutablePayload(GraphSnapshot graph, long programSequence = 0)
    {
        var containers = Containers(graph).ToArray();
        if (containers.Length > MaximumProgramsPerScope)
            throw new InvalidDataException($"A GPU program scope supports at most {MaximumProgramsPerScope} programs.");

        var programs = containers.Select(node =>
        {
            var state = GpuParticleEditorService.ReadState(node);
            var compiled = GpuParticleEditorService.Compile(state);
            var diagnostics = GpuBackendCapabilities.Unreal.Validate(compiled)
                .Concat(GpuBackendCapabilities.Unity.Validate(compiled)).ToArray();
            if (diagnostics.Length != 0)
            {
                throw new InvalidDataException($"GPU node '{node.Id:D}': " +
                    string.Join(" ", diagnostics.Select(diagnostic => diagnostic.Message)));
            }
            foreach (var resource in compiled.Resources)
                if (!state.ResourceAssetPaths.TryGetValue(resource.Id, out var assetPath) ||
                    string.IsNullOrWhiteSpace(assetPath))
                    throw new InvalidDataException(
                        $"GPU resource '{resource.Id}' on node '{node.Id:D}' has no engine asset reference.");

            var unreal = CreateDelivery(node.Id, state, compiled, programSequence, ShaderFormat.Usf);
            var unityShader = compiled.EmitUnityCompute();
            return new GpuExecutableProgram(node.Id, unreal,
                compiled.ExportMetadataJson(ShaderFormat.UnityCompute), unityShader.Text);
        }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new GpuExecutablePayload(1, programs), PayloadJson);
    }

    private static GpuProgramDelivery CreateDelivery(Guid nodeId, GpuEditorState state,
        CompiledGpuProgram compiled, long programSequence, ShaderFormat format)
    {
        var shader = format switch
        {
            ShaderFormat.Usf => compiled.EmitUsf(),
            ShaderFormat.HlslInclude => compiled.EmitHlslInclude(),
            ShaderFormat.UnityCompute => compiled.EmitUnityCompute(),
            _ => throw new ArgumentException("Unsupported shader format.")
        };
        var rendererBindings = state.Program.Flow.Where(f => f.Kind == FlowKind.RenderBillboard).Select(f =>
        {
            var settings = state.RendererSettings.GetValueOrDefault(f.Id) ?? new GpuRendererSettings();
            return new GpuRendererBinding(f.Id, "builtin:billboard", settings.Material, settings.SizeScale);
        }).ToArray();
        if (rendererBindings.Length != state.Program.Flow.Count(f => f.Kind == FlowKind.RenderBillboard) ||
            rendererBindings.Any(binding => !double.IsFinite(binding.SizeScale) || binding.SizeScale <= 0 ||
                binding.SizeScale > 10000 || binding.Material is not ("builtin:unlit-alpha" or "builtin:unlit-additive")))
            throw new InvalidDataException($"GPU renderer bindings for node '{nodeId:D}' are invalid.");
        return new GpuProgramDelivery(programSequence, nodeId, compiled.CanonicalJson,
            compiled.GetExportMetadata(format), shader.Text,
            compiled.RegisterKernels.Select(k => new GpuKernelPacket(k.KernelId, k.Stage,
                Convert.ToBase64String(k.SerializeLittleEndian()))).ToArray(),
            new Dictionary<string, string>(state.ResourceAssetPaths), 1, rendererBindings);
    }
}
