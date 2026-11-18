using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;

namespace LiberaFx.ViewModels;

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
}
