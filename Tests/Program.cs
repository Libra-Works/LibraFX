using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using LiberaFx;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Environment.SetEnvironmentVariable("LIBERAFX_DATA_ROOT",
            Path.Combine(Path.GetTempPath(), "liberafx-comment-test-" + Guid.NewGuid().ToString("N")));
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = workspace.CreateNode(GraphNodeKind.Comment);
        var view = new GraphNodeView { DataContext = node };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            var file = DispatchProxy.Create<IStorageFile, TestStorageFile>();
            var fake = (TestStorageFile)file;
            var import = typeof(GraphNodeView).GetMethod("AddCommentImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Import(byte[] data)
            {
                fake.Data = data;
                ((Task)import.Invoke(view, [node, file])!).GetAwaiter().GetResult();
            }

            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVQIHWP8DwQMQMD4HwgYgAAAV9UH+14vURUAAAAASUVORK5CYII=");
            Import(png);
            if (view.FindControl<Image>("EmbeddedImage")!.Source is null)
                throw new InvalidOperationException("Valid comment image did not render.");
            var definition = node.Definition.Parameters.Single(item => item.Id == "image");
            var largePng = CreateLargeImage(PngBitmapEncoderOptions.Default);
            if (largePng.Length <= CommentImage.MaximumBytes)
                throw new InvalidOperationException("Large image fixture is too small.");
            Import(largePng);
            var original = node.GetParameterValue(definition).GetString();
            if (view.FindControl<Image>("EmbeddedImage")!.Source is null ||
                !view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("optimized") ||
                Convert.FromBase64String(original!).Length > CommentImage.MaximumBytes)
                throw new InvalidOperationException("A large PNG was not optimized and displayed.");
            var largeJpeg = CreateLargeImage(new JpegBitmapEncoderOptions { Quality = 100 });
            if (largeJpeg.Length <= CommentImage.MaximumBytes)
                throw new InvalidOperationException("Large JPEG fixture is too small.");
            Import(largeJpeg);
            original = node.GetParameterValue(definition).GetString();
            var embeddedJpeg = Convert.FromBase64String(original!);
            if (view.FindControl<Image>("EmbeddedImage")!.Source is null ||
                !view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("optimized") ||
                embeddedJpeg.Length > CommentImage.MaximumBytes ||
                embeddedJpeg[0] != 255 || embeddedJpeg[1] != 216)
                throw new InvalidOperationException("A large JPEG was not optimized and displayed.");
            Import(new byte[CommentImage.MaximumSourceBytes + 1]);
            if (!view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("16 MiB") ||
                node.GetParameterValue(definition).GetString() != original)
                throw new InvalidOperationException("Oversized image was not safely rejected.");
            Import([0, 1, 2, 3]);
            if (!view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("PNG or JPEG") ||
                node.GetParameterValue(definition).GetString() != original)
                throw new InvalidOperationException("Invalid image was not safely rejected.");
            Import(png[..24]);
            if (string.IsNullOrEmpty(view.FindControl<TextBlock>("ImageStatus")!.Text) ||
                node.GetParameterValue(definition).GetString() != original)
                throw new InvalidOperationException("Truncated image was not safely rejected.");
            Console.WriteLine("Comment image import checks passed.");
        }
        finally
        {
            window.Close();
        }
        GroupLibraryTests.Run();
        Console.WriteLine("Project profile and group library checks passed.");
    }

    private static byte[] CreateLargeImage(BitmapEncoderOptions options)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(1024, 768), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var pixels = bitmap.Lock())
        {
            var data = new byte[pixels.RowBytes * pixels.Size.Height];
            new Random(42).NextBytes(data);
            for (var index = 3; index < data.Length; index += 4) data[index] = 255;
            System.Runtime.InteropServices.Marshal.Copy(data, 0, pixels.Address, data.Length);
        }
        using var output = new MemoryStream();
        bitmap.Save(output, options);
        return output.ToArray();
    }
}

public class TestStorageFile : DispatchProxy
{
    public byte[] Data { get; set; } = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method?.Name switch
        {
            nameof(IStorageFile.OpenReadAsync) => Task.FromResult<Stream>(new MemoryStream(Data, false)),
            nameof(IDisposable.Dispose) => null,
            _ => throw new NotSupportedException(method?.Name)
        };
}
