using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

// EffectiveViewport includes clipping by the list's ScrollViewer. Realized but
// offscreen virtualization buffers must not initiate engine asset requests.
public sealed class EngineAssetThumbnail : Image
{
    private Rect _viewport;
    private IDisposable? _subscription;
    private Bitmap? _bitmap;
    private bool _attached;

    public EngineAssetThumbnail()
    {
        Width = Height = 48;
        Stretch = Stretch.Uniform;
        DataContextChanged += (_, _) => { Release(); Observe(); };
        EffectiveViewportChanged += (_, e) => { _viewport = e.EffectiveViewport; Observe(); };
        AttachedToVisualTree += (_, _) => { _attached = true; Observe(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; _viewport = default; Release(); };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) Observe();
    }

    private void Observe()
    {
        if (!_attached || !IsEffectivelyVisible || !_viewport.Intersects(new Rect(Bounds.Size)))
        { Release(); return; }
        if (_subscription is not null) return;
        var asset = DataContext as EngineAsset ?? (DataContext as AssetBrowserRootEntry)?.EngineAsset;
        var browser = this.GetVisualAncestors().OfType<EngineAssetsWindow>().FirstOrDefault()?.DataContext as EngineAssetsWindowViewModel;
        if (asset is null || browser is null) return;
        _subscription = browser.ObserveVisibleThumbnail(asset, image =>
        {
            using var stream = new MemoryStream(image.Image, writable: false);
            var bitmap = Bitmap.DecodeToWidth(stream, 96);
            Source = bitmap;
            _bitmap?.Dispose();
            _bitmap = bitmap;
        });
    }

    private void Release()
    {
        _subscription?.Dispose(); _subscription = null;
        Source = null;
        _bitmap?.Dispose(); _bitmap = null;
    }
}
