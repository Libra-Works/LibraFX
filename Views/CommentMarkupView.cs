using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LiberaFx.Models.Graph;

namespace LiberaFx.Views;

public sealed class CommentTaskChangedEventArgs(string source, int offset, bool value) : EventArgs
{
    public string Source { get; } = source;
    public int Offset { get; } = offset;
    public bool Value { get; } = value;
}

public sealed class CommentMarkupView : ContentControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<CommentMarkupView, string>(nameof(Text), "");
    public static readonly StyledProperty<CommentFormat> FormatProperty = AvaloniaProperty.Register<CommentMarkupView, CommentFormat>(nameof(Format));
    private readonly StackPanel _blocks;
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public CommentFormat Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public event EventHandler<CommentTaskChangedEventArgs>? TaskChanged;

    public CommentMarkupView()
    {
        _blocks = new StackPanel { Spacing = 5 };
        Content = new ScrollViewer
        {
            Content = _blocks,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Render();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FormatProperty) Render();
    }

    private void Render()
    {
        if (_blocks is null) return;
        _blocks.Children.Clear();
        var source = Text ?? "";
        foreach (var block in CommentMarkup.Parse(source, Format))
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = block.Heading > 0 ? 23 - block.Heading * 1.5 : 13,
                FontWeight = block.Heading > 0 ? FontWeight.SemiBold : FontWeight.Normal,
                MinHeight = 16,
                Inlines = new InlineCollection()
            };
            foreach (var span in block.Spans)
            {
                var run = new Run(span.Text)
                {
                    FontWeight = span.Bold || block.Heading > 0 ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = span.Italic ? FontStyle.Italic : FontStyle.Normal
                };
                if (span.Code) run.FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace");
                if (span.Strike) run.TextDecorations = TextDecorations.Strikethrough;
                else if (span.Link is not null) run.TextDecorations = TextDecorations.Underline;
                text.Inlines.Add(run);
            }
            var links = block.Spans.Where(span => span.Link is not null).Select(span => span.Link).Distinct().ToArray();
            if (links.Length != 0) ToolTip.SetTip(text, string.Join(Environment.NewLine, links));
            if (block.Checked is { } value)
            {
                var check = new CheckBox { IsChecked = value, IsEnabled = block.TaskOffset >= 0, Content = text, Tag = block.TaskOffset };
                check.IsCheckedChanged += (_, _) =>
                    TaskChanged?.Invoke(this, new CommentTaskChangedEventArgs(source, block.TaskOffset, check.IsChecked == true));
                _blocks.Children.Add(check);
            }
            else if (block.Prefix.Length != 0)
                _blocks.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Children = { new TextBlock { Text = block.Prefix }, InColumn(text, 1) }
                });
            else _blocks.Children.Add(text);
        }
    }

    private static Control InColumn(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
