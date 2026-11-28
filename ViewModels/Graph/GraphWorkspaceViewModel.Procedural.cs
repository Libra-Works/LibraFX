using System.Text.Json;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Settings;

namespace LiberaFx.ViewModels.Graph;

public sealed partial class GraphWorkspaceViewModel
{
    public event EventHandler<Guid>? ProceduralEditorRequested;
    public void OpenProceduralEditor(GraphNodeViewModel node)
    {
        if (Nodes.Contains(node) && ProceduralResourceSchema.IsMetadata(node.Definition.Kind))
            _document.ProceduralEditorRequested?.Invoke(this, node.Id);
    }

    public void ApplyProceduralRecipe(GraphNodeViewModel node, ProceduralRecipe recipe)
    {
        if (!Nodes.Contains(node) || !ProceduralResourceSchema.IsMetadata(node.Definition.Kind))
            throw new InvalidOperationException("The procedural producer is not in this graph.");
        ProceduralResourceSchema.Validate(recipe, ProceduralResourceSchema.Name(node.Definition.Kind)!);
        var values = node.Parameters?.ToList() ?? [];
        var old = ProceduralResourceSchema.Read(node.BaseDefinition, node.Parameters);
        void UpdateBlocks(Guid id, float[] data, float[]? previous)
        {
            for (var block = 0; block < data.Length / 4; ++block)
            {
                var next = data.Skip(block * 4).Take(4).ToArray();
                if (previous is not null && next.SequenceEqual(previous.Skip(block * 4).Take(4))) continue;
                var key = ProceduralResourceSchema.Port(id, block);
                values.RemoveAll(p => p.Id == key);
                values.Add(new(key, "vector4", JsonSerializer.SerializeToElement(next)));
            }
        }
        foreach (var stage in recipe.Modifiers) UpdateBlocks(stage.Id, stage.Values, old.Modifiers.FirstOrDefault(m => m.Id == stage.Id)?.Values);
        foreach (var stage in recipe.Operations) UpdateBlocks(stage.Id, stage.Values, old.Operations.FirstOrDefault(m => m.Id == stage.Id)?.Values);
        values.RemoveAll(p => p.Id == "proceduralState");
        values.Add(new("proceduralState", "proceduralState", JsonSerializer.SerializeToElement(ProceduralResourceSchema.Encode(recipe))));
        var definition = ProceduralResourceSchema.Effective(node.BaseDefinition, values);
        var valid = definition.Parameters.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        values.RemoveAll(p => !valid.Contains(p.Id) && !p.Id.StartsWith("editor.", StringComparison.Ordinal));
        var commands = new List<IUndoableCommand>();
        commands.AddRange(Connections.Where(c => c.Target == node &&
                !definition.InputPorts!.Any(p => p.Id == c.TargetPortId))
            .Select(c => (IUndoableCommand)new RemoveGraphConnectionCommand(this, c)));
        commands.Add(new SetNodeParametersCommand(node, node.Parameters, values,
            LocalizationService.Shared.Get("Procedural.Edit")));
        ExecuteEdit(new CompositeUndoableCommand(LocalizationService.Shared.Get("Procedural.Edit"), commands));
    }
}
