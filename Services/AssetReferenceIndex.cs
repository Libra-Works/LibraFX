using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LibraGpu.Core;

namespace LiberaFx.Services;

public enum ReferenceResourceKind { Project, Group, Engine, LocalFile, Builtin, Unassigned, Unknown }

public sealed record ReferenceResource(string Key, ReferenceResourceKind Kind, string Name, string AssetClass,
    string Binding = "", string Scope = "", Guid? GroupId = null);

public sealed record ReferenceSource(ReferenceResource Resource, GraphSnapshot Graph,
    string? DocumentId = null, string? LocalPath = null);

public sealed record AssetReferenceUse(ReferenceSource Source, ReferenceResource Target, Guid NodeId,
    IReadOnlyList<Guid> ParentPath, string Location, string Parameter, bool Inferred = false, long? Revision = null,
    IReadOnlyList<string>? GroupAncestors = null)
{
    public string Identity => $"{Source.Resource.Key}|{string.Join("/", ParentPath)}|{NodeId}|{Parameter}|{Target.Key}";
}

public sealed class AssetReferenceIndex
{
    public const int MaximumUses = 20000;
    public IReadOnlyList<ReferenceResource> Resources { get; }
    public IReadOnlyList<AssetReferenceUse> Uses { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<ReferenceSource> Sources { get; }
    public bool Truncated { get; }

    public AssetReferenceIndex(IReadOnlyList<ReferenceSource> sources, IEnumerable<ReferenceResource>? files = null)
    {
        var resources = (files ?? []).ToDictionary(resource => resource.Key);
        var uses = new List<AssetReferenceUse>();
        var errors = new List<string>();
        var visited = new List<ReferenceSource>();
        var nodesRead = 0;
        var truncated = false;
        foreach (var source in sources)
        {
            resources[source.Resource.Key] = source.Resource;
            try
            {
                var graph = GroupExposedItems.Materialize(source.Graph);
                visited.Add(source);
                Visit(graph, [], [], source.Resource.Name, source, 0);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or JsonException)
            { errors.Add($"{source.Resource.Name}: {ex.Message}"); }
            if (truncated) break;
        }
        Resources = resources.Values.ToArray();
        Uses = uses;
        Errors = errors;
        Sources = visited;
        Truncated = truncated;

        void Visit(GraphSnapshot graph, Guid[] parents, string[] groupAncestors, string location, ReferenceSource source, int depth)
        {
            if (depth > 32) { truncated = true; return; }
            foreach (var node in graph.Nodes)
            {
                if (++nodesRead > 50000 || uses.Count >= MaximumUses) { truncated = true; return; }
                var name = location + " / " + node.Name;
                void Add(ReferenceResource target, string parameter, bool inferred = false, long? revision = null)
                {
                    if (uses.Count >= MaximumUses) { truncated = true; return; }
                    if (!resources.TryGetValue(target.Key, out var existing) || existing.AssetClass == "unknown")
                        resources[target.Key] = target;
                    uses.Add(new(source, target, node.Id, parents, name, parameter, inferred, revision, groupAncestors));
                }
                void Binding(string value, string assetClass, string parameter, bool inferred = false)
                {
                    if (string.IsNullOrEmpty(value)) return;
                    Add(FromBinding(source.Resource.Key, value, assetClass), parameter, inferred);
                }
                if (node.GroupAsset is { } group)
                    Add(Group(group.Id, node.Name), "Group", revision: group.Revision);
                if (GraphNodeDefinition.TryFind(node.Kind.Value, out var definition))
                {
                    var handled = new HashSet<string>();
                    if (RenderNodeSchema.IsRenderNode(definition!))
                    {
                        var effective = RenderNodeSchema.Effective(definition!, node.Parameters);
                        string Value(string id) => (node.Parameters?.FirstOrDefault(parameter => parameter.Id == id)?.Value
                            ?? effective.Parameters.First(parameter => parameter.Id == id).DefaultValue).GetString() ?? "";
                        var assetClass = definition!.TypeName == "ModelDraw" ? "model" : "texture";
                        var path = Value("assetPath");
                        if (path.Length == 0)
                            Add(new("unassigned:" + source.Resource.Key + ":" + node.Id, ReferenceResourceKind.Unassigned,
                                node.Name, assetClass), "assetPath");
                        else Binding(path, assetClass, "assetPath");
                        var schema = RenderNodeSchema.Read(definition, node.Parameters);
                        foreach (var material in schema.Where(item => item.MaterialPath.Length > 0)
                                     .DistinctBy(item => (item.Slot, item.MaterialPath)))
                            Binding(material.MaterialPath, "material", $"material[{material.Slot}]");
                        foreach (var parameter in schema.Where(item => item.Kind == "texture"))
                            Binding(Value(parameter.Id), "texture", parameter.Id);
                        handled.UnionWith(schema.Select(item => item.Id));
                        handled.UnionWith(["assetPath", "materialSchema"]);
                    }
                    if (GpuParticleEditorService.IsGpu(node.Kind))
                    {
                        var state = GpuParticleEditorService.ReadState(node);
                        foreach (var resource in state.Program.Resources.Where(resource => resource.Kind == ResourceKind.Texture2D))
                        {
                            if (state.ResourceAssetPaths.TryGetValue(resource.Id, out var path) && path.Length > 0)
                                Binding(path, "texture", "GPU/" + resource.Id);
                            else
                                Add(new($"unassigned:{source.Resource.Key}:{node.Id}:{resource.Id}",
                                    ReferenceResourceKind.Unassigned, resource.Id, "texture"), "GPU/" + resource.Id);
                        }
                        foreach (var renderer in state.Program.Flow.Where(flow => flow.Kind == FlowKind.RenderBillboard))
                            Binding(state.RendererSettings.GetValueOrDefault(renderer.Id)?.Material ?? "builtin:unlit-alpha",
                                "material", "GPU/" + renderer.Id + "/material");
                        handled.Add(GpuParticleEditorService.ParameterId);
                    }
                    if (node.Kind != GraphNodeKind.Comment)
                    {
                        foreach (var parameter in definition!.Parameters.Where(parameter =>
                                     parameter.Type == "string" && !handled.Contains(parameter.Id) && LooksLikeReference(parameter.Id)))
                        {
                            var value = node.Parameters?.FirstOrDefault(item => item.Id == parameter.Id)?.Value
                                ?? parameter.DefaultValue;
                            if (value.ValueKind == JsonValueKind.String)
                                Binding(value.GetString() ?? "", "unknown", parameter.Id, inferred: true);
                        }
                    }
                }
                if (node.ChildGraph is { } child)
                    Visit(child, [.. parents, node.Id],
                        node.GroupAsset is { } reference ? [.. groupAncestors, Group(reference.Id, node.Name).Key] : groupAncestors,
                        name, source, depth + 1);
                if (truncated) return;
            }
        }
    }

    public IReadOnlyList<AssetReferenceUse> Incoming(ReferenceResource resource) =>
        Uses.Where(use => use.Target.Key == resource.Key).ToArray();

    public IReadOnlyList<AssetReferenceUse> Candidates(ReferenceResource resource) =>
        resource.Kind is ReferenceResourceKind.Engine or ReferenceResourceKind.Unknown
            ? Uses.Where(use => use.Target.Key != resource.Key && use.Target.Kind == resource.Kind &&
                                use.Target.Binding == resource.Binding).Select(use => use with { Inferred = true }).ToArray()
            : [];

    public IReadOnlyList<AssetReferenceUse> Outgoing(ReferenceResource resource) =>
        Uses.Where(use => use.Source.Resource.Key == resource.Key ||
                          use.GroupAncestors?.Contains(resource.Key) == true).ToArray();

    public static ReferenceResource Group(Guid id, string name) =>
        new("group:" + id.ToString("D"), ReferenceResourceKind.Group, name, "group", GroupId: id);

    public static ReferenceResource File(string path, bool project = false) =>
        new(FileKey(path), project ? ReferenceResourceKind.Project : ReferenceResourceKind.LocalFile,
            Path.GetFileName(path), project ? "project" : "image", path);

    public static string FileKey(string path)
    {
        var full = Path.GetFullPath(path);
        return "file:" + (OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full);
    }

    public static ReferenceResource FromBinding(string scope, string binding, string assetClass)
    {
        if (binding is "builtin:white" or "builtin:unlit-alpha" or "builtin:unlit-additive" or "builtin:unlit-color")
            return new(binding, ReferenceResourceKind.Builtin, binding, assetClass, binding);
        try
        {
            EngineAssetPath.Validate(binding);
            return new("legacy:" + Hash(scope + "\0Unreal\0" + binding), ReferenceResourceKind.Engine,
                binding.Split('/').Last().Split('.')[0], assetClass, binding, scope);
        }
        catch (ArgumentException) { }
        if (Path.IsPathFullyQualified(binding))
            return new(FileKey(binding), ReferenceResourceKind.LocalFile, Path.GetFileName(binding), assetClass, binding);
        return new("unknown:" + Hash(scope + "\0" + binding), ReferenceResourceKind.Unknown,
            binding.Length > 100 ? binding[..100] : binding, assetClass, binding, scope);
    }

    private static bool LooksLikeReference(string id) =>
        new[] { "asset", "texture", "material", "model", "flowmap" }.Any(token => id.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
