using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibraGpu.Core;

public static class GpuCompiler
{
    private static readonly JsonSerializerOptions Json = CreateOptions();
    internal static string SerializeMetadata(GpuExportMetadata metadata) => JsonSerializer.Serialize(metadata, Json);
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter(null, false));
        return options;
    }

    public static GpuProgram Parse(string json, GpuLimits? limits = null)
    {
        limits ??= new();
        CheckLimits(limits);
        if (json is null || Encoding.UTF8.GetByteCount(json) > limits.MaxJsonBytes)
            throw Error("JsonLimit", "JSON is null or exceeds the byte limit.");
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = limits.MaxJsonDepth });
            Inspect(doc.RootElement, limits);
            var program = JsonSerializer.Deserialize<GpuProgram>(json, Json)
                ?? throw Error("JsonNull", "A program object is required.");
            var diagnostics = Validate(program, limits);
            if (diagnostics.Count != 0) throw new GpuValidationException(diagnostics);
            return program;
        }
        catch (JsonException e) { throw Error("InvalidJson", e.Message); }
    }

    private static void Inspect(JsonElement e, GpuLimits limits)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in e.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Error("DuplicateProperty", $"Duplicate or case-variant property '{property.Name}'.");
                if (names.Count > limits.MaxPropertiesPerObject)
                    throw Error("PropertyLimit", "Object exceeds the property limit.");
                Inspect(property.Value, limits);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray()) Inspect(item, limits);
        }
        else if (e.ValueKind == JsonValueKind.Number && (!e.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw Error("NonFinite", "JSON numbers must be finite.");
    }

    public static string Serialize(GpuProgram program, GpuLimits? limits = null)
    {
        var diagnostics = Validate(program, limits);
        if (diagnostics.Count != 0) throw new GpuValidationException(diagnostics);
        var text = JsonSerializer.Serialize(Canonical(program), Json);
        limits ??= new();
        if (Encoding.UTF8.GetByteCount(text) > limits.MaxJsonBytes) throw Error("JsonLimit", "Serialized JSON exceeds the byte limit.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = limits.MaxJsonDepth });
        Inspect(document.RootElement, limits);
        return text;
    }

    private static GpuProgram Canonical(GpuProgram p) => p with
    {
        Attributes = p.Attributes.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
        Resources = p.Resources.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
        Kernels = p.Kernels.OrderBy(x => x.Id, StringComparer.Ordinal).Select(k => k with
        {
            Nodes = k.Nodes.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            Writes = k.Writes.OrderBy(x => x.Attribute, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToList()
        }).ToList()
    };

    public static CompiledGpuProgram Compile(GpuProgram program, GpuLimits? limits = null)
    {
        limits ??= new();
        // Snapshot frontend-owned lists before publishing immutable compiled metadata.
        var canonicalJson = Serialize(program, limits);
        var snapshot = Parse(canonicalJson, limits);
        var lowered = GpuEmitterShapeLowering.Lower(snapshot);
        var loweredDiagnostics = Validate(lowered, limits);
        if (loweredDiagnostics.Count != 0) throw new GpuValidationException(loweredDiagnostics);
        return ShaderEmitter.Compile(lowered, canonicalJson);
    }
    public static CompiledGpuProgram Compile(string json, GpuLimits? limits = null) => Compile(Parse(json, limits), limits);
    private static GpuValidationException Error(string code, string message) => new([new(code, message)]);
    private static void CheckLimits(GpuLimits l)
    {
        if (l.MaxJsonBytes < 1 || l.MaxJsonDepth is < 1 or > 64 || l.MaxPropertiesPerObject < 1 ||
            l.MaxKernels is < 1 or > 1024 || l.MaxNodesPerKernel is < 1 or > 65536 ||
            l.MaxResources is < 1 or > 1024 || l.MaxFlowSteps is < 1 or > 4096 ||
            l.MaxParticleBytes < 80 || l.MaxResourceTexels < 1)
            throw new ArgumentOutOfRangeException(nameof(l), "Invalid or unsafe compiler limits.");
    }

    public static IReadOnlyList<GpuDiagnostic> Validate(GpuProgram? program, GpuLimits? limits = null)
    {
        limits ??= new();
        CheckLimits(limits);
        var d = new List<GpuDiagnostic>();
        void Add(string c, string m, string? k = null, string? n = null, string? pin = null) => d.Add(new(c, m, k, n, pin));
        if (program is null) { Add("NullProgram", "Program is required."); return d.AsReadOnly(); }
        var p = program;
        if (p.Version != 1) Add("Version", "Only IR version 1 is supported.");
        if (p.SpawnRate is { } rate && (!double.IsFinite(rate) || rate < 0 || rate > 1_000_000))
            Add("SpawnRate", "Spawn rate must be finite and between 0 and 1000000 particles per second.");
        if (p.InitialBurst is { } burst && burst > p.Capacity)
            Add("InitialBurst", "Initial burst must not exceed particle capacity.");
        if (p.WarmupTime is { } warmup && (!double.IsFinite(warmup) || warmup < 0 || warmup > 600))
            Add("WarmupTime", "Warmup time must be finite and between 0 and 600 seconds.");
        if (!ValidId(p.Id)) Add("Id", "Program ID must contain 1–128 non-control characters.");
        if (p.Capacity == 0 || (ulong)p.Capacity * ParticleAbi.Stride > limits.MaxParticleBytes ||
            (ulong)p.Capacity * ParticleAbi.Stride > uint.MaxValue)
            Add("Capacity", "Particle allocation is zero or exceeds byte/address limits.");
        if (p.Attributes is null || p.Resources is null || p.Kernels is null || p.Flow is null)
        { Add("NullCollection", "Program collections cannot be null."); return d.AsReadOnly(); }
        if (p.Kernels.Count > limits.MaxKernels || p.Resources.Count > limits.MaxResources || p.Flow.Count > limits.MaxFlowSteps ||
            p.Attributes.Count > limits.MaxAttributes)
        { Add("Limit", "Program collection limit exceeded."); return d.AsReadOnly(); }
        var all = new HashSet<string>(StringComparer.Ordinal) { p.Id };
        void GlobalId(string? id)
        {
            if (!ValidId(id)) Add("Id", "ID must contain 1–128 non-control characters.");
            else if (!all.Add(id!)) Add("DuplicateId", $"Duplicate program-level ID '{id}'.");
        }
        var attributes = new Dictionary<string, GpuType>(StringComparer.Ordinal);
        foreach (var a in p.Attributes)
        {
            if (a is null || !ValidId(a.Id)) { Add("Attribute", "Invalid attribute declaration."); continue; }
            if (!attributes.TryAdd(a.Id, a.Type)) Add("DuplicateId", $"Duplicate attribute '{a.Id}'.");
            var abi = ParticleAbi.Attributes.FirstOrDefault(x => x.Id == a.Id);
            if (abi is not null && abi.Type != a.Type)
                Add("AttributeAbi", $"Attribute '{a.Id}' does not match the fixed particle ABI.");
            else if (abi is null && (!Enum.IsDefined(a.Type) || a.Type == GpuType.Quaternion))
                Add("AttributeAbi", $"Custom attribute '{a.Id}' has an unsupported type.");
        }
        foreach (var required in ParticleAbi.Attributes)
            if (!attributes.ContainsKey(required.Id)) Add("AttributeAbi", $"Missing ABI attribute '{required.Id}'.");
        var resources = new Dictionary<string, ResourceDeclaration>(StringComparer.Ordinal);
        foreach (var r in p.Resources)
        {
            if (r is null) { Add("Resource", "Null resource."); continue; }
            GlobalId(r.Id);
            if (ValidId(r.Id)) resources.TryAdd(r.Id, r);
            if (!Enum.IsDefined(r.Kind)) Add("ResourceKind", "Unknown resource kind.");
            if (r.Kind is not (ResourceKind.Texture2D or ResourceKind.FlowMap2D))
                Add("UnsupportedResource", "Storage textures and buffers are reserved, not implemented.");
            if (r.Kind == ResourceKind.FlowMap2D)
            {
                if (r.FlowMap is not { } settings)
                    Add("FlowMapSettings", "FlowMap2D resources require sampling settings.");
                else
                {
                    var bounds = new[] { settings.WorldMinimumU, settings.WorldMaximumU, settings.WorldMinimumV, settings.WorldMaximumV };
                    var ranges = new[] { settings.WorldMaximumU - settings.WorldMinimumU, settings.WorldMaximumV - settings.WorldMinimumV };
                    if (!Enum.IsDefined(settings.Encoding) || !Enum.IsDefined(settings.Coordinates) || !Enum.IsDefined(settings.AxisU) || !Enum.IsDefined(settings.AxisV) || settings.AxisU == settings.AxisV ||
                        bounds.Any(value => !double.IsFinite(value) || !float.IsFinite((float)value)) ||
                        ranges.Any(value => !double.IsFinite(value) || !float.IsFinite((float)value) || value <= 0))
                        Add("FlowMapSettings", "Flow-map axes must be distinct and world bounds must be finite, increasing, and representable as floats.");
                }
            }
            else if (r.FlowMap is not null)
                Add("FlowMapSettings", "Only FlowMap2D resources may define flow-map sampling settings.");
            if ((r.Width == 0) != (r.Height == 0) || (ulong)r.Width * r.Height > limits.MaxResourceTexels)
                Add("ResourceSize", "Texture dimensions must both be zero (external) or bounded positive values.");
        }
        var kernels = new Dictionary<string, KernelGraph>(StringComparer.Ordinal);
        foreach (var k in p.Kernels)
        {
            if (k is null) { Add("Kernel", "Null kernel."); continue; }
            GlobalId(k.Id);
            if (ValidId(k.Id)) kernels.TryAdd(k.Id, k);
            if (!Enum.IsDefined(k.Stage)) Add("Stage", "Unsupported kernel stage.", k.Id);
            ValidateKernel(k, attributes, resources, limits, d);
        }
        bool rendered = false;
        bool updated = false;
        int spawnSteps = 0, updateSteps = 0;
        var executed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in p.Flow)
        {
            if (f is null) { Add("Flow", "Null flow step."); continue; }
            GlobalId(f.Id);
            if (!Enum.IsDefined(f.Kind)) { Add("FlowKind", "Unknown execution flow kind."); continue; }
            if (f.SpawnShape is { } shape)
            {
                if (f.Kind != FlowKind.Spawn)
                    Add("SpawnShape", "Emitter shapes are only valid on Spawn flow steps.");
                if (!Enum.IsDefined(shape.Kind))
                    Add("SpawnShape", "Unknown emitter shape.");
                if (shape.HalfExtents is null || shape.HalfExtents.Count != 3 ||
                    shape.HalfExtents.Any(value => !double.IsFinite(value) || !float.IsFinite((float)value)) ||
                    !double.IsFinite(shape.Radius) || !float.IsFinite((float)shape.Radius))
                    Add("SpawnShape", "Shape dimensions must be finite Float3 values.");
                else if (shape.Kind == GpuSpawnShapeKind.Box && shape.HalfExtents.Any(value => value < 0) ||
                    shape.Kind is GpuSpawnShapeKind.Sphere or GpuSpawnShapeKind.Circle && shape.Radius < 0)
                    Add("SpawnShape", "Half-extents and radius must be non-negative.");
            }
            if (f.Kind is FlowKind.RenderStartTexture or FlowKind.ComputePass)
            { Add("UnsupportedPass", "Texture simulation passes are declared but not implemented."); continue; }
            if (f.Kind is FlowKind.Spawn or FlowKind.Update)
            {
                if (f.Kind == FlowKind.Spawn ? ++spawnSteps > 1 : ++updateSteps > 1)
                    Add("StageLimit", "Version 1 supports one Spawn and one Update dispatch per flow.");
                if (rendered || f.Kind == FlowKind.Spawn && updated)
                    Add("FlowOrder", "Spawn must precede Update, and all kernels must precede rendering.");
                if (f.Kind == FlowKind.Update) updated = true;
                if (f.Resource is not null) Add("FlowBinding", "Particle kernels do not accept a texture resource.");
                if (f.Kernel is null || !kernels.TryGetValue(f.Kernel, out var kernel))
                    Add("UndefinedKernel", "Flow references an undefined kernel.");
                else
                {
                    if ((f.Kind == FlowKind.Spawn ? KernelStage.Spawn : KernelStage.Update) != kernel.Stage)
                        Add("StageMismatch", "Flow and kernel stages differ.", kernel.Id);
                    if (!executed.Add(kernel.Id)) Add("DuplicateDispatch", "A kernel may execute only once per flow.", kernel.Id);
                }
            }
            else
            {
                rendered = true;
                if (f.Kernel is not null) Add("FlowBinding", "Billboard rendering does not reference a compute kernel.");
                if (f.Resource is null || !resources.TryGetValue(f.Resource, out var r) || r.Kind != ResourceKind.Texture2D)
                    Add("UndefinedResource", "Billboard rendering requires a Texture2D resource.");
            }
        }
        if (p.Flow.Count == 0) Add("EmptyFlow", "At least one execution step is required.");
        return d.AsReadOnly();
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
    private static void ValidateKernel(KernelGraph k, Dictionary<string, GpuType> attrs,
        Dictionary<string, ResourceDeclaration> resources, GpuLimits limits, List<GpuDiagnostic> d)
    {
        void Add(string c, string m, string? n = null, string? pin = null) => d.Add(new(c, m, k.Id, n, pin,
            k.Nodes?.FirstOrDefault(x => x is not null && x.Id == n)?.SourceSpan ??
            k.Writes?.FirstOrDefault(x => x is not null && x.Id == n)?.SourceSpan));
        if (k.Nodes is null || k.Writes is null) { Add("NullCollection", "Kernel collections cannot be null."); return; }
        if (k.Nodes.Count > limits.MaxNodesPerKernel || k.Writes.Count > attrs.Count)
        { Add("Limit", "Kernel exceeds node/write limits."); return; }
        var nodes = new Dictionary<string, GpuNode>(StringComparer.Ordinal);
        var local = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in k.Nodes)
        {
            if (n is null || !ValidId(n.Id)) { Add("Id", "Invalid node ID."); continue; }
            if (!local.Add(n.Id)) Add("DuplicateId", "Duplicate kernel-local ID.", n.Id);
            nodes.TryAdd(n.Id, n);
        }
        foreach (var n in nodes.Values)
        {
            if (!Enum.IsDefined(n.Type) || n.Type == GpuType.Quaternion)
                Add("UnsupportedType", "Unknown types and semantic Quaternion are unsupported.", n.Id, "out");
            if (n.SourceSpan is { } span && (span.Start < 0 || span.Length < 0 || (long)span.Start + span.Length > int.MaxValue))
                Add("TextSpan", "Invalid source span.", n.Id);
            if (n.Inputs is null || n.Value is null) { Add("NullCollection", "Node collections cannot be null.", n.Id); continue; }
            if (!GpuOperationRegistry.Operations.TryGetValue(n.Op, out var op))
            { Add("UnsupportedOp", "Operation is not in the safe GPU registry.", n.Id); continue; }
            if (n.Inputs.Count < op.MinInputs || n.Inputs.Count > op.MaxInputs)
            { Add("Arity", "Wrong operation input count.", n.Id); continue; }
            var input = new List<GpuType>();
            for (int i = 0; i < n.Inputs.Count; i++)
            {
                if (n.Inputs[i] is null || !nodes.TryGetValue(n.Inputs[i], out var referenced))
                    Add("UndefinedNode", "Undefined input reference.", n.Id, $"in{i}");
                else input.Add(referenced.Type);
            }
            if (n.Op != GpuOp.Constant && n.Value.Count != 0 ||
                n.Op != GpuOp.ReadAttribute && n.Attribute is not null ||
                n.Op != GpuOp.ReadUniform && n.Uniform is not null ||
                n.Op != GpuOp.Component && n.ComponentIndex is not null ||
                n.Op != GpuOp.SampleFlowMap && n.Resource is not null ||
                n.Op != GpuOp.FCurve && n.Curve is not null)
                Add("Payload", "Unexpected operation payload.", n.Id);
            if (n.Op == GpuOp.FCurve)
            {
                if (n.Curve is null || n.Curve.Keys is not { Count: >= 1 and <= 64 } ||
                    n.Curve.Keys.Any(key => key is null || !double.IsFinite(key.Time) || !double.IsFinite(key.Value) ||
                        !double.IsFinite(key.InTangent) || !double.IsFinite(key.OutTangent) ||
                        Math.Abs(key.Time) > 1e6 || Math.Abs(key.Value) > 1e6))
                    Add("Curve", "F-Curve requires 1-64 finite keys.", n.Id, "curve");
            }
            if (n.Op == GpuOp.Constant)
            {
                if (n.Value.Count != GpuOperationRegistry.Components(n.Type)) Add("Components", "Wrong literal component count.", n.Id, "value");
                foreach (var v in n.Value.Take(4))
                {
                    bool valid = double.IsFinite(v) && (GpuOperationRegistry.Float(n.Type) ? Math.Abs(v) <= float.MaxValue :
                        n.Type == GpuType.Int ? v >= int.MinValue && v <= int.MaxValue && Math.Truncate(v) == v :
                        n.Type == GpuType.UInt ? v >= 0 && v <= uint.MaxValue && Math.Truncate(v) == v :
                        n.Type == GpuType.Bool && v is 0 or 1);
                    if (!valid) Add("Literal", "Literal is nonfinite, out of range, or not an exact integer/bool.", n.Id, "value");
                }
            }
            if (input.Count != n.Inputs.Count) continue;
            bool validType = op.Policy switch
            {
                TypePolicy.Literal => true,
                TypePolicy.Attribute => n.Attribute is not null && attrs.TryGetValue(n.Attribute, out var t) && t == n.Type,
                TypePolicy.Uniform => n.Uniform is not null && ParticleAbi.Uniforms.TryGetValue(n.Uniform, out var t) && t == n.Type,
                TypePolicy.Component => n.Type == GpuType.Float && GpuOperationRegistry.Float(input[0]) &&
                    GpuOperationRegistry.Components(input[0]) > 1 && n.ComponentIndex is >= 0 &&
                    n.ComponentIndex < GpuOperationRegistry.Components(input[0]),
                _ => GpuOperationRegistry.TypesMatch(n.Op, n.Type, input)
            };
            if (!validType) Add("TypeMismatch", "Operation types or declared attribute/uniform do not match.", n.Id, "out");
            if (n.Op == GpuOp.SampleFlowMap &&
                (n.Resource is null || !resources.TryGetValue(n.Resource, out var resource) || resource.Kind != ResourceKind.FlowMap2D))
                Add("FlowMapResource", "Flow-map sampling requires a declared FlowMap2D resource.", n.Id, "resource");
            if (op.Policy == TypePolicy.FractalNoise)
                for (int i = 2; i < 4; i++)
                    if (nodes.TryGetValue(n.Inputs[i], out var parameter) && parameter.Op == GpuOp.Constant && parameter.Value?.Count == 1 &&
                        (parameter.Value[0] < op.Inputs[i].Minimum || parameter.Value[0] > op.Inputs[i].Maximum))
                        Add("NoiseParameter", "Noise octaves must be 1–8 and gain must be 0–1.", n.Id, $"in{i}");
        }
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in k.Writes)
        {
            if (w is null) { Add("Write", "Null write."); continue; }
            if (w.SourceSpan is { } span && (span.Start < 0 || span.Length < 0 || (long)span.Start + span.Length > int.MaxValue))
                Add("TextSpan", "Invalid source span.", w.Id);
            if (!ValidId(w.Id)) Add("Id", "Invalid write ID.", w.Id);
            else if (!local.Add(w.Id)) Add("DuplicateId", "Duplicate kernel-local ID.", w.Id);
            if (w.Attribute is null || !written.Add(w.Attribute)) Add("DuplicateWrite", "An attribute may be written only once per kernel.", w.Id, "attribute");
            if (w.Attribute is null || !attrs.TryGetValue(w.Attribute, out var type)) Add("IllegalWrite", "Unknown particle attribute.", w.Id, "attribute");
            else if (w.Value is null || !nodes.TryGetValue(w.Value, out var value)) Add("UndefinedNode", "Write references an undefined node.", w.Id, "value");
            else if (type != value.Type) Add("TypeMismatch", "Attribute write requires an exact type.", w.Id, "value");
        }
        if (Topological(nodes).Count != nodes.Count) Add("Cycle", "Kernel dataflow contains a cycle.");
    }

    internal static List<GpuNode> Topological(IReadOnlyDictionary<string, GpuNode> nodes)
    {
        var incoming = nodes.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        var consumers = nodes.Keys.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var n in nodes.Values)
            foreach (var id in n.Inputs ?? [])
                if (id is not null && nodes.ContainsKey(id)) { incoming[n.Id]++; consumers[id].Add(n.Id); }
        var ready = new SortedSet<string>(incoming.Where(x => x.Value == 0).Select(x => x.Key), StringComparer.Ordinal);
        var result = new List<GpuNode>();
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            result.Add(nodes[id]);
            foreach (var next in consumers[id]) if (--incoming[next] == 0) ready.Add(next);
        }
        return result;
    }
}
