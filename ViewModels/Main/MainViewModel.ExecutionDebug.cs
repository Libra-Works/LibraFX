namespace LiberaFx.ViewModels;

public partial class MainViewModel
{
    private ExecutionDebugPaneViewModel? _executionDebugger;
    public ExecutionDebugPaneViewModel ExecutionDebugger => _executionDebugger ??= new(this);
}
