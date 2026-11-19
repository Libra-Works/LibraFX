using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibraGpu.Core;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;

namespace LiberaFx.Services;

public sealed record GpuPosition(double X, double Y);
public sealed record GpuTextDraft(string Text, List<GpuDiagnostic> Diagnostics);
public sealed record GpuRendererSettings(string Material = "builtin:unlit-alpha", double SizeScale = 1);
public sealed record GpuEditorState
{
    public GpuProgram Program { get; init; } = GpuSamples.CreateEmitter();
    public Dictionary<string, GpuPosition> Positions { get; init; } = [];
    public Dictionary<string, GpuTextDraft> Drafts { get; init; } = [];
    public Dictionary<string, string> ResourceAssetPaths { get; init; } = [];
    public Dictionary<string, GpuRendererSettings> RendererSettings { get; init; } = [];
}

public sealed record GpuEdit
{
    public string Action { get; init; } = "";
    public string? KernelId { get; init; }
    public GpuNode? Node { get; init; }
    public string? NodeId { get; init; }
    public string? SourceId { get; init; }
    public int Input { get; init; }
    public GpuPosition? Position { get; init; }
    public AttributeWrite? Write { get; init; }
    public FlowStep? Flow { get; init; }
    public KernelGraph? Kernel { get; init; }
    public string? Text { get; init; }
    public uint? Capacity { get; init; }
    public ResourceDeclaration? Resource { get; init; }
    public string? AssetPath { get; init; }
    public string? Material { get; init; }
    public double? SizeScale { get; init; }
}

