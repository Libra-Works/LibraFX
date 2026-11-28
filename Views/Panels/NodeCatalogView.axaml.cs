using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LiberaFx.Models.Graph;
using LiberaFx.Services.Compilation;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Views.Panels;

public partial class NodeCatalogView : UserControl
{
    private Control? _dragSource;
    private PointerPressedEventArgs? _pressed;
    private Point _dragStart;

    public NodeCatalogView()
    {
        InitializeComponent();
    }

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
        if (sender is Control { DataContext: NodeCatalogEntryViewModel { CanCreate: false } }) return;
        if (sender is not Control source || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragSource = source;
        _pressed = e;
        _dragStart = e.GetPosition(this);
        e.Pointer.Capture(source);
    }

    private async void OnNodePointerMoved(object? sender, PointerEventArgs e)
    {
        var current = e.GetPosition(this);
        if (_dragSource is not Control { Tag: string kindName } source ||
            _pressed is not { } pressed ||
            !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed ||
            (Math.Abs(current.X - _dragStart.X) < 5 && Math.Abs(current.Y - _dragStart.Y) < 5) ||
            !IsDraggableIdentifier(kindName))
        {
            return;
        }

        _dragSource = null;
        _pressed = null;
        e.Pointer.Capture(null);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(kindName));
        await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Copy);
    }

    private void OnNodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragSource = null;
        _pressed = null;
        e.Pointer.Capture(null);
    }

    private void OnNodePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragSource = null;
        _pressed = null;
    }

    private static bool IsDraggableIdentifier(string identifier)
    {
        if (identifier.StartsWith(NodeCatalogEntryViewModel.GroupPrefix, StringComparison.Ordinal))
            return true;
        var kindName = identifier.StartsWith(NodeCatalogEntryViewModel.PolymorphicPrefix, StringComparison.Ordinal)
            ? identifier[NodeCatalogEntryViewModel.PolymorphicPrefix.Length..]
            : identifier;
        return GraphNodeDefinition.TryFind(kindName, out _);
    }
}
