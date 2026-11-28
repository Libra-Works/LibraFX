using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Text.Json;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Views;

public partial class GraphNodeView : UserControl
{
    private void DetachImageNode()
    {
        if (_imageNode is not null) _imageNode.PropertyChanged -= OnImageNodeChanged;
        _imageNode = null;
        EmbeddedImage.Source = null;
        _commentBitmap?.Dispose();
        _commentBitmap = null;
    }

    private void OnImageNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphNodeViewModel.Parameters))
        {
            UpdateCommentImage();
            UpdateCommentBody();
        }
    }

    private void UpdateCommentBody()
    {
        var formatted = _imageNode?.HasFormattedComment == true;
        CommentSource.IsVisible = !formatted || _editingComment;
        CommentPreview.IsVisible = formatted && !_editingComment;
        EditCommentButton.Icon = _editingComment ? Models.Appearance.EditorIcon.Check : Models.Appearance.EditorIcon.Settings;
    }

    internal bool IsBlankCommentArea(PointerEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } ||
            !new Rect(CommentBody.Bounds.Size).Contains(e.GetPosition(CommentBody)) ||
            e.Source is not Visual source ||
            source.GetSelfAndVisualAncestors().Any(visual => visual is ScrollBar or CheckBox))
        {
            return false;
        }

        if (CommentSource.IsVisible && CommentSource.FindDescendantOfType<Avalonia.Controls.Presenters.TextPresenter>() is { } text &&
            !string.IsNullOrWhiteSpace(CommentSource.Text))
        {
            return !text.TextLayout.HitTestPoint(e.GetPosition(text)).IsInside;
        }

        return !CommentPreview.IsVisible ||
            !source.GetSelfAndVisualAncestors().TakeWhile(visual => visual != CommentPreview)
                .OfType<TextBlock>().Any(label => label.TextLayout.HitTestPoint(e.GetPosition(label)).IsInside);
    }

    internal bool ShouldFocusGraphOnBlankComment(PointerPressedEventArgs e) =>
        !_editingComment && CommentSource.IsVisible && !string.IsNullOrWhiteSpace(CommentSource.Text) &&
        IsBlankCommentArea(e);

    internal bool TryScrollComment(PointerWheelEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } ||
            !new Rect(CommentBody.Bounds.Size).Contains(e.GetPosition(CommentBody)))
        {
            return false;
        }

        var viewer = CommentSource.IsVisible ? CommentSource.FindDescendantOfType<ScrollViewer>()
            : CommentPreview.Content as ScrollViewer;
        if (viewer is null || viewer.Extent.Height <= viewer.Viewport.Height)
        {
            return false;
        }

        viewer.Offset = new Vector(viewer.Offset.X,
            Math.Clamp(viewer.Offset.Y - e.Delta.Y * 48, 0, viewer.Extent.Height - viewer.Viewport.Height));
        return true;
    }

    private void OnEditCommentClick(object? sender, RoutedEventArgs e)
    {
        if (_editingComment && _imageNode is { } node) node.CommentText = CommentSource.Text ?? "";
        _editingComment = !_editingComment;
        UpdateCommentBody();
        if (_editingComment) CommentSource.Focus();
        e.Handled = true;
    }

    private void OnCommentTaskChanged(object? sender, CommentTaskChangedEventArgs e)
    {
        if (_imageNode is not { CommentStyle: CommentFormat.Markdown } node ||
            !node.Workspace.Nodes.Contains(node) || node.CommentText != e.Source || e.Offset < 0) return;
        node.CommentText = CommentMarkup.ToggleTask(e.Source, e.Offset, e.Value);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (DataContext is GraphNodeViewModel node) node.RefreshLocalizedText();
        if (_imageStatusKey is { } key)
            ImageStatus.Text = LocalizationService.Shared.Get(key);
    }

    private void SetImageStatus(string? key, string? externalMessage = null)
    {
        _imageStatusKey = key;
        ImageStatus.Text = key is null
            ? externalMessage ?? ""
            : LocalizationService.Shared.Get(key);
    }

    private void SetImageError(Exception exception)
    {
        var key = exception.Message switch
        {
            "Image exceeds 16 MiB." => "Panels.CommentImageSourceTooLarge",
            "Choose a PNG or JPEG image no larger than 8192 x 8192 pixels." => "Panels.CommentImageInvalid",
            "Image could not be prepared for embedding." => "Panels.CommentImagePrepareFailed",
            "Image could not be reduced to the embedding limit." => "Panels.CommentImageOptimizeFailed",
            _ => null
        };
        SetImageStatus(key, exception.Message);
    }

    private void UpdateCommentImage()
    {
        EmbeddedImage.Source = null;
        _commentBitmap?.Dispose();
        _commentBitmap = null;
        SetImageStatus(null);
        var definition = _imageNode?.Definition.Parameters.FirstOrDefault(item => item.Id == "image");
        var value = definition is null ? null : _imageNode!.GetParameterValue(definition).GetString();
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(value), false);
            var bitmap = new Bitmap(stream);
            if (bitmap.PixelSize.Width > 4096 || bitmap.PixelSize.Height > 4096)
            {
                bitmap.Dispose();
                SetImageStatus("Panels.CommentImageDisplayTooLarge");
                return;
            }
            _commentBitmap = bitmap;
            EmbeddedImage.Source = bitmap;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or InvalidDataException or
                                   InvalidOperationException or NotSupportedException)
        {
            SetImageStatus("Panels.CommentImageDecodeFailed");
        }
    }

    private async void OnAddImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphNodeViewModel { IsComment: true } node ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Shared.Get("Panels.ChooseCommentImage"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(LocalizationService.Shared.Get("Panels.PngOrJpeg")) { Patterns = ["*.png", "*.jpg", "*.jpeg"] }]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await AddCommentImageAsync(node, file);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (ReferenceEquals(DataContext, node)) SetImageError(ex);
        }
    }

    private async Task AddCommentImageAsync(GraphNodeViewModel node, IStorageFile file)
    {
        try
        {
            await using var input = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + count > CommentImage.MaximumSourceBytes)
                    throw new InvalidDataException("Image exceeds 16 MiB.");
                buffer.Write(chunk, 0, count);
            }
            var data = buffer.ToArray();
            if (!CommentImage.IsValidSource(data, out var width, out var height))
                throw new InvalidDataException("Choose a PNG or JPEG image no larger than 8192 x 8192 pixels.");
            var optimized = data.Length > CommentImage.MaximumBytes || width > 4096 || height > 4096;
            if (optimized) data = OptimizeCommentImage(data, width, height);
            else
            {
                buffer.Position = 0;
                using var bitmap = new Bitmap(buffer);
            }
            var value = JsonSerializer.SerializeToElement(Convert.ToBase64String(data));
            if (!NodeParameter.IsValidValue("commentImage", value))
                throw new InvalidDataException("Image could not be prepared for embedding.");
            if (ReferenceEquals(DataContext, node) && node.Workspace.Nodes.Contains(node))
            {
                node.Workspace.SetParameter(node, "image", value);
                if (optimized) SetImageStatus("Panels.CommentImageOptimized");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or NotSupportedException)
        {
            if (ReferenceEquals(DataContext, node)) SetImageError(ex);
        }
    }

    private static byte[] OptimizeCommentImage(byte[] source, int width, int height)
    {
        var png = source.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        foreach (var size in new[] { 1024, 768, 512, 256 })
        {
            using var input = new MemoryStream(source, false);
            using var bitmap = width >= height
                ? Bitmap.DecodeToWidth(input, Math.Min(size, width), BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToHeight(input, Math.Min(size, height), BitmapInterpolationMode.HighQuality);
            using var output = new MemoryStream();
            bitmap.Save(output, png ? PngBitmapEncoderOptions.Default : JpegBitmapEncoderOptions.Default);
            if (output.Length <= CommentImage.MaximumBytes) return output.ToArray();
        }
        throw new InvalidDataException("Image could not be reduced to the embedding limit.");
    }

    private void OnRemoveImageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphNodeViewModel { IsComment: true } node)
            node.Workspace.SetParameter(node, "image", JsonSerializer.SerializeToElement(""));
    }

}