/// <summary>Portable program plus non-portable authoring state, committed as one CPU undo command.</summary>
public static class GpuParticleEditorService
{
    public static readonly GraphNodeKind Kind = new("GpuParticle");
    public const string ParameterId = "gpuState";
    public const int MaxStateBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool IsGpu(GraphNodeKind kind) => kind == Kind;
    public static GpuEditorState ReadState(GraphNodeViewModel node) => IsGpu(node.Definition.Kind)
        ? ReadState(node.Parameters, node.Id) : throw new ArgumentException("Node is not a GPU Particle.");
    public static GpuEditorState ReadState(GraphNodeSnapshot node) => IsGpu(node.Kind)
        ? ReadState(node.Parameters, node.Id) : throw new ArgumentException("Node is not a GPU Particle.");
    public static IReadOnlyList<NodeParameterSnapshot> InitialParameters(Guid id)
    {
        var state = InitialState(id);
        var program = state.Program;
        var positions = program.Kernels.SelectMany(GpuGraphLayout.CreatePositions).ToDictionary();
        return [new(ParameterId, "gpuState", JsonSerializer.SerializeToElement(state with { Positions = positions }, Json))];
    }
    private static GpuEditorState InitialState(Guid id) => new()
    {
        Program = GpuSamples.CreateEmitter() with { Id = "gpu_" + id.ToString("N") },
        ResourceAssetPaths = new() { ["billboardTexture"] = "builtin:white" },
        RendererSettings = new() { ["renderStep"] = new(SizeScale: 100) }
    };
    private static GpuEditorState ReadState(IReadOnlyList<NodeParameterSnapshot>? parameters, Guid id)
    {
        var value = parameters?.FirstOrDefault(p => p.Id == ParameterId)?.Value;
        if (value is null || value.Value.GetRawText() == "{}")
            return InitialState(id);
        if (!IsStateValue(value.Value)) throw new InvalidDataException("Invalid GPU workspace state.");
        return value.Value.Deserialize<GpuEditorState>(Json)!;
    }
    public static GpuProgram ReadProgram(GraphNodeViewModel node) => ReadState(node).Program;
    public static GpuProgram ReadProgram(GraphNodeSnapshot node) => ReadState(node).Program;
    public static bool IsStateValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > MaxStateBytes)
            return false;
        if (value.GetRawText() == "{}") return true;
        try
        {
            var state = value.Deserialize<GpuEditorState>(Json);
            if (state is null || state.Program is null || state.Program.Kernels is null ||
                state.Program.Attributes is null || state.Program.Flow is null || state.Program.Resources is null ||
                state.Positions is null || state.Drafts is null || state.ResourceAssetPaths is null ||
                state.RendererSettings is null)
                return false;
            var p = state.Program;
            if (p.Id is not { Length: > 0 and <= 128 } || p.Capacity == 0 ||
                (ulong)p.Capacity * ParticleAbi.Stride > new GpuLimits().MaxParticleBytes ||
                p.Kernels.Count > 64 || p.Flow.Count > 128 || p.Resources.Count > 64 || p.Attributes.Count > 64 ||
                p.Attributes.Any(a => a is null || a.Id is not { Length: > 0 and <= 128 } || !Enum.IsDefined(a.Type)) ||
                p.Flow.Any(f => f is null || f.Id is not { Length: > 0 and <= 128 } || !Enum.IsDefined(f.Kind) ||
                    f.Kernel is { Length: > 128 } || f.Resource is { Length: > 128 } ||
                    f.SpawnShape is { } shape && (!Enum.IsDefined(shape.Kind) || shape.HalfExtents is null ||
                        shape.HalfExtents.Count != 3 || shape.HalfExtents.Any(value => !double.IsFinite(value)) ||
                        !double.IsFinite(shape.Radius))) ||
                p.Resources.Any(r => r is null || r.Id is not { Length: > 0 and <= 128 } || !Enum.IsDefined(r.Kind) ||
                    (r.Kind == ResourceKind.FlowMap2D) != (r.FlowMap is not null) ||
                    r.FlowMap is { } flowMap && (!Enum.IsDefined(flowMap.AxisU) || !Enum.IsDefined(flowMap.AxisV) ||
                        !double.IsFinite(flowMap.WorldMinimumU) || !double.IsFinite(flowMap.WorldMaximumU) ||
                        !double.IsFinite(flowMap.WorldMinimumV) || !double.IsFinite(flowMap.WorldMaximumV))) ||
                p.Kernels.Any(k => k is null || k.Id is not { Length: > 0 and <= 128 } ||
                    !Enum.IsDefined(k.Stage) ||
                    k.Nodes is null || k.Writes is null || k.Nodes.Count > 4096 || k.Writes.Count > 4096 ||
                    k.Nodes.Any(n => n is null || n.Id is not { Length: > 0 and <= 128 } ||
                        n.Inputs is null || n.Value is null || n.Inputs.Count > 4 || n.Value.Count > 4 ||
                        !Enum.IsDefined(n.Op) || !Enum.IsDefined(n.Type) ||
                        n.Attribute is { Length: > 128 } || n.Uniform is { Length: > 128 } || n.Resource is { Length: > 128 } ||
                        n.Inputs.Any(s => s is null || s.Length > 128) ||
                        n.Value.Any(v => !double.IsFinite(v))) ||
                    k.Writes.Any(w => w is null || w.Id is not { Length: > 0 and <= 128 } ||
                        w.Attribute is not { Length: > 0 and <= 128 } || w.Value is not { Length: > 0 and <= 128 }) ||
                    k.Nodes.Select(n => n.Id).Distinct().Count() != k.Nodes.Count) ||
                p.Kernels.Select(k => k.Id).Distinct().Count() != p.Kernels.Count ||
                state.Positions.Count > 262144 || state.Positions.Any(x =>
                    x.Value is null || !double.IsFinite(x.Value.X) || !double.IsFinite(x.Value.Y)) ||
                state.Drafts.Count > 64 || state.Drafts.Any(d => d.Value is null ||
                    d.Value.Text is null || d.Value.Diagnostics is null || d.Value.Diagnostics.Any(d => d is null) ||
                    d.Value.Text.Length > 1024 * 1024) ||
                state.ResourceAssetPaths.Count > 64 || state.ResourceAssetPaths.Any(r => r.Value is null || r.Value.Length > 4096) ||
                state.RendererSettings.Count > 128 || state.RendererSettings.Any(r =>
                    r.Value is null || r.Key.Length > 128 ||
                    r.Value.Material is not ("builtin:unlit-alpha" or "builtin:unlit-additive") ||
                    !double.IsFinite(r.Value.SizeScale) || r.Value.SizeScale is <= 0 or > 10000))
                return false;
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return false; }
    }

    public static GraphNodeViewModel Create(GraphWorkspaceViewModel graph, double x = 80, double y = 80)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("GPU node coordinates must be finite.");
        return graph.CreateNode(Kind, x, y);
    }

    public static IReadOnlyList<ResourceDeclaration> BillboardTextures(GpuEditorState state)
    {
        var ids = state.Program.Flow.Where(f => f.Kind == FlowKind.RenderBillboard)
            .Select(f => f.Resource).ToHashSet();
        return state.Program.Resources.Where(r => r.Kind == ResourceKind.Texture2D && ids.Contains(r.Id)).ToArray();
    }

    public static void AssignBillboardTexture(GraphWorkspaceViewModel graph, GraphNodeViewModel node, EngineAsset asset)
    {
        if (asset.AssetClass != "texture") throw new ArgumentException("Select a texture asset.");
        PreviewAssetStore.ValidateAssetPath(asset.Path);
        var resources = BillboardTextures(ReadState(node));
        if (resources.Count != 1) throw new InvalidOperationException("Select the texture resource inside the GPU workspace.");
        AssignTextureResource(graph, node, asset, resources[0].Id);
    }

    public static void AssignTextureResource(GraphWorkspaceViewModel graph, GraphNodeViewModel node,
        EngineAsset asset, string resourceId)
    {
        if (!graph.Nodes.Contains(node)) throw new ArgumentException("Select a node in the active graph.");
        if (asset.AssetClass != "texture") throw new ArgumentException("Select a texture asset.");
        PreviewAssetStore.ValidateAssetPath(asset.Path);
        var resource = ReadState(node).Program.Resources.SingleOrDefault(resource =>
            resource.Id == resourceId && (resource.Kind is ResourceKind.Texture2D or ResourceKind.FlowMap2D))
            ?? throw new ArgumentException("Select an existing texture resource.");
        Apply(graph, node.Id, [new() { Action = "resource", Resource = resource, AssetPath = asset.Path }]);
    }

    public static IReadOnlyList<GpuDiagnostic> Diagnostics(GpuEditorState state) =>
        GpuCompiler.Validate(state.Program).Concat(state.Drafts.SelectMany(d => d.Value.Diagnostics)).ToArray();

    public static GpuEditorState Apply(GraphWorkspaceViewModel graph, Guid container, IReadOnlyList<GpuEdit> edits,
        bool requireValid = false)
    {
        var node = graph.GetNode(container);
        if (!IsGpu(node.Definition.Kind)) throw new ArgumentException("Select a GPU Particle container.");
        if (edits.Count > 4096 || edits.Any(e => e is null)) throw new ArgumentException("GPU plan exceeds 4096 edits or contains a null edit.");
        var state = ReadState(node);
        // Deep-copy before staging: failed plans never mutate the existing workspace.
        state = JsonSerializer.Deserialize<GpuEditorState>(JsonSerializer.Serialize(state, Json), Json)!;
        foreach (var edit in edits) state = Stage(state, edit);
        var value = JsonSerializer.SerializeToElement(state, Json);
        if (!IsStateValue(value)) throw new ArgumentException("GPU state exceeds authoring limits.");
        if (requireValid && Diagnostics(state) is { Count: > 0 } errors) throw new GpuValidationException(errors);
        graph.SetParameter(node, ParameterId, value);
        return state;
    }

    private static GpuEditorState Stage(GpuEditorState state, GpuEdit edit)
    {
        var p = state.Program;
        if (edit.Action == "renderer")
        {
            var flow = p.Flow.SingleOrDefault(f => f.Id == edit.NodeId && f.Kind == FlowKind.RenderBillboard)
                ?? throw new ArgumentException("Select a billboard render step.");
            var current = state.RendererSettings.GetValueOrDefault(flow.Id) ?? new GpuRendererSettings();
            state.RendererSettings[flow.Id] = new(edit.Material ?? current.Material, edit.SizeScale ?? current.SizeScale);
            return state;
        }
        if (edit.Action == "capacity")
            return state with { Program = p with { Capacity = edit.Capacity ?? throw new ArgumentException("Capacity required.") } };
        if (edit.Action == "flow-add")
        {
            var flow = edit.Flow ?? throw new ArgumentException("Flow required.");
            if (p.Flow.Any(f => f.Id == flow.Id)) throw new ArgumentException("Duplicate flow ID.");
            p.Flow.Add(flow);
            if (edit.Kernel is { } kernel) p.Kernels.Add(kernel);
            return state;
        }
        if (edit.Action == "flow-remove")
        {
            if (p.Flow.RemoveAll(f => f.Id == edit.NodeId) == 0) throw new ArgumentException("Flow not found.");
            state.RendererSettings.Remove(edit.NodeId!);
            return state;
        }
        if (edit.Action == "flow-update")
        {
            var flow = edit.Flow ?? throw new ArgumentException("Flow required.");
            var index = p.Flow.FindIndex(f => f.Id == flow.Id);
            if (index < 0) throw new ArgumentException("Flow not found.");
            p.Flow[index] = flow;
            return state;
        }
        if (edit.Action == "flow-move")
        {
            var index = p.Flow.FindIndex(f => f.Id == edit.NodeId);
            if (index < 0 || edit.Input < 0 || edit.Input >= p.Flow.Count) throw new ArgumentException("Invalid flow index.");
            var flow = p.Flow[index]; p.Flow.RemoveAt(index); p.Flow.Insert(edit.Input, flow);
            return state;
        }
        if (edit.Action == "resource")
        {
            var resource = edit.Resource ?? throw new ArgumentException("Resource required.");
            var index = p.Resources.FindIndex(r => r.Id == resource.Id);
            if (index < 0) p.Resources.Add(resource); else p.Resources[index] = resource;
            if (edit.AssetPath is { } path)
            {
                if (path.Length > 4096) throw new ArgumentException("Asset path too long.");
                state.ResourceAssetPaths[resource.Id] = path;
            }
            return state;
        }
        var k = p.Kernels.SingleOrDefault(k => k.Id == edit.KernelId)
            ?? throw new ArgumentException("Kernel not found.");
        if (edit.Action == "text")
        {
            var text = edit.Text ?? "";
            try
            {
                DslLoweringOptions? maps = null;
                try
                {
                    var emitted = GpuDsl.Emit(k, resources: p.Resources);
                    if (emitted.Text == text)
                        maps = new() { NodeIdsBySpan = emitted.NodeIdsBySpan, WriteIdsBySpan = emitted.WriteIdsBySpan };
                }
                catch (GpuValidationException) { }
                var replacement = GpuDsl.Compile(text, k.Id, k.Stage, options: maps, resources: p.Resources);
                var candidate = p with { Kernels = p.Kernels.Select(x => x.Id == k.Id ? replacement : x).ToList() };
                var errors = GpuCompiler.Validate(candidate).Where(d => d.KernelId == k.Id).ToList();
                if (errors.Count > 0) throw new GpuValidationException(errors);
                state.Drafts.Remove(k.Id);
                return state with { Program = candidate };
            }
            catch (GpuValidationException e)
            {
                state.Drafts[k.Id] = new(text, e.Diagnostics.Select(d => d with { KernelId = d.KernelId ?? k.Id }).ToList());
                return state;
            }
        }
        if (edit.Action == "draft")
        {
            state.Drafts[k.Id] = new(edit.Text ?? "", [new("UnappliedText", "Text draft has not been applied.", k.Id)]);
            return state;
        }
        if (edit.Action == "discard-draft") { state.Drafts.Remove(k.Id); return state; }
        if (edit.Action == "add")
        {
            var n = edit.Node ?? throw new ArgumentException("Node required.");
            if (k.Nodes.Any(n2 => n2.Id == n.Id)) throw new ArgumentException("Duplicate node ID.");
            k.Nodes.Add(n);
            state.Positions[Key(k.Id, n.Id)] = edit.Position ?? new(60 + k.Nodes.Count * 30, 60 + k.Nodes.Count * 40);
        }
        else if (edit.Action == "remove")
        {
            if (k.Nodes.RemoveAll(n => n.Id == edit.NodeId) == 0) throw new ArgumentException("Node not found.");
            foreach (var n in k.Nodes)
                for (var i = 0; i < n.Inputs.Count; i++) if (n.Inputs[i] == edit.NodeId) n.Inputs[i] = "";
            k.Writes.RemoveAll(w => w.Value == edit.NodeId);
            state.Positions.Remove(Key(k.Id, edit.NodeId!));
        }
        else if (edit.Action == "update")
        {
            var n = edit.Node ?? throw new ArgumentException("Node required.");
            var index = k.Nodes.FindIndex(n2 => n2.Id == n.Id);
            if (index < 0) throw new ArgumentException("Node not found.");
            k.Nodes[index] = n;
        }
        else if (edit.Action == "move")
        {
            if (!k.Nodes.Any(n => n.Id == edit.NodeId)) throw new ArgumentException("Node not found.");
            state.Positions[Key(k.Id, edit.NodeId!)] = edit.Position ?? throw new ArgumentException("Position required.");
        }
        else if (edit.Action == "connect" || edit.Action == "disconnect")
        {
            var n = k.Nodes.Single(n => n.Id == edit.NodeId);
            var descriptor = GpuOperationRegistry.Operations[n.Op];
            if (edit.Input < 0 || edit.Input >= descriptor.MaxInputs) throw new ArgumentException("Input index out of range.");
            if (edit.Action == "connect")
            {
                if (!k.Nodes.Any(s => s.Id == edit.SourceId)) throw new ArgumentException("Source not found.");
                bool Reaches(string id, HashSet<string> visited) => id == n.Id ||
                    visited.Add(id) && k.Nodes.Single(s => s.Id == id).Inputs
                        .Where(s => k.Nodes.Any(x => x.Id == s)).Any(s => Reaches(s, visited));
                if (Reaches(edit.SourceId!, [])) throw new ArgumentException("Connections must be acyclic.");
            }
            while (n.Inputs.Count <= edit.Input) n.Inputs.Add("");
            n.Inputs[edit.Input] = edit.Action == "disconnect" ? "" : edit.SourceId!;
            if (edit.Action == "connect" && n.Op == GpuOp.SampleFlowMap && edit.Input == 0)
            {
                var source = k.Nodes.Single(candidate => candidate.Id == edit.SourceId);
                if (source.Type is GpuType.Float2 or GpuType.Float3)
                {
                    var nodeIndex = k.Nodes.FindIndex(candidate => candidate.Id == n.Id);
                    k.Nodes[nodeIndex] = n with { Type = source.Type };
                }
            }
        }
        else if (edit.Action == "write")
        {
            var w = edit.Write ?? throw new ArgumentException("Attribute write required.");
            var index = k.Writes.FindIndex(w2 => w2.Id == w.Id);
            if (index < 0) k.Writes.Add(w); else k.Writes[index] = w;
        }
        else if (edit.Action == "write-remove") k.Writes.RemoveAll(w => w.Id == edit.NodeId);
        else throw new ArgumentException("Unknown GPU edit action.");
        return state;
    }

    public static string Key(string kernel, string node) => kernel + "/" + node;
    public static CompiledGpuProgram Compile(GpuEditorState state)
    {
        if (Diagnostics(state) is { Count: > 0 } errors) throw new GpuValidationException(errors);
        return GpuCompiler.Compile(state.Program);
    }
    public static string Export(GpuEditorState state, string format)
    {
        var compiled = Compile(state);
        return format switch
        {
            "json" => compiled.CanonicalJson,
            "usf" => compiled.EmitUsf().Text,
            "hlsl" => compiled.EmitHlslInclude().Text,
            "compute" => compiled.EmitUnityCompute().Text,
            _ => throw new ArgumentException("Format must be json, usf, hlsl or compute.")
        };
    }
    public static string SafeFileName(string id) =>
        new string(id.Take(80).Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
    public static async Task WriteAtomicAsync(string destination, string source)
    {
        var full = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(full)!;
        var staged = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".writing");
        try
        {
            await File.WriteAllTextAsync(staged, source, new UTF8Encoding(false));
            File.Move(staged, full, overwrite: true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
}
