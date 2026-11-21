using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class ProceduralTexturePreviewTests
{
    public static void Run()
    {
        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = graph.CreateNode(new("ProceduralTexture"));
        graph.ApplyProceduralRecipe(node, new ProceduralRecipe
        {
            Operations = [ProceduralResourceSchema.CreatePixel(5, 0), ProceduralResourceSchema.CreatePixel(25, 1)], OutputRegister = 2
        });
        var snapshot = graph.CreateSnapshot().Nodes.Single();
        var frame = ProceduralTexturePreview.Render(snapshot, 0, CancellationToken.None);
        var center = ((frame.Height / 2) * frame.Width + frame.Width / 2) * 4;
        if (frame.UsesTime || frame.Pixels[center + 3] < 245 || frame.Pixels[3] != 0 ||
            frame.Pixels[center] > frame.Pixels[center + 3])
            throw new InvalidOperationException($"Local preview must use the runtime radial mask and premultiplied transparency: animated={frame.UsesTime}, center={string.Join(',', frame.Pixels.Skip(center).Take(4))}, corner={string.Join(',', frame.Pixels.Take(4))}.");
        var animated = ProceduralTexturePreview.Render(new GraphNodeSnapshot(Guid.NewGuid(), new("ProceduralTexture"), "Animated", 0, 0,
            node.PortType, node.Definition.PortCategory), 0, CancellationToken.None);
        var later = ProceduralTexturePreview.Render(new GraphNodeSnapshot(Guid.NewGuid(), new("ProceduralTexture"), "Animated", 0, 0,
            node.PortType, node.Definition.PortCategory), 1, CancellationToken.None);
        if (!animated.UsesTime || animated.Pixels.SequenceEqual(later.Pixels))
            throw new InvalidOperationException("Local preview must reflect procedural animation time.");
        var view = new ProceduralWorkspaceView();
        var window = new Window { Content = view, Width = 1120, Height = 760 };
        window.Show();
        try
        {
            view.Open(graph, node.Id);
            window.UpdateLayout();
            var watch = Stopwatch.StartNew();
            var preview = view.GetVisualDescendants().OfType<Image>().Single(image => image.Name == "TexturePreviewImage");
            var dispatcherFrame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (preview.Source is not null || watch.Elapsed > TimeSpan.FromSeconds(10)) dispatcherFrame.Continue = false; };
            timer.Start();
            try { Dispatcher.UIThread.PushFrame(dispatcherFrame); }
            finally { timer.Stop(); }
            if (preview.Source is null)
                throw new InvalidOperationException("The texture Flow must display its local preview without an engine connection.");
            window.UpdateLayout();
            var directory = Path.Combine(Environment.CurrentDirectory, "build", "authoring-release");
            Directory.CreateDirectory(directory);
            using var image = new RenderTargetBitmap(new PixelSize(1120, 760));
            image.Render(view);
            image.Save(Path.Combine(directory, "local-texture-preview.png"), PngBitmapEncoderOptions.Default);
            view.CloseWorkspace();
            if (preview.Source is not null)
                throw new InvalidOperationException("Closing the Flow must release its local preview bitmap.");
        }
        finally { window.Close(); }
        Console.WriteLine("Local procedural texture preview: runtime pixels, transparency, animation and UI lifetime passed.");
    }
}
