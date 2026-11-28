using CommunityToolkit.Mvvm.Input;

namespace LiberaFx.ViewModels;

public sealed class GraphProblemViewModel
{
    public GraphNodeViewModel Node { get; }
    public string Message { get; }
    public IRelayCommand SelectCommand { get; }

    internal GraphProblemViewModel(GraphNodeViewModel node, string message)
    {
        Node = node;
        Message = message;
        SelectCommand = new RelayCommand(() => node.Workspace.SelectNode(node));
    }
}
