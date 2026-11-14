using System.Reflection;
using Avalonia;
using Avalonia.Controls;
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
            var original = node.GetParameterValue(definition).GetString();
            Import(new byte[CommentImage.MaximumBytes + 1]);
            if (!view.FindControl<TextBlock>("ImageStatus")!.Text!.Contains("512 KiB") ||
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
