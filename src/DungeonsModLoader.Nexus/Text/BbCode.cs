using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DungeonsModLoader.Nexus.Text;

/// <summary>A run of a rendered BBCode document. The UI maps these to inlines; <see cref="BbCode.ToPlainText"/> flattens them.</summary>
public abstract record BbNode;

/// <summary>Text with inline formatting. <paramref name="HeadingLevel"/> 1 (largest) to 3 marks text from [size] tags, 0 is body text.</summary>
public sealed record BbText(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strikethrough = false, int HeadingLevel = 0, string? Url = null) : BbNode;

public sealed record BbLineBreak : BbNode;

/// <summary>Start of a list item: "• " or "1. " should be rendered before the following text.</summary>
public sealed record BbListItem(bool Ordered, int Index) : BbNode;

public sealed record BbImage(string Url) : BbNode;

public sealed record BbRule : BbNode;

/// <summary>
/// Renders the BBCode (with stray HTML) Nexus uses for mod descriptions as plain formatted text: bold, italic,
/// underline, links, headings from [size], lists, images as placeholders. Unknown tags are dropped, HTML entities
/// decoded and <c>&lt;br /&gt;</c> turned into line breaks. No embedded browser needed.
/// </summary>
public static partial class BbCode
{
    private const int MaxLength = 200_000;

