namespace LiberaFx.Models.Commands;

// The document's state-tree history restores snapshots; no child undo stack exists.
internal sealed class InlineGraphEditCommand(IUndoableCommand edit, Action execute) : IUndoableCommand
{
    public string Description => edit.Description;
    public void Execute() => execute();
    public void Undo() => throw new NotSupportedException("Restore the document history snapshot.");
}
