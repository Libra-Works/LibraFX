namespace LiberaFx.Models.Commands;

/// <summary>
/// Groups commands into one undo step. Execute (including redo) follows input order;
/// undo follows reverse order. The input sequence is copied at construction.
/// </summary>
/// <remarks>
/// Child commands must leave their own state unchanged when they throw.
/// If an operation fails, completed child operations are reversed and the original
/// exception is rethrown. Any failures during that recovery are attached to its
/// <see cref="Exception.Data"/> under <see cref="RecoveryErrorsKey"/>; in that case
/// full restoration cannot be guaranteed.
/// </remarks>
public sealed class CompositeUndoableCommand : IUndoableCommand
{
    public const string RecoveryErrorsKey = "CompositeUndoableCommand.RecoveryErrors";

    private readonly IUndoableCommand[] _commands;

    public string Description { get; }

    public CompositeUndoableCommand(string description, IEnumerable<IUndoableCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(commands);

        _commands = commands.ToArray();
        if (_commands.Any(command => command is null))
        {
            throw new ArgumentException("Commands cannot contain null entries.", nameof(commands));
        }

        Description = description;
    }

    public void Execute()
    {
        int completed = 0;
        try
        {
            for (; completed < _commands.Length; completed++)
            {
                _commands[completed].Execute();
            }
        }
        catch (Exception error)
        {
            Recover(error, completed - 1, -1, -1, undo: true);
            throw;
        }
    }

    public void Undo()
    {
        int index = _commands.Length - 1;
        try
        {
            for (; index >= 0; index--)
            {
                _commands[index].Undo();
            }
        }
        catch (Exception error)
        {
            Recover(error, index + 1, _commands.Length, 1, undo: false);
            throw;
        }
    }

    private void Recover(Exception originalError, int start, int end, int step, bool undo)
    {
        List<Exception>? errors = null;
        for (int index = start; index != end; index += step)
        {
            try
            {
                if (undo)
                {
                    _commands[index].Undo();
                }
                else
                {
                    _commands[index].Execute();
                }
            }
            catch (Exception error)
            {
                (errors ??= new()).Add(error);
            }
        }

        if (errors is not null)
        {
            if (originalError.Data[RecoveryErrorsKey] is Exception previousErrors)
            {
                errors.Insert(0, previousErrors);
            }

            originalError.Data[RecoveryErrorsKey] =
                new AggregateException("Failed to restore one or more batch commands.", errors);
        }
    }
}