    public static IReadOnlyList<BbNode> Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<BbNode>();
        }

        var text = input.Length > MaxLength ? input[..MaxLength] : input;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = HtmlBreak().Replace(text, "\n");
        text = HtmlTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        var parser = new Parser();
        parser.Run(text);
        return parser.Nodes;
    }

    /// <summary>Plain text with line breaks and list markers but no formatting (tooltips, search, tests).</summary>
    public static string ToPlainText(string? input)
    {
        var builder = new StringBuilder();
        foreach (var node in Parse(input))
        {
            switch (node)
            {
                case BbText t:
                    builder.Append(t.Text);
                    if (t.Url is not null && !t.Text.Contains(t.Url, StringComparison.OrdinalIgnoreCase) && !string.Equals(t.Text, t.Url, StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Append(" (").Append(t.Url).Append(')');
                    }

                    break;
                case BbLineBreak:
                    builder.Append('\n');
                    break;
                case BbListItem item:
                    builder.Append(item.Ordered ? $"{item.Index}. " : "• ");
                    break;
                case BbImage image:
                    builder.Append("[image: ").Append(image.Url).Append(']');
                    break;
                case BbRule:
                    builder.Append("────────");
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"</?(?:p|div|span|font|strong|em|b|i|u|a|img|ul|ol|li|h[1-6]|table|tr|td|th|center|hr|blockquote)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\[(/?)([a-zA-Z*]+)(?:=([^\]]*))?\]")]
    private static partial Regex Tag();

    private sealed class Parser
    {
        private readonly List<BbNode> _nodes = new();
        private readonly StringBuilder _text = new();
        private readonly Stack<string> _openTags = new();
        private readonly Stack<(bool Ordered, int Index)> _lists = new();
        private int _bold, _italic, _underline, _strike, _heading;
        private string? _url;
        private string? _pendingLinkUrl;
        private int _consecutiveBreaks;

        public IReadOnlyList<BbNode> Nodes => _nodes;

        public void Run(string text)
        {
            var position = 0;
            foreach (Match match in Tag().Matches(text))
            {
                if (match.Index < position)
                {
                    // Consumed by an [img] / [youtube] handler that read ahead to its closing tag.
                    continue;
                }

                AppendText(text.AsSpan(position, match.Index - position));
                position = match.Index + match.Length;

                var closing = match.Groups[1].Value == "/";
                var name = match.Groups[2].Value.ToLowerInvariant();
                var argument = match.Groups[3].Success ? match.Groups[3].Value.Trim().Trim('"', '\'') : null;

                if (name == "*")
                {
                    if (!closing)
                    {
                        ListItem();
                    }

                    continue;
                }

                if (closing)
                {
                    Close(name);
                }
                else
                {
                    Open(name, argument, text, ref position);
                }
            }

            AppendText(text.AsSpan(position));
            Flush();
            TrimTrailingBreaks();
        }

        private void Open(string name, string? argument, string text, ref int position)
        {
            switch (name)
            {
                case "b":
                    Flush();
                    _bold++;
                    break;
                case "i":
                    Flush();
                    _italic++;
                    break;
                case "u":
                    Flush();
                    _underline++;
                    break;
                case "s":
                    Flush();
                    _strike++;
                    break;
                case "size":
                    Flush();
                    _heading = HeadingFor(argument);
                    _openTags.Push("size");
                    return;
                case "url":
                    Flush();
                    _pendingLinkUrl = argument;
                    _url = argument ?? string.Empty;
                    break;
                case "img":
                {
                    // [img]url[/img] or [img=url]
                    Flush();
                    var url = argument;
                    if (url is null)
                    {
                        var end = text.IndexOf("[/img]", position, StringComparison.OrdinalIgnoreCase);
                        if (end >= 0)
                        {
                            url = text[position..end].Trim();
                            position = end + "[/img]".Length;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        _nodes.Add(new BbImage(url));
                    }

                    return;
                }

                case "youtube":
                {
                    Flush();
                    var end = text.IndexOf("[/youtube]", position, StringComparison.OrdinalIgnoreCase);
                    if (end >= 0)
                    {
                        var id = text[position..end].Trim();
                        position = end + "[/youtube]".Length;
                        var url = id.Contains("://", StringComparison.Ordinal) ? id : "https://www.youtube.com/watch?v=" + id;
                        _nodes.Add(new BbText("YouTube video", Url: url));
                    }

                    return;
                }

                case "list":
                    Flush();
                    Break(force: true);
                    _lists.Push((argument is not null && argument.Length > 0 && argument != "none", 0));
                    break;
                case "line":
                case "hr":
                    Flush();
                    Break(force: true);
                    _nodes.Add(new BbRule());
                    Break(force: true);
                    return;
                case "quote":
                    Flush();
                    Break(force: true);
                    _italic++;
                    break;
                case "spoiler":
                    Flush();
                    Break(force: true);
                    break;
                case "heading":
                    Flush();
                    _heading = 2;
                    break;
                default:
                    // color, center, left, right, font, code, email, ... : formatting we do not show; keep the text.
                    break;
            }

            _openTags.Push(name);
        }

        private void Close(string name)
        {
            Flush();
            switch (name)
            {
                case "b":
                    _bold = Math.Max(0, _bold - 1);
                    break;
                case "i":
                    _italic = Math.Max(0, _italic - 1);
                    break;
                case "u":
                    _underline = Math.Max(0, _underline - 1);
                    break;
                case "s":
                    _strike = Math.Max(0, _strike - 1);
                    break;
                case "size":
                case "heading":
                    _heading = 0;
                    break;
                case "url":
                    _url = null;
                    _pendingLinkUrl = null;
                    break;
                case "list":
                    if (_lists.Count > 0)
                    {
                        _lists.Pop();
                    }

                    Break(force: true);
                    break;
                case "quote":
                    _italic = Math.Max(0, _italic - 1);
                    Break(force: true);
                    break;
                case "spoiler":
                    Break(force: true);
                    break;
            }

            if (_openTags.Count > 0 && _openTags.Peek() == name)
            {
                _openTags.Pop();
            }
        }

        private void ListItem()
        {
            Flush();
            if (_lists.Count == 0)
            {
                _lists.Push((false, 0));
            }

            var (ordered, index) = _lists.Pop();
            index++;
            _lists.Push((ordered, index));
            Break(force: false);
            _nodes.Add(new BbListItem(ordered, index));
            _consecutiveBreaks = 0;
        }

        private void AppendText(ReadOnlySpan<char> span)
        {
            foreach (var ch in span)
            {
                if (ch == '\n')
                {
                    Flush();
                    Break(force: false);
                }
                else
                {
                    _text.Append(ch);
                }
            }
        }

        /// <summary>Emits a line break, collapsing runs of blank lines to one blank line.</summary>
        private void Break(bool force)
        {
            if (_consecutiveBreaks >= 2 && !force)
            {
                return;
            }

            if (force && _consecutiveBreaks >= 1 && _nodes.Count > 0 && _nodes[^1] is BbLineBreak)
            {
                return;
            }

            if (_nodes.Count == 0)
            {
                return;
            }

            _nodes.Add(new BbLineBreak());
            _consecutiveBreaks++;
        }

        private void Flush()
        {
            if (_text.Length == 0)
            {
                return;
            }

            var raw = _text.ToString();
            _text.Clear();
            var content = _consecutiveBreaks > 0 || _nodes.Count == 0 ? raw.TrimStart(' ', '\t') : raw;
            if (content.Length == 0)
            {
                return;
            }

            // [url]http://x[/url] has the address as its text; [url=http://x]text[/url] carries it in the argument.
            var url = _url is null ? null : (_pendingLinkUrl is { Length: > 0 } ? _pendingLinkUrl : content.Trim());
            _nodes.Add(new BbText(content, _bold > 0, _italic > 0, _underline > 0, _strike > 0, _heading, url));
            _consecutiveBreaks = 0;
        }

        private void TrimTrailingBreaks()
        {
            while (_nodes.Count > 0 && _nodes[^1] is BbLineBreak)
            {
                _nodes.RemoveAt(_nodes.Count - 1);
            }
        }

        private static int HeadingFor(string? sizeArgument)
        {
            if (!int.TryParse(sizeArgument, out var size))
            {
                return 0;
            }

            return size switch
            {
                >= 6 => 1,
                5 => 2,
                4 => 3,
                _ => 0,
            };
        }
    }
}
