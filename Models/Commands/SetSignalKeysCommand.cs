using LiberaFx.Models.Graph;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Models.Commands;

public sealed class SetSignalKeysCommand(GraphNodeViewModel node,
    IReadOnlyList<SignalTimelineKey>? before, IReadOnlyList<SignalTimelineKey>? after,
    string description) : IUndoableCommand
{
    public string Description => description;
    public void Execute() => node.SetSignalKeysValue(after);
    public void Undo() => node.SetSignalKeysValue(before);
}
