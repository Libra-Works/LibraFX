using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;

namespace LiberaFx.ViewModels.Graph;

public sealed partial class GraphWorkspaceViewModel
{
    /// <summary>
    /// The project-scoped identity registry attached to the full document snapshot.
    /// A nested graph workspace sees the same document-level registry.
    /// </summary>
    public ResourceIdentityDocument? ResourceIdentities => _document._rootSnapshot.ResourceIdentities;

    /// <summary>
    /// Replaces the document's identity registry as one history state. Callers should
    /// construct an additive/migration result first; this method never infers bindings
    /// from legacy node paths or names.
    /// </summary>
    public void SetResourceIdentityDocument(ResourceIdentityDocument? identities,
        string description = "Update resource identities")
    {
        var root = _document;
        if (identities is not null) ResourceIdentityStore.Validate(identities);
        var before = root.CreateSnapshot();
        var after = before with { ResourceIdentities = identities };
        if (GraphSnapshot.ContentEquals(before, after)) return;
        root._history.Execute(new ReplaceGraphSnapshotCommand(root, before, after, description));
    }

    /// <summary>
    /// Applies a deliberate identity-document edit atomically to the current registry.
    /// The mutation is validated before it enters undo history.
    /// </summary>
    public void EditResourceIdentityDocument(Func<ResourceIdentityDocument?, ResourceIdentityDocument> edit,
        string description = "Edit resource identities")
    {
        ArgumentNullException.ThrowIfNull(edit);
        SetResourceIdentityDocument(edit(ResourceIdentities), description);
    }

    private ResourceIdentityDocument? CopySelectedResourceIdentities(IReadOnlySet<Guid> selected)
    {
        if (ResourceIdentities is not { } document)
        {
            return null;
        }

        var uses = document.Uses.Where(use => IsSelectedResourceUse(use.NodeId, use.InstancePath, selected))
            .Select(use => use with { InstancePath = (use.InstancePath ?? []).Skip(_graphPath.Count).ToArray() }).ToArray();
        var parameters = document.ParameterUses.Where(use => IsSelectedResourceUse(use.NodeId, use.InstancePath, selected))
            .Select(use => use with { InstancePath = (use.InstancePath ?? []).Skip(_graphPath.Count).ToArray() }).ToArray();
        var ids = uses.Select(use => use.ResourceId).Concat(parameters.Select(use => use.ResourceId)).ToHashSet();
        return ids.Count == 0 ? null : document with
        {
            Resources = document.Resources.Where(resource => ids.Contains(resource.Id)).ToArray(),
            ResourceBindings = document.ResourceBindings.Where(binding => ids.Contains(binding.ResourceId)).ToArray(),
            ParameterBindings = document.ParameterBindings.Where(binding => ids.Contains(binding.ResourceId)).ToArray(),
            Uses = uses,
            ParameterUses = parameters
        };
    }

    private bool IsSelectedResourceUse(Guid nodeId, IReadOnlyList<Guid>? scope, IReadOnlySet<Guid> selected)
    {
        var path = scope ?? [];
        return path.Count >= _graphPath.Count && path.Take(_graphPath.Count).SequenceEqual(_graphPath) &&
            selected.Contains(path.Count == _graphPath.Count ? nodeId : path[_graphPath.Count]);
    }

    private ResourceIdentityDocument? RemoveSelectedResourceUses(IReadOnlySet<Guid> selected)
    {
        if (ResourceIdentities is not { } document)
        {
            return null;
        }

        var uses = document.Uses.Where(use => !IsSelectedResourceUse(use.NodeId, use.InstancePath, selected)).ToArray();
        var parameters = document.ParameterUses
            .Where(use => !IsSelectedResourceUse(use.NodeId, use.InstancePath, selected)).ToArray();
        return uses.Length == document.Uses.Count && parameters.Length == document.ParameterUses.Count
            ? null : document with { Uses = uses, ParameterUses = parameters };
    }

    private ResourceIdentityDocument? RemapPastedResourceIdentities(GraphSnapshot snapshot,
        IReadOnlyDictionary<Guid, GraphNodeViewModel> pasted)
    {
        if (snapshot.ResourceIdentities is not { } source)
        {
            return null;
        }
        ResourceIdentityStore.Validate(source);

        var remap = new Dictionary<(string Scope, Guid Node), (IReadOnlyList<Guid> Scope, Guid Node)>();
        void Map(GraphNodeSnapshot oldNode, Guid newId, GraphSnapshot? newChild,
            IReadOnlyList<Guid> oldScope, IReadOnlyList<Guid> newScope)
        {
            remap.Add((ResourceIdentityScope.Key(oldScope), oldNode.Id), (newScope, newId));
            if (oldNode.ChildGraph is { } oldChild && newChild is not null)
            {
                var before = ResourceIdentityScope.Child(oldScope, oldNode.Id);
                var after = ResourceIdentityScope.Child(newScope, newId);
                foreach (var (oldInner, newInner) in oldChild.Nodes.Zip(newChild.Nodes))
                {
                    Map(oldInner, newInner.Id, newInner.ChildGraph, before, after);
                }
            }
        }

        foreach (var node in snapshot.Nodes)
        {
            var replacement = pasted[node.Id];
            Map(node, replacement.Id, replacement.ChildGraph, [], _graphPath.ToArray());
        }
        (IReadOnlyList<Guid> Scope, Guid Node) Resolve(IReadOnlyList<Guid>? scope, Guid node) =>
            remap.TryGetValue((ResourceIdentityScope.Key(scope), node), out var mapped)
                ? mapped : throw new InvalidDataException("A clipboard resource use is outside the pasted graph.");

        var uses = source.Uses.Select(use =>
        {
            var (scope, node) = Resolve(use.InstancePath, use.NodeId);
            return use with { NodeId = node, InstancePath = ResourceIdentityScope.Normalize(scope) };
        }).ToArray();
        var parameters = source.ParameterUses.Select(use =>
        {
            var (scope, node) = Resolve(use.InstancePath, use.NodeId);
            return use with { NodeId = node, InstancePath = ResourceIdentityScope.Normalize(scope) };
        }).ToArray();
        var current = ResourceIdentities ?? new ResourceIdentityDocument(1, [], [], [], [], []);
        var result = current with
        {
            Resources = Merge(current.Resources, source.Resources, resource => resource.Id),
            ResourceBindings = Merge(current.ResourceBindings, source.ResourceBindings,
                binding => (binding.Project, binding.ResourceId)),
            ParameterBindings = Merge(current.ParameterBindings, source.ParameterBindings,
                binding => (binding.Project, binding.ResourceId, binding.ParameterId)),
            Uses = current.Uses.Concat(uses).ToArray(),
            ParameterUses = current.ParameterUses.Concat(parameters).ToArray()
        };
        ResourceIdentityStore.Validate(result);
        return result;

        static IReadOnlyList<T> Merge<T, TKey>(IReadOnlyList<T> current, IReadOnlyList<T> incoming, Func<T, TKey> key)
            where TKey : notnull
        {
            var existing = current.ToDictionary(key);
            var added = new List<T>();
            foreach (var item in incoming)
            {
                if (!existing.TryGetValue(key(item), out var old))
                {
                    added.Add(item);
                }
                else if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(old), JsonSerializer.SerializeToElement(item)))
                {
                    throw new InvalidDataException("The pasted resource identity conflicts with the destination document.");
                }
            }
            return current.Concat(added).ToArray();
        }
    }
}
