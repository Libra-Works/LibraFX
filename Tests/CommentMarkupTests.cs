using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;
using LiberaFx.Views;

internal static class CommentMarkupTests
{
    public static void Run()
    {
        const string markdown = "# Work\r\n- [ ] **First** task\r\n- [x] _Second_ `code`\r\n[link](https://example.invalid)";
        var parsed = CommentMarkup.Parse(markdown, CommentFormat.Markdown);
        if (parsed[0].Heading != 1 || parsed[1].Checked != false || parsed[2].Checked != true ||
            parsed[1].Spans.All(span => !span.Bold) || parsed[2].Spans.All(span => !span.Code) ||
            parsed[3].Spans.Single().Link != "https://example.invalid")
            throw new InvalidOperationException("Markdown headings, inline styles, tasks or links were not parsed.");
        var toggled = CommentMarkup.ToggleTask(markdown, parsed[1].TaskOffset, true);
        if (toggled != markdown.Replace("- [ ]", "- [x]", StringComparison.Ordinal))
            throw new InvalidOperationException("Toggling a task changed unrelated source text or CRLF line endings.");
        var html = CommentMarkup.Parse("""
            <h2>Title</h2><p><strong>Safe &amp; readable</strong></p>
            <script>secret script content</script><style>hidden style content</style>
            <iframe src="https://example.invalid">hidden frame</iframe>
            <a href="javascript:alert(1)" onclick="alert(1)">Visible label</a>
            <img src="https://example.invalid/private.png" onerror="alert(1)">
            <input type="checkbox" checked><b>Done</b>
            """, CommentFormat.Html);
        var rendered = string.Concat(html.SelectMany(block => block.Spans).Select(span => span.Text));
        if (rendered.Contains("secret", StringComparison.Ordinal) || rendered.Contains("hidden", StringComparison.Ordinal) ||
            !rendered.Contains("Safe & readable", StringComparison.Ordinal) || !rendered.Contains("Visible label", StringComparison.Ordinal) ||
            html.SelectMany(block => block.Spans).Any(span => span.Link is not null) ||
            !html.Any(block => block.Checked == true && block.TaskOffset == -1))
            throw new InvalidOperationException("The inert HTML allowlist exposed active content or lost safe text.");
        if (CommentMarkup.Parse("<b>plain text</b>", CommentFormat.Text).Single().Spans.Single().Text != "<b>plain text</b>" ||
            CommentMarkup.Parse("<script>unterminated", CommentFormat.Html).Count != 0 ||
            CommentMarkup.Parse(string.Concat(Enumerable.Repeat("- [ ] task\n", 600)), CommentFormat.Markdown).Count > 257)
            throw new InvalidOperationException("Comment parsing did not preserve plain text or bound malformed/large input.");

        var graph = new GraphWorkspaceViewModel(new UndoRedoManager());
        var node = graph.CreateNode(GraphNodeKind.Comment);
        if (node.CommentStyle != CommentFormat.Text)
            throw new InvalidOperationException("Legacy comments did not default to plain text.");
        node.CommentText = markdown;
        graph.SetParameter(node, "format", JsonSerializer.SerializeToElement(1));
        var view = new GraphNodeView { DataContext = node };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            if (view.FindControl<TextBox>("CommentSource")!.IsVisible ||
                !view.FindControl<CommentMarkupView>("CommentPreview")!.IsVisible)
                throw new InvalidOperationException("Markdown comments do not display their formatted preview.");
            var task = view.GetVisualDescendants().OfType<CheckBox>().Single(check => Equals(check.Tag, parsed[1].TaskOffset));
            task.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            if (node.CommentText != toggled || !graph.History.Undo() ||
                graph.GetNode(node.Id).CommentText != markdown || !graph.History.Redo() ||
                graph.GetNode(node.Id).CommentText != toggled)
                throw new InvalidOperationException("Task editing is not source-preserving and undoable.");
            using var project = new MemoryStream();
            new ProjectFileService().SaveAsync(project, graph).GetAwaiter().GetResult();
            project.Position = 0;
            var reloaded = ProjectDocumentReader.LoadAsync(project).GetAwaiter().GetResult();
            var saved = reloaded.Graph.Nodes.Single().Parameters!;
            if (saved.Single(parameter => parameter.Id == "format").Value.GetInt32() != 1 ||
                saved.Single(parameter => parameter.Id == "text").Value.GetString() != toggled)
                throw new InvalidOperationException("Comment source and format did not round-trip through project persistence.");
        }
        finally
        {
            window.Close();
        }
    }
}
