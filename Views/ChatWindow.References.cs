using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using LiberaFx.Extensions.Abstractions;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public sealed partial class ChatWindow
{
    private readonly WrapPanel _nodeReferences = new() { Orientation = Orientation.Horizontal };
    private readonly Button _explainSelection = new();
    private GraphWorkspaceViewModel? _selectionWorkspace;
    private Guid[] _selectionIds = [];
    private Guid[] _selectionPath = [];
    private const string ReferenceInstructions =
        "When explaining known graph nodes, cite them as [readable node name](liberafx-node:UUID). " +
        "A single UUID refers to the current editing level. For another level use a leading slash and " +
        "slash-separated root-to-parent group UUIDs followed by the node UUID; /UUID means a root node. Never invent node IDs. " +
        "Explain the overview before details. Distinguish observed graph behavior from inferred author intent. " +
        "Reply in the user's language.";

    private void ExplainSelection()
    {
        var nodes = _main.Graph.SelectedNodes.Take(32).ToArray();
        if (nodes.Length == 0) { SetStatus("SelectionRequired"); return; }
        _selectionWorkspace = _main.ActiveGraphProvider.Graph;
        _selectionPath = _selectionWorkspace.CurrentGraphPath.ToArray();
        _selectionIds = nodes.Select(node => node.Id).ToArray();
        _input.Text = Text("SelectionQuestion") + "\n" + string.Join(", ", nodes.Select(node => node.Name));
        _input.Focus();
        SetStatus("SelectionAttached");
    }

    private void ShowNodeReferences(string text)
    {
        _nodeReferences.Children.Clear();
        if (_main.ActiveDocument is not { } document) return;
        foreach (var reference in ChatGraphReferences.Read(text, document, _main.ActiveGraphProvider.Graph.CurrentGraphPath))
        {
            var button = new Button
            {
                Content = reference.Label, Tag = reference, MaxWidth = 300,
                Margin = new Thickness(0, 0, 6, 6)
            };
            ToolTip.SetTip(button, reference.Label);
            Avalonia.Automation.AutomationProperties.SetName(button, Text("JumpToNode") + ": " + reference.Label);
            button.Click += (_, _) =>
            {
                if (_busy) return;
                _conversationContext?.Dispose();
                _conversationContext = null;
                _messages.Clear();
                _selectionWorkspace = null;
                _selectionIds = [];
                _selectionPath = [];
                SetStatus(ChatGraphReferences.Navigate(_main, reference) ? "NodeNavigated" : "NodeUnavailable");
            };
            _nodeReferences.Children.Add(button);
        }
    }

    private bool PrepareSelectionContext(bool share)
    {
        if (_selectionIds.Length == 0) return true;
        if (!share || !ReferenceEquals(_selectionWorkspace, _main.ActiveGraphProvider.Graph) ||
            !_selectionPath.SequenceEqual(_main.ActiveGraphProvider.Graph.CurrentGraphPath) ||
            _selectionIds.Any(id => !_main.Graph.Nodes.Any(node => node.Id == id)))
        {
            SetStatus("SelectionChanged");
            return false;
        }
        _messages.Add(new ChatMessage("system", "The user is asking about these selected node IDs: " +
            JsonSerializer.Serialize(new { parentPath = _selectionPath, nodeIds = _selectionIds })));
        _selectionWorkspace = null;
        _selectionIds = [];
        _selectionPath = [];
        return true;
    }
}
