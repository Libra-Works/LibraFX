using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using LiberaFx.Models.Graph;
using LiberaFx.Models.Resources;
using LiberaFx.Services.Compilation;
using LiberaFx.Services.Settings;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.ViewModels.Assets;

public sealed partial class ResourceIdentityPaneViewModel
{
    private async Task RefreshProjectAsync()
    {
        if (_disposed || SelectedPeer is not { } peer) return;
        try
        {
            var state = await _main.PreviewAssets.EngineProjectAsync(peer.ClientId);
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            ProjectState = state;
            Status = state.Project is null
                ? LocalizationService.Shared.Get("Resources.IdentityNoProject", state.Status, state.Detail ?? "")
                : LocalizationService.Shared.Get("Resources.IdentityProjectReady", state.Project.Engine,
                    state.Project.ProjectId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            ProjectState = null;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
        }
    }

    private async Task ProjectOperationAsync(string operation)
    {
        if (_disposed || SelectedPeer is not { } peer) return;
        if (operation == "fork" && !ConfirmFork) return;
        try
        {
            var state = await _main.PreviewAssets.EngineProjectAsync(peer.ClientId, operation);
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            ProjectState = state;
            ConfirmFork = false;
            Status = state.Detail ?? LocalizationService.Shared.Get("Resources.IdentityProjectState",
                state.Status, state.ProjectState);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId) return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
        }
    }

