using LibraGpu.Core;
using LiberaFx.Models.Graph;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiberaFx.Services.Compilation;

namespace LiberaFx.Services;

public sealed record GpuKernelPacket(string Id, KernelStage Stage, string Base64);
public sealed record GpuRendererBinding(string FlowId, string Renderer, string Material, double SizeScale);
public sealed record GpuProgramDelivery(
    long ProgramSequence, Guid NodeId, string CanonicalJson, GpuExportMetadata Metadata,
    string ShaderSource, IReadOnlyList<GpuKernelPacket> RegisterKernels,
    IReadOnlyDictionary<string, string> ResourceAssetPaths,
    int RendererBindingsVersion, IReadOnlyList<GpuRendererBinding> RendererBindings)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, GeneratedGpuResourceReference>? GeneratedResources { get; init; }
}
public sealed record GpuExecutableProgram(Guid NodeId, GpuProgramDelivery Unreal,
    string UnityMetadataJson, string UnityShaderSource);
public sealed record GpuFluidProgram(Guid NodeId, Fluid2DSettings Settings, GpuRendererSettings Renderer)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutomaticPlane { get; init; }
}
public sealed record GpuExecutablePayload(int Version, IReadOnlyList<GpuExecutableProgram> Programs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GpuFluidProgram>? FluidPrograms { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GpuGeneratedRenderTexture>? GeneratedDrawBindings { get; init; }
}
public sealed record GpuGeneratedRenderTexture(Guid NodeId, int Slot, string Name, int Association, int Index,
    Guid ProducerNodeId, string OutputId, bool MainTexture);
public sealed record GpuDrawTextureTarget(Guid NodeId, string NodeName, string ParameterId, string ParameterName);
public sealed record GpuContainerDiagnostic(Guid NodeId, GpuDiagnostic Diagnostic);

public static class GpuProgramDeliveryService
{
    private static GpuEditorState StateForDelivery(GraphSnapshot graph, GraphNodeSnapshot node)
    {
        var state = GpuParticleEditorService.ReadState(node);
        bool Connected(GraphSnapshot scope) => scope.Connections.Any(e => e.TargetNodeId == node.Id && e.TargetPortId == "emitterMatrix") ||
            scope.Nodes.Any(n => n.Kind != GraphNodeKind.SpawnableGroup && n.ChildGraph is { } child && Connected(child));
        if (!Connected(graph)) return state;
        if (state.Fluid2D is not null) throw new InvalidDataException("Emitter Matrix applies to particle emitter shapes. Use Draw Matrix to place a Fluid2D domain.");
        return state with { Program = state.Program with { TransformEmitterShape = true } };
    }
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
                .Select(d => new GpuContainerDiagnostic(node.Id, d))).Concat(ValidateGeneratedResources(graph))
            .Concat(GeneratedDrawBindings(graph, out _)).ToArray();

    public static IReadOnlyList<GpuGeneratedRenderTexture> GeneratedDrawBindings(GraphSnapshot graph)
    {
        var errors = GeneratedDrawBindings(graph, out var bindings);
        if (errors.Count != 0) throw new GpuValidationException(errors.Select(error => error.Diagnostic));
        return bindings;
    }

    private static IReadOnlyList<GpuContainerDiagnostic> GeneratedDrawBindings(GraphSnapshot graph, out IReadOnlyList<GpuGeneratedRenderTexture> bindings)
    {
        var errors = new List<GpuContainerDiagnostic>();
        var result = new List<GpuGeneratedRenderTexture>();
        var nodes = ScopeNodes(graph).ToDictionary(node => node.Id);
        var slots = new HashSet<(Guid, string)>();
        foreach (var producer in Containers(graph))
        {
            var state = GpuParticleEditorService.ReadState(producer);
            if (state.Fluid2D is null) continue;
            foreach (var consumer in state.GeneratedConsumers ?? [])
            {
                var definition = nodes.TryGetValue(consumer.TargetNodeId, out var target)
                    ? GraphNodeDefinition.All.FirstOrDefault(item => item.Kind == target.Kind) : null;
                var parameter = definition is null ? null : RenderNodeSchema.Read(definition, target!.Parameters)
                    .FirstOrDefault(item => item.Id == consumer.ParameterId && item.Kind == "texture");
                var main = consumer.ParameterId == "assetPath" && definition?.TypeName is "SpriteDraw" or "FlexiblePlaneDraw";
                if (definition is null || (!main && parameter is null) || (main && consumer.OutputId != "ColorOutput") || !slots.Add((consumer.TargetNodeId, consumer.ParameterId)) ||
                    consumer.OutputId is not ("DyeOutput" or "ColorOutput" or "FieldOutput"))
                {
                    errors.Add(new(producer.Id, new("GeneratedDrawBinding", "Generated texture target must be a unique supported draw/texture slot in the same scope.")));
                    continue;
                }
                result.Add(new(target!.Id, parameter?.Slot ?? 0, parameter?.Name ?? "", parameter?.Association ?? 2,
                    parameter?.Index ?? -1, producer.Id, consumer.OutputId, main));
            }
        }
        if (result.Count > 64)
            errors.Add(new(Guid.Empty, new("GeneratedDrawBindingLimit", "A scope supports at most 64 generated draw texture bindings.")));
        bindings = result.ToArray();
        return errors;
    }

    private static IEnumerable<GraphNodeSnapshot> ScopeNodes(GraphSnapshot graph)
    {
        foreach (var node in graph.Nodes)
        {
            yield return node;
            if (node.Kind != GraphNodeKind.SpawnableGroup && !GpuParticleEditorService.IsGpu(node.Kind) &&
                !CpuParticleSchema.HasChild(node.Kind) && node.ChildGraph is { } child)
                foreach (var nested in ScopeNodes(child)) yield return nested;
        }
    }

    public static IReadOnlyList<GpuDrawTextureTarget> DrawTextureTargets(GraphSnapshot graph)
    {
        var targets = new List<GpuDrawTextureTarget>();
        foreach (var node in ScopeNodes(graph))
        {
            var definition = GraphNodeDefinition.All.FirstOrDefault(item => item.Kind == node.Kind);
            if (definition is null || !RenderProgramBindings.IsRenderNode(node.Kind)) continue;
            if (definition.TypeName is "SpriteDraw" or "FlexiblePlaneDraw")
                targets.Add(new(node.Id, node.Name, "assetPath", ""));
            foreach (var parameter in RenderNodeSchema.Read(definition, node.Parameters).Where(parameter => parameter.Kind == "texture"))
                targets.Add(new(node.Id, node.Name, parameter.Id, parameter.Name));
        }
        return targets;
    }

    private static IReadOnlyList<GpuContainerDiagnostic> ValidateGeneratedResources(GraphSnapshot graph)
    {
        var nodes = Containers(graph).ToArray();
        var states = nodes.ToDictionary(node => node.Id, GpuParticleEditorService.ReadState);
        var errors = new List<GpuContainerDiagnostic>();
        foreach (var (id, state) in states.Where(pair => pair.Value.Fluid2D is null))
        {
        foreach (var resource in state.Program.Resources.Where(resource => resource.FlowMap?.Coordinates == FlowMapCoordinates.GeneratedPlane))
            if (state.GeneratedResources?.ContainsKey(resource.Id) != true)
                errors.Add(new(id, new("GeneratedResourceCoordinates", "Generated-plane sampling requires an explicit generated resource producer.")));
        foreach (var (resourceId, reference) in state.GeneratedResources ?? [])
        {
            var resource = state.Program.Resources.SingleOrDefault(item => item.Id == resourceId);
            if (resource is null || resource.Kind is not (ResourceKind.Texture2D or ResourceKind.FlowMap2D) || state.ResourceAssetPaths.ContainsKey(resourceId))
                errors.Add(new(id, new("GeneratedResourceBinding", $"'{resourceId}' requires one generated Texture2D binding, separate from engine assets.")));
            else if (!states.TryGetValue(reference.ProducerNodeId, out var producer) || producer.Fluid2D is not { } settings)
                errors.Add(new(id, new("GeneratedResourceProducer", $"Producer '{reference.ProducerNodeId}' is not an active fluid in the same scope.")));
            else if (!((reference.OutputId is "DyeOutput" or "FieldOutput" or "ColorOutput") && resource.Kind == ResourceKind.Texture2D ||
                reference.OutputId == "VelocityOutput" && resource.Kind == ResourceKind.FlowMap2D && resource.FlowMap?.Encoding == FlowMapEncoding.FloatVelocity ||
                reference.OutputId == "DensityOutput" && resource.Kind == ResourceKind.FlowMap2D && resource.FlowMap?.Encoding == FlowMapEncoding.FloatDensity))
                errors.Add(new(id, new("GeneratedResourceOutput", $"Unsupported generated output '{reference.OutputId}'.")));
            else if (resource.Width != 0 && (resource.Width != settings.Width || resource.Height != settings.Height))
                errors.Add(new(id, new("GeneratedResourceDimensions", $"'{resourceId}' dimensions do not match the generated output.")));
        }
        }
        return errors;
    }
    public static IReadOnlyList<GpuProgramDelivery> Create(GraphSnapshot graph, long programSequence,
        ShaderFormat format = ShaderFormat.Usf)
    {
        return Containers(graph).Select(node =>
        {
            var state = StateForDelivery(graph, node);
            if (state.Fluid2D is not null)
            {
                throw new InvalidDataException("2D fluid requires the executable effect-package transport, not particle source delivery.");
            }
            if (state.GeneratedResources is { Count: > 0 })
                throw new InvalidDataException("Generated resources require whole-effect package delivery.");
            var compiled = GpuParticleEditorService.Compile(state);
            if (format == ShaderFormat.Usf)
            {
                RequireBackendSupport(node.Id, compiled, GpuBackendCapabilities.Unreal);
            }
            else if (format == ShaderFormat.UnityCompute)
            {
                RequireBackendSupport(node.Id, compiled, GpuBackendCapabilities.Unity);
            }
            return CreateDelivery(node.Id, state, compiled, programSequence, format);
        }).ToArray();
    }

    public static byte[] CreateExecutablePayload(GraphSnapshot graph, long programSequence = 0)
    {
        var containers = Containers(graph).ToArray();
        if (containers.Length > MaximumProgramsPerScope)
            throw new InvalidDataException($"A GPU program scope supports at most {MaximumProgramsPerScope} programs.");

        var states = containers.Select(node => (Node: node, State: StateForDelivery(graph, node))).ToArray();
        ulong ownedBytes = 0;
        foreach (var item in states)
        {
            if (item.State.Fluid2D is { } fluid)
                ownedBytes += (ulong)fluid.Width * fluid.Height * 144;
            else
            {
                var compiled = GpuParticleEditorService.Compile(item.State);
                var registers = compiled.RegisterKernels.Select(kernel => kernel.RegisterCount).DefaultIfEmpty().Max();
                ownedBytes += compiled.ParticleBufferBytes + (ulong)compiled.Capacity * registers * 16 +
                    (ulong)compiled.RegisterKernels.Sum(kernel => 64L * (1 + kernel.Instructions.Count));
            }
        }
        if (ownedBytes > 256UL * 1024 * 1024)
            throw new InvalidDataException("An execution scope exceeds the 256 MiB owned GPU resource budget.");
        var generatedErrors = ValidateGeneratedResources(graph);
        if (generatedErrors.Count != 0)
            throw new GpuValidationException(generatedErrors.Select(error => error.Diagnostic));
        var drawBindings = GeneratedDrawBindings(graph);
        var generatedBindings = states.Any(item => item.State.Fluid2D is null && item.State.GeneratedResources is { Count: > 0 });
        var consumers = states.Where(item => item.State.Fluid2D is null)
            .SelectMany(item => (item.State.GeneratedResources ?? []).Where(pair => (pair.Value.OutputId is "DyeOutput" or "ColorOutput") &&
                item.State.Program.Flow.Any(flow => flow.Kind == FlowKind.RenderBillboard && flow.Resource == pair.Key)).Select(pair => pair.Value))
            .Select(reference => reference.ProducerNodeId).ToHashSet();
        foreach (var binding in drawBindings)
            consumers.Add(binding.ProducerNodeId);
        var fluids = states.Where(item => item.State.Fluid2D is not null).Select(item =>
        {
            if (GpuParticleEditorService.Diagnostics(item.State) is { Count: > 0 } errors)
            {
                throw new GpuValidationException(errors);
            }
            return new GpuFluidProgram(item.Node.Id, item.State.Fluid2D!,
                item.State.FluidRenderer ?? new(Fluid2DPresets.RendererMaterial(item.State.Fluid2D!.Model), 1))
            {
                AutomaticPlane = item.State.FluidAutomaticPlane ?? (consumers.Contains(item.Node.Id) ? false : null)
            };
        }).ToArray();
        var programs = states.Where(item => item.State.Fluid2D is null).Select(item =>
        {
            var node = item.Node;
            var state = item.State;
            var compiled = GpuParticleEditorService.Compile(state);
            RequireBackendSupport(node.Id, compiled, GpuBackendCapabilities.Unreal, GpuBackendCapabilities.Unity);
            foreach (var resource in compiled.Resources)
                if (state.GeneratedResources?.ContainsKey(resource.Id) != true &&
                    (!state.ResourceAssetPaths.TryGetValue(resource.Id, out var assetPath) || string.IsNullOrWhiteSpace(assetPath)))
                    throw new InvalidDataException(
                        $"GPU resource '{resource.Id}' on node '{node.Id:D}' has no engine asset reference.");

            var unreal = CreateDelivery(node.Id, state, compiled, programSequence, ShaderFormat.Usf);
            var unityShader = compiled.EmitUnityCompute();
            return new GpuExecutableProgram(node.Id, unreal,
                compiled.ExportMetadataJson(ShaderFormat.UnityCompute), unityShader.Text);
        }).ToArray();
        var version = generatedBindings || drawBindings.Count != 0 || fluids.Any(fluid => fluid.AutomaticPlane is not null) ? 3 : fluids.Length == 0 ? 1 : 2;
        return JsonSerializer.SerializeToUtf8Bytes(new GpuExecutablePayload(version, programs)
        {
            FluidPrograms = fluids.Length == 0 ? null : fluids,
            GeneratedDrawBindings = drawBindings.Count == 0 ? null : drawBindings
        }, PayloadJson);
    }

    private static void RequireBackendSupport(Guid nodeId, CompiledGpuProgram program,
        params GpuBackendCapabilities[] targets)
    {
        var diagnostics = targets.SelectMany(target => GpuBackendRegistry.BuiltIn.Validate(target.Id, program)).ToArray();
        if (diagnostics.Length != 0)
        {
            throw new InvalidDataException($"GPU node '{nodeId:D}': " +
                string.Join(" ", diagnostics.Select(diagnostic => diagnostic.Message)));
        }
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
            new Dictionary<string, string>(state.ResourceAssetPaths), 1, rendererBindings)
        {
            GeneratedResources = state.GeneratedResources is { Count: > 0 }
                ? new Dictionary<string, GeneratedGpuResourceReference>(state.GeneratedResources) : null
        };
    }
}
