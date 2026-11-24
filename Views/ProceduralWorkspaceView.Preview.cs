using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LiberaFx.Models.Graph;
using LiberaFx.Services;

namespace LiberaFx.Views;

public sealed partial class ProceduralWorkspaceView
{
    private readonly StackPanel _texturePreview = new() { Name = "LocalTexturePreview", Margin = new Thickness(12, 8), Spacing = 4 };
    private readonly Image _textureImage = new() { Name = "TexturePreviewImage", Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly ProceduralMeshPreviewView _meshPreview = new() { Name = "MeshPreview", HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _texturePreviewTitle = new();
    private readonly TextBlock _texturePreviewStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly NumericUpDown _texturePreviewTime = new() { Minimum = 0, Maximum = 1_000_000, Value = 0, Increment = .1m, Width = 115 };
    private readonly IconButton _texturePreviewPlay = new() { Icon = Models.Appearance.EditorIcon.Play };
    private readonly DispatcherTimer _texturePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _texturePreviewClock = new();
    private double _texturePreviewEpoch;
    private WriteableBitmap? _textureBitmap;
    private CancellationTokenSource? _texturePreviewCancellation;
    private int _texturePreviewGeneration;
    private bool _texturePreviewBusy;

    private Control CreateTexturePreview()
    {
        var image = new Grid { Height = 220, Children = { new ColorSwatch { Color = Colors.Transparent }, _textureImage, _meshPreview } };
        _texturePreview.Children.Add(_texturePreviewTitle);
        _texturePreview.Children.Add(image);
        _texturePreview.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { _texturePreviewPlay, _texturePreviewTime } });
        _texturePreview.Children.Add(_texturePreviewStatus);
        _texturePreviewTime.ValueChanged += (_, _) => { if (IsVisible) QueueTexturePreview(); };
        _texturePreviewPlay.Click += (_, _) =>
        {
            if (_texturePreviewTimer.IsEnabled)
            {
                _texturePreviewTimer.Stop(); _texturePreviewClock.Stop();
                _texturePreviewPlay.Icon = Models.Appearance.EditorIcon.Play;
            }
            else
            {
                _texturePreviewEpoch = (double)(_texturePreviewTime.Value ?? 0);
                _texturePreviewClock.Restart(); _texturePreviewTimer.Start();
                _texturePreviewPlay.Icon = Models.Appearance.EditorIcon.Pause;
            }
        };
        _texturePreviewTimer.Tick += (_, _) =>
        {
            if (IsVisible && !_texturePreviewBusy)
                _texturePreviewTime.Value = (decimal)Math.Min(1_000_000, _texturePreviewEpoch + _texturePreviewClock.Elapsed.TotalSeconds);
        };
        _texturePreview.AttachedToVisualTree += (_, _) => { if (IsVisible && _nodeId != Guid.Empty) QueueTexturePreview(); };
        return _texturePreview;
    }

    private void RefreshTexturePreview()
    {
        _texturePreview.IsVisible = true;
        _textureImage.IsVisible = !Model;
        _meshPreview.IsVisible = Model;
        _texturePreviewTitle.Text = T(Model ? "MeshPreview" : "TexturePreview");
        ToolTip.SetTip(_texturePreviewTime, T("PreviewTime"));
        ToolTip.SetTip(_texturePreviewPlay, T("PreviewPlay"));
        QueueTexturePreview();
    }
    private async void QueueTexturePreview()
    {
        if (!IsVisible) return;
        _texturePreviewCancellation?.Cancel();
        _texturePreviewCancellation?.Dispose();
        var cancellation = _texturePreviewCancellation = new CancellationTokenSource();
        var generation = ++_texturePreviewGeneration;
        var node = _node;
        var snapshot = new GraphNodeSnapshot(node.Id, node.BaseDefinition.Kind, node.Name, 0, 0,
            node.PortType, node.BaseDefinition.PortCategory, Parameters: node.Parameters);
        var time = (double)(_texturePreviewTime.Value ?? 0);
        _texturePreviewBusy = true;
        try
        {
            if (Model)
            {
                var mesh = await Task.Run(() => ProceduralMeshPreview.Render(snapshot, time, cancellation.Token));
                if (cancellation.IsCancellationRequested || generation != _texturePreviewGeneration || !IsVisible) return;
                _meshPreview.Show(mesh);
                _texturePreviewStatus.Text = T("MeshPreviewHelp");
                return;
            }
            var frame = await Task.Run(() => ProceduralTexturePreview.Render(snapshot, time, cancellation.Token));
            if (cancellation.IsCancellationRequested || generation != _texturePreviewGeneration || !IsVisible) return;
            var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var buffer = bitmap.Lock())
                for (var row = 0; row < frame.Height; row++)
                    Marshal.Copy(frame.Pixels, row * frame.Width * 4, buffer.Address + row * buffer.RowBytes, frame.Width * 4);
            var previous = _textureBitmap;
            _textureBitmap = bitmap; _textureImage.Source = bitmap; previous?.Dispose();
            _texturePreviewStatus.Text = _workspace.Connections.Any(edge => edge.Target.Id == node.Id &&
                edge.TargetPortId?.StartsWith("stage_", StringComparison.Ordinal) == true)
                ? T("PreviewDefaults") : T("PreviewLocal");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or
            ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (generation != _texturePreviewGeneration || !IsVisible) return;
            _meshPreview.Show(null);
            _textureImage.Source = null; _textureBitmap?.Dispose(); _textureBitmap = null;
            _texturePreviewStatus.Text = T("PreviewUnavailable") + " " + error.Message;
        }
        finally { if (generation == _texturePreviewGeneration) _texturePreviewBusy = false; }
    }
    private void ClearTexturePreview()
    {
        ++_texturePreviewGeneration;
        _texturePreviewCancellation?.Cancel(); _texturePreviewCancellation?.Dispose(); _texturePreviewCancellation = null;
        _texturePreviewTimer.Stop(); _texturePreviewClock.Stop(); _texturePreviewBusy = false;
        _texturePreviewPlay.Icon = Models.Appearance.EditorIcon.Play;
        _textureImage.Source = null; _textureBitmap?.Dispose(); _textureBitmap = null;
        _meshPreview.Show(null);
    }
}
