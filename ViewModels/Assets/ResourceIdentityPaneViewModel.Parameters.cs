using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services;

namespace LiberaFx.ViewModels;

public sealed partial class ResourceIdentityPaneViewModel
{
    private async Task ReadMaterialSchemaAsync()
    {
        if (SelectedPeer is not { } peer || ProjectState?.Project is not { } project ||
            SelectedResource is not { Kind: "material" } selected ||
            CurrentDocument is not { } document ||
            document.ResourceBindings.SingleOrDefault(binding =>
                binding.Project == project && binding.ResourceId == selected.Id) is not { } materialBinding)
            return;
        try
        {
            var schema = await _main.PreviewAssets.MaterialSchemaAsync(peer.ClientId, project, materialBinding.Binding);
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId ||
                SelectedResource?.Id != selected.Id || ProjectState?.Project != project ||
                !ReferenceEquals(document, CurrentDocument))
            {
                Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
                return;
            }
            EngineParameters.Clear();
            foreach (var item in schema) EngineParameters.Add(item);
            SelectedEngineParameter = EngineParameters.FirstOrDefault();
            Status = LocalizationService.Shared.Get("Resources.IdentitySchemaLoaded", schema.Count);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException or JsonException)
        { Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message); }
    }

    private async Task RegisterParameterAsync()
    {
        if (_observedGraph is not { } workspace || SelectedPeer is not { } peer ||
            ProjectState?.Project is not { } project || SelectedTargetNode is not { } target ||
            SelectedLocation is not { Id: var locationId } || !locationId.StartsWith("material:", StringComparison.Ordinal) ||
            SelectedResource is not { Kind: "material" } selected ||
            SelectedGraphParameter is not { } graphParameter || SelectedEngineParameter is not { } engineParameter)
            return;
        var initialGraph = workspace.CreateSnapshot();
        if (!FindNode(initialGraph, target.Id, out var capturedScope, out _)) return;
        var document = workspace.ResourceIdentities;
        if (document is null) return;
        var material = document.Resources.SingleOrDefault(item => item.Id == selected.Id);
        var materialBinding = document.ResourceBindings.SingleOrDefault(item =>
            item.Project == project && item.ResourceId == selected.Id);
        if (material is null || materialBinding is null) return;
        var capturedName = ParameterName.Trim();
        var capturedKind = ParameterKind;
        var capturedBindingName = ParameterBindingName.Trim();
        var capturedBindingFormat = BindingFormat;
        var capturedAssociationText = ParameterAssociation;
        var capturedIndexText = ParameterIndex;
        var capturedDefaultJson = ParameterDefaultJson;
        var capturedMinimumJson = ParameterMinimumJson;
        var capturedMaximumJson = ParameterMaximumJson;
        if (!int.TryParse(capturedAssociationText, out var association) || association is < 0 or > 2 ||
            !int.TryParse(capturedIndexText, out var index) || index is < -1 or > 65535 ||
            string.IsNullOrWhiteSpace(capturedName) || string.IsNullOrWhiteSpace(capturedBindingName))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityParameterInvalid");
            return;
        }
        JsonElement defaultValue;
        JsonElement? minimum = null;
        JsonElement? maximum = null;
        try
        {
            using var defaultDocument = JsonDocument.Parse(capturedDefaultJson);
            defaultValue = defaultDocument.RootElement.Clone();
            if (!string.IsNullOrWhiteSpace(capturedMinimumJson))
            {
                using var minimumDocument = JsonDocument.Parse(capturedMinimumJson);
                minimum = minimumDocument.RootElement.Clone();
            }
            if (!string.IsNullOrWhiteSpace(capturedMaximumJson))
            {
                using var maximumDocument = JsonDocument.Parse(capturedMaximumJson);
                maximum = maximumDocument.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityParameterInvalid");
            return;
        }
        var parameterFormat = capturedBindingFormat switch
        {
            "unreal.object-path" => "unreal.material-parameter.v1",
            "godot.res-path.v1" => "godot.shader-parameter.v1",
            _ => "unity.shader-property.v1"
        };
        var parameterBinding = new OpaqueBinding(parameterFormat,
            JsonSerializer.Serialize(new { name = capturedBindingName, association, index }));
        ResourceProbeResult probe;
        try
        {
            probe = await _main.PreviewAssets.ProbeParameterAsync(peer.ClientId, project,
                materialBinding.Binding, parameterBinding);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException or JsonException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId || ProjectState?.Project != project) return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
            return;
        }
        if (probe.Status != ResourceResolutionStatus.Resolved || probe.ActualKind != capturedKind)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult",
                probe.Status, probe.ActualKind ?? "", probe.Detail ?? "");
            return;
        }
        if (_disposed || !ReferenceEquals(workspace, _main.ActiveGraphProvider.Graph) ||
            SelectedTargetNode?.Id != target.Id || SelectedLocation?.Id != locationId ||
            SelectedResource?.Id != selected.Id || SelectedGraphParameter?.Id != graphParameter.Id ||
            SelectedEngineParameter != engineParameter || ParameterKind != capturedKind ||
            ParameterName.Trim() != capturedName || ParameterBindingName.Trim() != capturedBindingName ||
            BindingFormat != capturedBindingFormat || ParameterAssociation != capturedAssociationText ||
            ParameterIndex != capturedIndexText || ParameterDefaultJson != capturedDefaultJson ||
            ParameterMinimumJson != capturedMinimumJson || ParameterMaximumJson != capturedMaximumJson ||
            ProjectState?.Project != project || !ReferenceEquals(document, workspace.ResourceIdentities))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var root = workspace.CreateSnapshot();
        if (!FindNode(root, target.Id, out var scope, out var node)) return;
        if (!ResourceIdentityScope.Equal(capturedScope, scope))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        if (!document.Uses.Any(use => use.NodeId == target.Id && use.Location == locationId &&
                use.ResourceId == selected.Id && ResourceIdentityScope.Equal(use.InstancePath, scope)))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityMaterialRequired");
            return;
        }
        var pin = RenderNodeSchema.Read(GraphNodeDefinition.All.Single(item => item.Kind == node.Kind), node.Parameters)
            .FirstOrDefault(item => item.Id == graphParameter.Id && item.Slot.ToString() == locationId["material:".Length..]);
        if (pin is null || pin.Kind != engineParameter.Kind || capturedKind != engineParameter.Kind)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityParameterPinMissing", graphParameter.Name);
            return;
        }
        var parameterId = Guid.NewGuid();
        var currentDocument = workspace.ResourceIdentities ?? EmptyDocument();
        var existingUse = currentDocument.ParameterUses.Any(use => use.NodeId == target.Id &&
            use.PinId == graphParameter.Id && ResourceIdentityScope.Equal(use.InstancePath, scope));
        if (existingUse && !ReplaceExistingUse)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityReplaceRequired");
            return;
        }
        workspace.EditResourceIdentityDocument(current =>
        {
            current ??= EmptyDocument();
            var resources = current.Resources.Select(resource => resource.Id == selected.Id
                ? resource with
                {
                    Parameters = (resource.Parameters ?? []).Append(new MaterialParameterDefinition(
                        parameterId, capturedKind, capturedName, defaultValue, minimum, maximum)).ToArray()
                } : resource).ToArray();
            return current with
            {
                Resources = resources,
                ParameterBindings = current.ParameterBindings.Append(new ProjectParameterBinding(
                    project, selected.Id, parameterId, capturedKind, parameterBinding)).ToArray(),
                ParameterUses = current.ParameterUses.Where(use => use.NodeId != target.Id || use.PinId != graphParameter.Id ||
                    !ResourceIdentityScope.Equal(use.InstancePath, scope)).Append(
                    new ParameterUse(target.Id, graphParameter.Id, selected.Id, parameterId, scope)).ToArray()
            };
        }, LocalizationService.Shared.Get("Resources.IdentityHistoryParameter"));
        ReplaceExistingUse = false;
        Status = LocalizationService.Shared.Get("Resources.IdentityParameterRegistered", capturedName);
    }

    private async Task DeliverUnityBindingsAsync()
    {
        if (SelectedPeer is not { } peer || ProjectState?.Project is not { Engine: "Unity" } project ||
            CurrentDocument is not { } document) return;
        var resources = document.Resources.Select(resource =>
        {
            var binding = document.ResourceBindings.SingleOrDefault(item =>
                item.Project == project && item.ResourceId == resource.Id);
            if (binding is null) return null;
            var parameters = (resource.Parameters ?? []).Select(parameter =>
            {
                var parameterBinding = document.ParameterBindings.SingleOrDefault(item =>
                    item.Project == project && item.ResourceId == resource.Id && item.ParameterId == parameter.Id);
                return parameterBinding is null ? null : new UnityRuntimeParameterBindingDelivery(
                    parameter.Id, parameter.Name, parameter.Kind, parameterBinding.Binding);
            }).Where(item => item is not null).Cast<UnityRuntimeParameterBindingDelivery>().ToArray();
            return new UnityRuntimeBindingDelivery(resource.Id, resource.Kind, binding.Binding, parameters);
        }).Where(item => item is not null).Cast<UnityRuntimeBindingDelivery>().ToArray();
        try
        {
            var path = await _main.PreviewAssets.DeliverUnityBindingsAsync(peer.ClientId, project, resources);
            Status = LocalizationService.Shared.Get("Resources.IdentityUnityDelivered", path ?? "Assets/LiberaFx/LiberaFxBindings.asset");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException)
        { Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message); }
    }

    private void RefreshBindingFormat()
    {
        BindingFormat = Engine switch
        {
            "Unity" => "unity.asset-guid-local-id.v1",
            "Unreal" => "unreal.object-path",
            "Godot" => "godot.res-path.v1",
            _ => ""
        };
        if (BindingFormat == "unreal.object-path" && string.IsNullOrEmpty(BindingValue)) BindingValue = "/Game/";
        if (BindingFormat == "godot.res-path.v1" && string.IsNullOrEmpty(BindingValue)) BindingValue = "res://";
    }

    private static ResourceIdentityDocument EmptyDocument() => new(1, [], [], [], [], []);

    private static string StringParameter(GraphNodeSnapshot node, string id) =>
        node.Parameters?.FirstOrDefault(parameter => parameter.Id == id)?.Value.GetString() ?? "";

    private static string KindForLocation(string location, GraphNodeKind? nodeKind)
    {
        if (location.StartsWith("material:", StringComparison.Ordinal)) return "material";
        if (location.StartsWith("texture:", StringComparison.Ordinal)) return "texture";
        return nodeKind?.Value is "SpriteDraw" or "SpriteAnimation" or "SpriteComposition" ? "texture" : "model";
    }

    private static IReadOnlyList<Guid> GetScope(GraphSnapshot graph, Guid nodeId) =>
        FindNode(graph, nodeId, out var scope, out _) ? scope : Array.Empty<Guid>();

    private static bool FindNode(GraphSnapshot graph, Guid nodeId, out IReadOnlyList<Guid> scope,
        out GraphNodeSnapshot node)
    {
        var found = false;
        IReadOnlyList<Guid> foundScope = Array.Empty<Guid>();
        GraphNodeSnapshot? foundNode = null;
        void Visit(GraphSnapshot current, IReadOnlyList<Guid> path)
        {
            foreach (var candidate in current.Nodes)
            {
                if (candidate.Id == nodeId)
                {
                    found = true;
                    foundScope = path.ToArray();
                    foundNode = candidate;
                    return;
                }
                if (candidate.ChildGraph is { } child)
                {
                    Visit(child, ResourceIdentityScope.Child(path, candidate.Id));
                    if (found) return;
                }
            }
        }
        Visit(graph, Array.Empty<Guid>());
        scope = foundScope;
        node = foundNode!;
        return found;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= OnMainChanged;
        _main.ActiveGraphProvider.PropertyChanged -= OnActiveGraphChanged;
        if (_observedGraph is not null)
        {
            _observedGraph.History.StateChanged -= OnGraphStateChanged;
            _observedGraph.PropertyChanged -= OnGraphPropertyChanged;
        }
    }
}
