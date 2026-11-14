using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using LiberaFx.Models.Graph;

namespace LiberaFx.Views;

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
            !GraphNodeDefinition.TryFind(kindName, out var definition))
        {
            return;
        }

        _dragSource = null;
        _pressed = null;
        e.Pointer.Capture(null);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(definition!.Kind.ToString()));
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
}
