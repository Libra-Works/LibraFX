using LiberaFx.Services.Preview;

namespace LiberaFx.ViewModels.Graph;

public sealed partial class GraphWorkspaceViewModel
{
    internal ExecutionDebugFrame? ExecutionDebugFrame { get; private set; }
    internal IReadOnlySet<Guid> ExecutionDebugBreakpoints { get; private set; } = new HashSet<Guid>();

    internal void SetExecutionDebug(ExecutionDebugFrame? frame, HashSet<Guid> breakpoints)
    {
        ExecutionDebugBreakpoints = breakpoints;
        ExecutionDebugFrame = frame;
        OnPropertyChanged(nameof(ExecutionDebugFrame));
    }
}