    private async Task ProbeAsync()
    {
        if (SelectedPeer is not { } peer || ProjectState?.Project is not { } project) return;
        var bindingFormat = BindingFormat;
        var bindingValue = BindingValue.Trim();
        try
        {
            var result = await _main.PreviewAssets.ProbeResourceAsync(peer.ClientId, project,
                new(bindingFormat, bindingValue));
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId || ProjectState?.Project != project ||
                BindingFormat != bindingFormat || BindingValue.Trim() != bindingValue)
                return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult",
                result.Status, result.ActualKind ?? "", result.Detail ?? "");
            if (result.Status == ResourceResolutionStatus.Resolved && result.ActualKind != ResourceKind)
                Status = LocalizationService.Shared.Get("Resources.IdentityKindMismatch", ResourceKind,
                    result.ActualKind ?? "");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or
                                           TimeoutException or IOException or ArgumentException or JsonException)
        {
            if (_disposed || SelectedPeer?.ClientId != peer.ClientId || ProjectState?.Project != project) return;
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeFailed", exception.Message);
        }
    }

    private async Task RegisterAndAssignAsync()
    {
        if (!CanRegister || SelectedPeer is not { } peer || ProjectState?.Project is not { } project) return;
        var target = SelectedTargetNode;
        var location = SelectedLocation;
        var workspace = _observedGraph;
        var capturedKind = ResourceKind;
        var capturedName = ResourceName.Trim();
        var capturedBindingValue = BindingValue.Trim();
        var capturedFormat = BindingFormat;
        if (target is not null && (location is null ||
            !FindNode(workspace!.CreateSnapshot(), target.Id, out _, out _)))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var capturedScope = target is null ? Array.Empty<Guid>() : GetScope(workspace!.CreateSnapshot(), target.Id);
        var binding = new OpaqueBinding(capturedFormat, capturedBindingValue);
        var result = await _main.PreviewAssets.ProbeResourceAsync(peer.ClientId, project, binding);
        if (result.Status != ResourceResolutionStatus.Resolved)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult", result.Status,
                result.ActualKind ?? "", result.Detail ?? "");
            return;
        }
        if (result.ActualKind != capturedKind || location is not null &&
            capturedKind != KindForLocation(location.Id, target?.Kind))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityKindMismatch", capturedKind,
                result.ActualKind ?? "");
            return;
        }
        if (_disposed || !ReferenceEquals(workspace, _main.ActiveGraphProvider.Graph) ||
            peer.ClientId != SelectedPeer?.ClientId || project != ProjectState?.Project ||
            capturedKind != ResourceKind || capturedName != ResourceName.Trim() ||
            capturedBindingValue != BindingValue.Trim() || capturedFormat != BindingFormat ||
            target is not null && (target.Id != SelectedTargetNode?.Id ||
                location?.Id != SelectedLocation?.Id ||
                !FindNode(workspace!.CreateSnapshot(), target.Id, out var currentScope, out _) ||
                !ResourceIdentityScope.Equal(capturedScope, currentScope)))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var resourceId = Guid.NewGuid();
        var current = workspace?.ResourceIdentities;
        var useExists = target is not null && location is not null && current?.Uses.Any(use =>
            use.NodeId == target.Id && use.Location == location.Id &&
            ResourceIdentityScope.Equal(use.InstancePath, capturedScope)) == true;
        if (useExists && !ReplaceExistingUse)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityReplaceRequired");
            return;
        }
        var definition = new ResourceDefinition(resourceId, capturedKind, capturedName);
        if (workspace is null)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityDocumentRequired");
            return;
        }
        workspace.EditResourceIdentityDocument(document =>
        {
            document ??= EmptyDocument();
            if (document.Resources.Any(resource => resource.Id == resourceId))
                throw new InvalidDataException("Generated duplicate resource identity.");
            var uses = document.Uses.Where(use => target is null || location is null ||
                use.NodeId != target.Id || use.Location != location.Id ||
                !ResourceIdentityScope.Equal(use.InstancePath, capturedScope)).ToList();
            if (target is not null && location is not null)
                uses.Add(new(target.Id, location.Id, resourceId, capturedScope));
            return document with
            {
                Resources = document.Resources.Append(definition).ToArray(),
                ResourceBindings = document.ResourceBindings.Append(
                    new ProjectResourceBinding(project, resourceId, capturedKind, binding)).ToArray(),
                Uses = uses.ToArray()
            };
        }, LocalizationService.Shared.Get(useExists
            ? "Resources.IdentityHistoryReplace" : "Resources.IdentityHistoryRegister"));
        ReplaceExistingUse = false;
        RefreshRegisteredResources();
        SelectedResource = RegisteredResources.FirstOrDefault(item => item.Id == resourceId);
        Status = LocalizationService.Shared.Get("Resources.IdentityRegistered", capturedName, resourceId);
    }

    private async Task AssignExistingAsync()
    {
        if (_observedGraph is not { } workspace || SelectedPeer is not { } peer ||
            ProjectState?.Project is not { } project || SelectedTargetNode is not { } target ||
            SelectedLocation is not { } location || SelectedResource is not { } selected)
            return;
        var initialGraph = workspace.CreateSnapshot();
        if (!FindNode(initialGraph, target.Id, out var capturedScope, out _)) return;
        var document = workspace.ResourceIdentities;
        if (document is null) return;
        var resource = document.Resources.SingleOrDefault(item => item.Id == selected.Id);
        var binding = document.ResourceBindings.SingleOrDefault(item =>
            item.Project == project && item.ResourceId == selected.Id);
        if (resource is null || binding is null) return;
        if (resource.Kind != KindForLocation(location.Id, target.Kind))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityKindMismatch",
                KindForLocation(location.Id, target.Kind), resource.Kind);
            return;
        }
        var probe = await _main.PreviewAssets.ProbeResourceAsync(peer.ClientId, project, binding.Binding);
        if (probe.Status != ResourceResolutionStatus.Resolved || probe.ActualKind != resource.Kind)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityProbeResult",
                probe.Status, probe.ActualKind ?? "", probe.Detail ?? "");
            return;
        }
        if (_disposed || !ReferenceEquals(workspace, _main.ActiveGraphProvider.Graph) ||
            SelectedTargetNode?.Id != target.Id || SelectedLocation?.Id != location.Id ||
            SelectedResource?.Id != selected.Id || ProjectState?.Project != project ||
            !ReferenceEquals(document, workspace.ResourceIdentities))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var root = workspace.CreateSnapshot();
        if (!FindNode(root, target.Id, out var scope, out _)) return;
        if (!ResourceIdentityScope.Equal(capturedScope, scope))
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityTargetChanged");
            return;
        }
        var replaces = document.Uses.Any(use => use.NodeId == target.Id && use.Location == location.Id &&
            ResourceIdentityScope.Equal(use.InstancePath, scope));
        if (replaces && !ReplaceExistingUse)
        {
            Status = LocalizationService.Shared.Get("Resources.IdentityReplaceRequired");
            return;
        }
        workspace.EditResourceIdentityDocument(current =>
        {
            current ??= EmptyDocument();
            return current with
            {
                Uses = current.Uses.Where(use => use.NodeId != target.Id || use.Location != location.Id ||
                    !ResourceIdentityScope.Equal(use.InstancePath, scope)).Append(
                    new ResourceUse(target.Id, location.Id, selected.Id, scope)).ToArray()
            };
        }, LocalizationService.Shared.Get(replaces
            ? "Resources.IdentityHistoryReplace" : "Resources.IdentityHistoryAssign"));
        ReplaceExistingUse = false;
        Status = LocalizationService.Shared.Get("Resources.IdentityAssigned", selected.Name, location.Id);
    }

}
