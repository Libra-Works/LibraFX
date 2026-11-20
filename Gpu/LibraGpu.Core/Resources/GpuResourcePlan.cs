using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibraGpu.Core;

public enum GpuFlowResourceKind { StorageBuffer, ParticleBuffer, Texture2D, Texture3D, RenderTarget }
public enum GpuResourceFormat { Float4, Rgba16Float, R32Float, Particle80, Rgba32Float }
public enum GpuResourceLifetime { Persistent, Temporary, External }
public enum GpuResourceInitialization { Undefined, Zero, External }
public enum GpuResourceAccess { StorageRead, StorageWrite, Sample, RenderTarget, VertexRead }
public enum GpuResourceFrame { Current, Previous }

/// <summary>Identity is local to an effect scope instance, never an engine asset path.</summary>
public sealed record GpuFlowResource
{
    public string Id { get; init; } = "";
    public GpuFlowResourceKind Kind { get; init; }
    public GpuResourceFormat Format { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; } = 1;
    public uint Depth { get; init; } = 1;
    public GpuResourceLifetime Lifetime { get; init; }
    public GpuResourceInitialization Initialization { get; init; }
    public string? ExternalBinding { get; init; }
}

public sealed record GpuResourceUse(string Binding, string Resource, uint Version, GpuResourceAccess Access,
    GpuResourceFrame Frame = GpuResourceFrame.Current);
public sealed record GpuResourcePass
{
    public string Id { get; init; } = "";
    public string Operation { get; init; } = "";
    public List<GpuResourceUse> Uses { get; init; } = [];
    public List<string> After { get; init; } = [];
}
public sealed record GpuResourcePlan
{
    public int Version { get; init; } = 1;
    public List<GpuFlowResource> Resources { get; init; } = [];
    public List<GpuResourcePass> Passes { get; init; } = [];
}

public sealed record GpuResourceTransition(string Resource, string BeforePass, string AfterPass,
    GpuResourceAccess Before, GpuResourceAccess After, GpuResourceFrame Frame);
public sealed record GpuResourceAllocation(GpuFlowResource Resource, ulong Bytes, int FirstPass, int LastPass,
    bool RequiresHistory);
public sealed record CompiledGpuResourcePass(string Id, string Operation, IReadOnlyList<GpuResourceUse> Uses);
public sealed record CompiledGpuResourcePlan(IReadOnlyList<CompiledGpuResourcePass> Passes,
    IReadOnlyList<GpuResourceAllocation> Allocations, IReadOnlyList<GpuResourceTransition> Transitions,
    ulong OwnedBytes);

/// <summary>Adapters must provide their actual device/pipeline capabilities, including external bindings.</summary>
public sealed record GpuExternalResourceBinding(GpuFlowResourceKind Kind, GpuResourceFormat Format,
    uint Width, uint Height, uint Depth, IReadOnlySet<GpuResourceAccess> Accesses);
public sealed record GpuResourceCapabilities(
    IReadOnlySet<GpuFlowResourceKind> Kinds,
    IReadOnlySet<GpuResourceFormat> Formats,
    IReadOnlySet<GpuResourceAccess> Accesses,
    IReadOnlyDictionary<string, GpuExternalResourceBinding> ExternalBindings,
    ulong MaximumOwnedBytes);

