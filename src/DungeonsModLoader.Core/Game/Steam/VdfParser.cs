using System.Text;

namespace DungeonsModLoader.Core.Game.Steam;

/// <summary>
/// One node of a parsed Valve KeyValues document (<c>libraryfolders.vdf</c>, <c>appmanifest_*.acf</c>):
/// either a value node (<see cref="Value"/> set) or a block node (<see cref="Children"/>, <see cref="Value"/> null).
/// Lookups are case-insensitive and return the first match; duplicate keys are preserved in <see cref="Children"/>.
/// </summary>
public sealed class VdfNode
{
    /// <summary>Creates a value node.</summary>
    public VdfNode(string name, string value)
    {
        Name = name;
        Value = value;
        Children = Array.Empty<VdfNode>();
    }

    /// <summary>Creates a block node.</summary>
    public VdfNode(string name, IReadOnlyList<VdfNode> children)
    {
        Name = name;
        Children = children;
    }

    public string Name { get; }

    /// <summary>The string value, or <c>null</c> for a block.</summary>
    public string? Value { get; }

    public IReadOnlyList<VdfNode> Children { get; }

    public bool IsBlock => Value is null;

    /// <summary>First child with the given name (case-insensitive), or <c>null</c>.</summary>
    public VdfNode? this[string name] => Find(name);

    /// <summary>First child with the given name (case-insensitive), or <c>null</c>.</summary>
    public VdfNode? Find(string name)
    {
        foreach (var child in Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Every child with the given name (case-insensitive), in document order.</summary>
    public IEnumerable<VdfNode> FindAll(string name)
        => Children.Where(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Value of the first child with the given name, or <c>null</c> when missing or a block.</summary>
    public string? GetValue(string name) => Find(name)?.Value;

    public override string ToString() => IsBlock ? $"{Name} {{ {Children.Count} children }}" : $"{Name} = {Value}";
}

/// <summary>
/// A small, tolerant parser for Valve's KeyValues text format. Handles quoted and bare tokens, nested
/// <c>{ }</c> blocks, <c>\"</c> / <c>\\</c> / <c>\n</c> / <c>\t</c> escapes inside quotes, <c>//</c> line comments,
/// <c>#include</c>/<c>#base</c> directives and <c>[$CONDITION]</c> tags (both ignored), CRLF/LF line endings and a
/// leading UTF-8 BOM. Malformed input never throws: unbalanced braces are closed at end of input and a dangling
/// key is dropped.
/// </summary>
public static class VdfParser
{
    /// <summary>Parses the document into a synthetic root block whose children are the top-level keys.</summary>
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokenizer = new Tokenizer(text);
        return new VdfNode(string.Empty, ParseBlock(tokenizer, isRoot: true));
    }

    /// <summary>Reads and parses the file (any BOM is handled by the text reader).</summary>
    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    private static List<VdfNode> ParseBlock(Tokenizer tokenizer, bool isRoot)
    {
        var children = new List<VdfNode>();
        while (true)
        {
            var token = tokenizer.Next();
            switch (token.Kind)
            {
                case TokenKind.End:
                    return children;

                case TokenKind.Close:
                    if (isRoot)
                    {
                        continue; // stray closing brace at top level: ignore
                    }

                    return children;

                case TokenKind.Open:
                    // A block without a key: parse and drop it so the rest of the document still loads.
                    ParseBlock(tokenizer, isRoot: false);
                    continue;

                case TokenKind.String:
                    if (!token.Quoted && token.Text.StartsWith('#'))
                    {
                        // #include "file" / #base "file": skip the directive and its argument.
                        tokenizer.Next();
                        continue;
                    }

                    if (!token.Quoted && token.Text.StartsWith('['))
                    {
                        continue; // [$WIN32]-style conditional: ignore
                    }

                    var key = token.Text;
                    var next = tokenizer.Next();
                    switch (next.Kind)
                    {
                        case TokenKind.Open:
                            children.Add(new VdfNode(key, ParseBlock(tokenizer, isRoot: false)));
                            break;
                        case TokenKind.String:
                            children.Add(new VdfNode(key, next.Text));
                            break;
                        case TokenKind.Close:
                            // Dangling key right before the end of a block: drop it, close the block.
                            if (isRoot)
                            {
                                continue;
                            }

                            return children;
                        case TokenKind.End:
                            return children;
                    }

                    break;
            }
        }
    }

    private enum TokenKind
    {
        String,
        Open,
        Close,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, bool Quoted)
    {
        public static readonly Token OpenBrace = new(TokenKind.Open, "{", false);
        public static readonly Token CloseBrace = new(TokenKind.Close, "}", false);
        public static readonly Token EndOfInput = new(TokenKind.End, string.Empty, false);
    }

    private sealed class Tokenizer
    {
        private readonly string _text;
        private int _position;

        public Tokenizer(string text)
        {
            _text = text;
            _position = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        }

        public Token Next()
        {
            while (true)
            {
                SkipWhitespace();
                if (_position >= _text.Length)
                {
                    return Token.EndOfInput;
                }

                var c = _text[_position];
                if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    SkipLine();
                    continue;
                }

                switch (c)
                {
                    case '{':
                        _position++;
                        return Token.OpenBrace;
                    case '}':
                        _position++;
                        return Token.CloseBrace;
                    case '"':
                        return ReadQuoted();
                    default:
                        return ReadBare();
                }
            }
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        private void SkipLine()
        {
            while (_position < _text.Length && _text[_position] != '\n')
            {
                _position++;
            }
        }

        private Token ReadQuoted()
        {
            _position++; // opening quote
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (c == '\\' && _position + 1 < _text.Length)
                {
                    var escaped = _text[_position + 1];
                    switch (escaped)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        default:
                            // Unknown escape: keep it verbatim (Valve does the same).
                            builder.Append('\\').Append(escaped);
                            break;
                    }

                    _position += 2;
                    continue;
                }

                if (c == '"')
                {
                    _position++;
                    break;
                }

                builder.Append(c);
                _position++;
            }

            return new Token(TokenKind.String, builder.ToString(), Quoted: true);
        }

        private Token ReadBare()
        {
            var start = _position;
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"')
                {
                    break;
                }

                if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    break;
                }

                _position++;
            }

            return new Token(TokenKind.String, _text[start.._position], Quoted: false);
        }
    }
}
