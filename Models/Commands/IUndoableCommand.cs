namespace LiberaFx.Models.Commands;

public interface IUndoableCommand
{
    string Description { get; }

    void Execute();

    void Undo();
}
