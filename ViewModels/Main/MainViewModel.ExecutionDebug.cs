using LiberaFx.ViewModels.Panels;

namespace LiberaFx.ViewModels.Main;

public partial class MainViewModel
{
    private ExecutionDebugPaneViewModel? _executionDebugger;
    public ExecutionDebugPaneViewModel ExecutionDebugger => _executionDebugger ??= new(this);
}
