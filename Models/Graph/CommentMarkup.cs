using System.Net;
using System.Text.RegularExpressions;

namespace LiberaFx.Models.Graph;

public enum CommentFormat { Text, Markdown, Html }
public sealed record CommentSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false,
    bool Strike = false, string? Link = null);
public sealed record CommentBlock(IReadOnlyList<CommentSpan> Spans, int Heading = 0, string Prefix = "",
    bool? Checked = null, int TaskOffset = -1);

public static class CommentMarkup
{
    private static readonly Regex Task = new(@"^\s*[-*+] \[(?<state>[ xX])\]\s?(?<text>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Inline = new(
        @"\*\*(?<bold>[^*\n]+)\*\*|__(?<bold>[^_\n]+)__|~~(?<strike>[^~\n]+)~~|`(?<code>[^`\n]+)`|\*(?<italic>[^*\n]+)\*|_(?<italic>[^_\n]+)_|\[(?<label>[^\]\n]+)\]\((?<url>[^)\n]+)\)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex HtmlTokens = new(@"<!--[\s\S]*?-->|<\s*(?<close>/?)(?<tag>[A-Za-z][A-Za-z0-9:-]*)(?<attrs>[^>]*)>|(?<text>[^<]+)|<",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "iframe", "object", "embed", "svg", "math", "head", "template" };

    public static IReadOnlyList<CommentBlock> Parse(string? source, CommentFormat format)
    {
        var text = source ?? "";
        if (text.Length > 4096) text = text[..4096];
        return format switch
        {
            CommentFormat.Markdown => Markdown(text),
            CommentFormat.Html => Html(text),
            _ => [new([new(text)])]
        };
    }

    public static string ToggleTask(string source, int offset, bool value)
    {
        if (offset <= 0 || offset + 1 >= source.Length || source[offset - 1] != '[' || source[offset + 1] != ']' ||
            source[offset] is not (' ' or 'x' or 'X'))
            throw new ArgumentException("The task marker no longer matches the comment.", nameof(offset));
        return string.Concat(source.AsSpan(0, offset), value ? "x" : " ", source.AsSpan(offset + 1));
    }

    private static IReadOnlyList<CommentBlock> Markdown(string source)
    {
        var blocks = new List<CommentBlock>();
        var offset = 0;
        var code = false;
        foreach (var raw in source.Split('\n'))
        {
            if (blocks.Count >= 256) { blocks.Add(new([new("\u2026")])); break; }
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                code = !code;
            else if (code)
                blocks.Add(new([new(line, Code: true)]));
            else if (Task.Match(line) is { Success: true } task)
                blocks.Add(new(ParseInline(task.Groups["text"].Value), Checked: task.Groups["state"].Value != " ",
                    TaskOffset: offset + task.Groups["state"].Index));
            else
            {
                var heading = 0;
                while (heading < line.Length && heading < 6 && line[heading] == '#') heading++;
                if (heading > 0 && heading < line.Length && line[heading] == ' ')
                    blocks.Add(new(ParseInline(line[(heading + 1)..]), Heading: heading));
                else
                {
                    var bullet = Regex.Match(line, @"^\s*(?:[-*+] |\d+[.)] )", RegexOptions.CultureInvariant);
                    var quote = line.StartsWith("> ", StringComparison.Ordinal);
                    blocks.Add(new(ParseInline(bullet.Success ? line[bullet.Length..] : quote ? line[2..] : line),
                        Prefix: bullet.Success ? "\u2022 " : quote ? "\u2502 " : ""));
                }
            }
            offset += raw.Length + 1;
        }
        return blocks;
    }

    private static IReadOnlyList<CommentSpan> ParseInline(string line)
    {
        var spans = new List<CommentSpan>();
        var offset = 0;
        foreach (Match match in Inline.Matches(line))
        {
            if (match.Index > offset) spans.Add(new(line[offset..match.Index]));
            if (match.Groups["label"].Success)
                spans.Add(new(match.Groups["label"].Value, Link: SafeLink(match.Groups["url"].Value)));
            else
            {
                var group = new[] { "bold", "italic", "code", "strike" }.First(name => match.Groups[name].Success);
                spans.Add(new(match.Groups[group].Value, Bold: group == "bold", Italic: group == "italic",
                    Code: group == "code", Strike: group == "strike"));
            }
            offset = match.Index + match.Length;
        }
        if (offset < line.Length || spans.Count == 0) spans.Add(new(line[offset..]));
        return spans;
    }

    private static IReadOnlyList<CommentBlock> Html(string source)
    {
        var blocks = new List<CommentBlock>();
        var spans = new List<CommentSpan>();
        var suppressed = new Stack<string>();
        var bold = 0;
        var italic = 0;
        var code = 0;
        var strike = 0;
        var heading = 0;
        var prefix = "";
        bool? check = null;
        string? link = null;
        void Flush()
        {
            if (spans.Count != 0 || check.HasValue)
                blocks.Add(new(spans.ToArray(), heading, prefix, check));
            spans.Clear();
            check = null;
            prefix = "";
        }
        foreach (Match token in HtmlTokens.Matches(source))
        {
            if (blocks.Count >= 256) { blocks.Add(new([new("\u2026")])); break; }
            if (token.Groups["tag"].Success)
            {
                var tag = token.Groups["tag"].Value.ToLowerInvariant();
                var close = token.Groups["close"].Value.Length != 0;
                if (Blocked.Contains(tag))
                {
                    if (!close) suppressed.Push(tag);
                    else if (suppressed.TryPeek(out var current) && current == tag) suppressed.Pop();
                    continue;
                }
                if (suppressed.Count != 0) continue;
                var delta = close ? -1 : 1;
                switch (tag)
                {
                    case "strong": case "b": bold = Math.Max(0, bold + delta); break;
                    case "em": case "i": italic = Math.Max(0, italic + delta); break;
                    case "code": case "pre": code = Math.Max(0, code + delta); break;
                    case "s": case "del": strike = Math.Max(0, strike + delta); break;
                    case "a": link = close ? null : SafeLink(Attribute(token.Groups["attrs"].Value, "href")); break;
                    case "br": Flush(); blocks.Add(new([new("")])); break;
                    case "p": case "div": case "ul": case "ol": case "blockquote":
                        Flush(); break;
                    case "li":
                        Flush();
                        if (!close) prefix = "\u2022 ";
                        break;
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        Flush(); heading = close ? 0 : tag[1] - '0'; break;
                    case "input" when !close:
                        if (string.Equals(Attribute(token.Groups["attrs"].Value, "type"), "checkbox", StringComparison.OrdinalIgnoreCase))
                            check = Regex.IsMatch(token.Groups["attrs"].Value, @"(?:^|\s)checked(?:\s|=|/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        break;
                }
            }
            else if (suppressed.Count == 0 && token.Groups["text"].Success)
            {
                var value = WebUtility.HtmlDecode(token.Groups["text"].Value);
                if (code == 0) value = Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant);
                if (!string.IsNullOrWhiteSpace(value) || spans.Count != 0)
                    spans.Add(new(value, bold > 0, italic > 0, code > 0, strike > 0, link));
            }
        }
        Flush();
        return blocks;
    }

    private static string? Attribute(string attributes, string name)
    {
        var match = Regex.Match(attributes, "(?:^|\\s)" + name + "\\s*=\\s*(?:\"(?<v>[^\"]*)\"|'(?<v>[^']*)'|(?<v>[^\\s'\"/>]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value) : null;
    }

    private static string? SafeLink(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" ? value : null;
}