public static class GpuResourcePlanCompiler
{
    private const int MaximumResources = 256;
    private const int MaximumPasses = 4096;
    private const int MaximumJsonBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        MaxDepth = 16,
        WriteIndented = true
    };

    public static GpuResourcePlan Parse(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw Error("ResourceJsonLimit", "Resource plan exceeds the input limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            Inspect(document.RootElement);
            var plan = JsonSerializer.Deserialize<GpuResourcePlan>(json, Json)
                ?? throw Error("ResourceJson", "A resource plan object is required.");
            Compile(plan);
            return plan;
        }
        catch (JsonException e) { throw Error("ResourceJson", e.Message); }
    }

    private static void Inspect(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Error("ResourceJson", "Duplicate resource plan property.");
                Inspect(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Inspect(item);
    }

    public static string Serialize(GpuResourcePlan plan)
    {
        Compile(plan);
        var canonical = plan with
        {
            Resources = plan.Resources.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Passes = plan.Passes.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p with
            {
                Uses = p.Uses.OrderBy(u => u.Binding, StringComparer.Ordinal).ToList(),
                After = p.After.Order(StringComparer.Ordinal).ToList()
            }).ToList()
        };
        var json = JsonSerializer.Serialize(canonical, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw Error("ResourceJsonLimit", "Resource plan exceeds the output limit.");
        return json;
    }

    public static CompiledGpuResourcePlan Compile(GpuResourcePlan plan, GpuResourceCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Version != 1 || plan.Resources is null || plan.Passes is null ||
            plan.Resources.Count > MaximumResources || plan.Passes.Count > MaximumPasses)
            throw Error("ResourcePlan", "Unsupported resource plan version, null collection or excessive plan size.");
        var resources = new Dictionary<string, GpuFlowResource>(StringComparer.Ordinal);
        var bytes = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var resource in plan.Resources)
        {
            if (resource is null || !Id(resource.Id) || !resources.TryAdd(resource.Id, resource))
                throw Error("ResourceIdentity", "Resource identities must be unique nonempty scope-local names.");
            bytes.Add(resource.Id, ValidateResource(resource));
            if (capabilities is not null && (!capabilities.Kinds.Contains(resource.Kind) ||
                !capabilities.Formats.Contains(resource.Format) || resource.ExternalBinding is { } binding &&
                (!capabilities.ExternalBindings.TryGetValue(binding, out var external) ||
                    external.Kind != resource.Kind || external.Format != resource.Format ||
                    external.Width != resource.Width || external.Height != resource.Height || external.Depth != resource.Depth)))
                throw Error("ResourceCapability", $"Unsupported resource or external binding '{resource.Id}'.");
        }
        var passes = new Dictionary<string, GpuResourcePass>(StringComparer.Ordinal);
        foreach (var pass in plan.Passes)
        {
            if (pass is null || !Id(pass.Id) || !Id(pass.Operation) || pass.Uses is null || pass.After is null ||
                pass.Uses.Count > MaximumResources || pass.After.Count > MaximumPasses || !passes.TryAdd(pass.Id, pass))
                throw Error("ResourcePass", "Passes require unique identities, operations and bounded non-null bindings.");
        }
        var edges = passes.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var indegree = passes.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        void Edge(string before, string after)
        {
            if (!passes.ContainsKey(before)) throw Error("ResourceDependency", $"Missing prerequisite pass '{before}'.");
            if (edges[before].Add(after)) indegree[after]++;
        }
        var writers = new Dictionary<(string Resource, uint Version), string>();
        var uses = new Dictionary<string, List<(string Pass, GpuResourceUse Use)>>(StringComparer.Ordinal);
        foreach (var pass in passes.Values)
        {
            foreach (var before in pass.After)
            {
                if (!Id(before)) throw Error("ResourceDependency", "Invalid prerequisite identity.");
                Edge(before, pass.Id);
            }
            var bindings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var use in pass.Uses)
            {
                if (use is null || !Id(use.Binding) || !bindings.Add(use.Binding) || !Id(use.Resource) ||
                    !resources.TryGetValue(use.Resource, out var resource) || !Enum.IsDefined(use.Access) ||
                    !Enum.IsDefined(use.Frame) || use.Version > MaximumPasses)
                    throw Error("ResourceBinding", $"Invalid resource binding in pass '{pass.Id}'.");
                ValidateAccess(resource, use);
                if (capabilities is not null && !capabilities.Accesses.Contains(use.Access))
                    throw Error("ResourceCapability", $"Unsupported access '{use.Access}' in pass '{pass.Id}'.");
                if (capabilities is not null && resource.ExternalBinding is { } binding &&
                    !capabilities.ExternalBindings[binding].Accesses.Contains(use.Access))
                    throw Error("ResourceCapability", $"External binding '{binding}' does not permit '{use.Access}'.");
                if (!uses.TryGetValue(use.Resource, out var list)) uses.Add(use.Resource, list = []);
                list.Add((pass.Id, use));
                if (Writes(use.Access) && !writers.TryAdd((use.Resource, use.Version), pass.Id))
                    throw Error("ResourceWriter", $"Multiple producers for '{use.Resource}' version {use.Version}.");
            }
            // Versions identify contents, not separate allocations. Reading and writing the same allocation
            // in one dispatch is invalid; ping-pong must use distinct resources. History has a separate allocation.
            foreach (var group in pass.Uses.GroupBy(u => (u.Resource, u.Frame)))
            {
                if (group.Any(u => Writes(u.Access)) && group.Count() != 1)
                    throw Error("ResourceAlias", $"Pass '{pass.Id}' aliases a writable resource '{group.Key.Resource}'.");
                if (group.Select(u => u.Access).Distinct().Count() != 1)
                    throw Error("ResourceAccess", $"Pass '{pass.Id}' requires incompatible simultaneous access modes for '{group.Key.Resource}'.");
            }
        }
        foreach (var resource in resources.Values)
        {
            var versions = writers.Keys.Where(k => k.Resource == resource.Id).Select(k => k.Version).Order().ToArray();
            for (var i = 0; i < versions.Length; i++)
            {
                if (versions[i] != (uint)i + 1) throw Error("ResourceVersion", $"Write versions for '{resource.Id}' must start at 1 without gaps.");
                if (i > 0) Edge(writers[(resource.Id, versions[i - 1])], writers[(resource.Id, versions[i])]);
            }
            if (!uses.TryGetValue(resource.Id, out var resourceUses)) continue;
            foreach (var (pass, use) in resourceUses.Where(u => !Writes(u.Use.Access)))
            {
                if (use.Frame == GpuResourceFrame.Previous)
                {
                    if (use.Version != 0 || resource.Lifetime != GpuResourceLifetime.Persistent ||
                        resource.Initialization != GpuResourceInitialization.Zero)
                        throw Error("ResourceHistory", "Previous-frame reads require initialized persistent storage and version 0.");
                    continue;
                }
                if (use.Version == 0)
                {
                    if (resource.Initialization == GpuResourceInitialization.Undefined)
                        throw Error("ResourceUninitialized", $"Pass '{pass}' reads uninitialized '{resource.Id}'.");
                }
                else if (writers.TryGetValue((resource.Id, use.Version), out var writer)) Edge(writer, pass);
                else throw Error("ResourceProducer", $"No producer for '{resource.Id}' version {use.Version}.");
                // Preserve old contents until every reader has finished, even if the next write was authored first.
                if (writers.TryGetValue((resource.Id, use.Version + 1), out var next)) Edge(pass, next);
            }
        }
        var ready = new SortedSet<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.Ordinal);
        var order = new List<CompiledGpuResourcePass>();
        while (ready.Count != 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            var pass = passes[id];
            order.Add(new(id, pass.Operation, Array.AsReadOnly(pass.Uses.ToArray())));
            foreach (var after in edges[id]) if (--indegree[after] == 0) ready.Add(after);
        }
        if (order.Count != passes.Count) throw Error("ResourceCycle", "Resource dependencies contain a same-frame cycle. Use explicit previous-frame storage for feedback.");
        var indices = order.Select((p, i) => (p.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        var allocations = new List<GpuResourceAllocation>();
        ulong ownedBytes = 0;
        foreach (var resource in resources.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var resourceUses = uses.GetValueOrDefault(resource.Id) ?? [];
            var history = resourceUses.Any(u => u.Use.Frame == GpuResourceFrame.Previous);
            var size = checked(bytes[resource.Id] * (history ? 2UL : 1UL));
            if (resource.Lifetime != GpuResourceLifetime.External) ownedBytes = checked(ownedBytes + size);
            allocations.Add(new(resource, size,
                resourceUses.Count == 0 ? -1 : resourceUses.Min(u => indices[u.Pass]),
                resourceUses.Count == 0 ? -1 : resourceUses.Max(u => indices[u.Pass]), history));
        }
        if (ownedBytes > (capabilities?.MaximumOwnedBytes ?? 256UL * 1024 * 1024))
            throw Error("ResourceBudget", $"Resource plan requires {ownedBytes} owned bytes.");
        var transitions = new List<GpuResourceTransition>();
        var lastUses = new Dictionary<(string, GpuResourceFrame), (string Pass, GpuResourceAccess Access)>();
        foreach (var pass in order)
        foreach (var use in pass.Uses)
        {
            var key = (use.Resource, use.Frame);
            if (lastUses.TryGetValue(key, out var last) && last.Pass != pass.Id &&
                (Writes(last.Access) || Writes(use.Access) || last.Access != use.Access))
                transitions.Add(new(use.Resource, last.Pass, pass.Id, last.Access, use.Access, use.Frame));
            lastUses[key] = (pass.Id, use.Access);
        }
        return new(order.AsReadOnly(), allocations.AsReadOnly(), transitions.AsReadOnly(), ownedBytes);
    }

    private static ulong ValidateResource(GpuFlowResource r)
    {
        if (!Enum.IsDefined(r.Kind) || !Enum.IsDefined(r.Format) || !Enum.IsDefined(r.Lifetime) ||
            !Enum.IsDefined(r.Initialization) || r.Width == 0 || r.Height == 0 || r.Depth == 0 ||
            r.Width > 16777216 || r.Height > 16384 || r.Depth > 2048 ||
            r.Kind != GpuFlowResourceKind.Texture3D && r.Depth != 1 ||
            r.Kind is GpuFlowResourceKind.StorageBuffer or GpuFlowResourceKind.ParticleBuffer && r.Height != 1 ||
            (r.Kind == GpuFlowResourceKind.ParticleBuffer) != (r.Format == GpuResourceFormat.Particle80) ||
            r.Kind is GpuFlowResourceKind.Texture2D or GpuFlowResourceKind.Texture3D or GpuFlowResourceKind.RenderTarget && r.Width > 16384 ||
            (r.Lifetime == GpuResourceLifetime.External) != (r.Initialization == GpuResourceInitialization.External) ||
            (r.Lifetime == GpuResourceLifetime.External ? !Id(r.ExternalBinding) : r.ExternalBinding is not null))
            throw Error("ResourceDeclaration", $"Invalid dimensions, format, lifetime or external binding for '{r.Id}'.");
        return checked((ulong)r.Width * r.Height * r.Depth * (r.Format switch
        {
            GpuResourceFormat.Float4 or GpuResourceFormat.Rgba32Float => 16UL, GpuResourceFormat.Rgba16Float => 8,
            GpuResourceFormat.R32Float => 4, GpuResourceFormat.Particle80 => 80,
            _ => throw new InvalidOperationException()
        }));
    }

    private static void ValidateAccess(GpuFlowResource r, GpuResourceUse use)
    {
        if (Writes(use.Access) && (use.Version == 0 || use.Frame != GpuResourceFrame.Current) ||
            use.Access == GpuResourceAccess.Sample && r.Kind is not (GpuFlowResourceKind.Texture2D or GpuFlowResourceKind.Texture3D or GpuFlowResourceKind.RenderTarget) ||
            use.Access == GpuResourceAccess.RenderTarget && r.Kind != GpuFlowResourceKind.RenderTarget ||
            use.Access == GpuResourceAccess.VertexRead && r.Kind is not (GpuFlowResourceKind.StorageBuffer or GpuFlowResourceKind.ParticleBuffer))
            throw Error("ResourceAccess", $"Invalid access to '{r.Id}' at binding '{use.Binding}'.");
    }

    private static bool Writes(GpuResourceAccess access) => access is GpuResourceAccess.StorageWrite or GpuResourceAccess.RenderTarget;
    private static bool Id(string? id) => id is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl);
    private static GpuValidationException Error(string code, string message) => new([new(code, message)]);
}
