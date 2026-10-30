using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace Horizon.Station.UI.Views;

/// <summary>
/// Fold regions for the <c>.moon</c> (Lua) editor — view-only, never changes the script text.
/// Folds Lua blocks (<c>function / if / for / while / do … end</c>, <c>repeat … until</c>),
/// multi-line <c>--[[ ]]</c> comments, runs of 3+ <c>--</c> comment lines, and named
/// <c>--#group Name</c> … <c>--#endgroup</c> regions. Keywords inside strings and comments are
/// skipped; an unbalanced block (mid-edit) simply doesn't fold.
/// </summary>
public static class LuaFolding
{
    private const int MinCommentRun = 3;
    private const string GroupTag = "#group";
    private const string EndGroupTag = "#endgroup";

    private readonly record struct Open(string Keyword, int Offset);
    private readonly record struct Group(string Name, int Offset);

    /// <summary>All fold regions of <paramref name="doc"/>, sorted by start offset.</summary>
    public static List<NewFolding> Create(TextDocument doc)
    {
        var folds = new List<NewFolding>();
        ScanBlocks(doc, doc.Text, folds);
        ScanCommentRuns(doc, folds);
        folds.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return folds;
    }

    private static void ScanBlocks(TextDocument doc, string text, List<NewFolding> folds)
    {
        var blocks = new Stack<Open>();
        var groups = new Stack<Group>();
        int pendingDo = 0;   // 'for' / 'while' headers whose 'do' belongs to them, not a new block
        int n = text.Length;
        int i = 0;
        while (i < n)
        {
            char c = text[i];
            if (c == '-' && i + 1 < n && text[i + 1] == '-')
            {
                i = SkipComment(doc, text, i, groups, folds);
            }
            else if (c == '"' || c == '\'')
            {
                i = SkipQuoted(text, i);
            }
            else if (c == '[' && LongBracketLevel(text, i) >= 0)
            {
                i = SkipLong(text, i, LongBracketLevel(text, i));
            }
            else if (IsIdentChar(c))
            {
                int start = i;
                while (i < n && IsIdentChar(text[i])) i++;
                if (!char.IsDigit(c))
                    pendingDo = OnWord(doc, text.Substring(start, i - start), start, i, blocks, pendingDo, folds);
            }
            else
            {
                i++;
            }
        }
    }

    // Tracks block keywords; returns the updated pending-'do' count.
    private static int OnWord(TextDocument doc, string word, int start, int end,
                              Stack<Open> blocks, int pendingDo, List<NewFolding> folds)
    {
        switch (word)
        {
            case "function":
            case "if":
            case "repeat":
                blocks.Push(new Open(word, start));
                break;
            case "for":
            case "while":
                blocks.Push(new Open(word, start));
                pendingDo++;
                break;
            case "do":
                if (pendingDo > 0) pendingDo--;
                else blocks.Push(new Open(word, start));
                break;
            case "end":
                if (blocks.Count > 0) AddBlock(doc, blocks.Pop().Offset, end, "... end", folds);
                break;
            case "until":
                // keep the 'until <cond>' line visible: fold up to the end of the line before it
                if (blocks.Count > 0)
                {
                    var prev = doc.GetLineByOffset(start).PreviousLine;
                    if (prev != null) AddBlock(doc, blocks.Pop().Offset, prev.EndOffset, "...", folds);
                }
                break;
        }
        return pendingDo;
    }

    // Folds from the end of the opener's line (so its header stays visible) to endOffset.
    private static void AddBlock(TextDocument doc, int openOffset, int endOffset, string name, List<NewFolding> folds)
    {
        var openLine = doc.GetLineByOffset(openOffset);
        if (endOffset <= openLine.EndOffset) return;   // single line
        folds.Add(new NewFolding(openLine.EndOffset, endOffset) { Name = name });
    }

    // At "--": skips a line or long comment, recording group markers and multi-line long comments.
    private static int SkipComment(TextDocument doc, string text, int i, Stack<Group> groups, List<NewFolding> folds)
    {
        int body = i + 2;
        if (body < text.Length && text[body] == '[' && LongBracketLevel(text, body) >= 0)
        {
            int end = SkipLong(text, body, LongBracketLevel(text, body));
            if (doc.GetLineByOffset(Math.Min(end, text.Length)).LineNumber > doc.GetLineByOffset(i).LineNumber)
                folds.Add(new NewFolding(i, end) { Name = "--[[ ... ]]" });
            return end;
        }

        var line = doc.GetLineByOffset(i);
        var content = text.Substring(body, line.EndOffset - body).Trim();
        if (content.StartsWith(EndGroupTag, StringComparison.OrdinalIgnoreCase))
        {
            if (groups.Count > 0)
            {
                var g = groups.Pop();
                if (line.EndOffset > doc.GetLineByOffset(g.Offset).EndOffset)
                    folds.Add(new NewFolding(g.Offset, line.EndOffset) { Name = g.Name });
            }
        }
        else if (content.StartsWith(GroupTag, StringComparison.OrdinalIgnoreCase))
        {
            var name = content[GroupTag.Length..].Trim();
            groups.Push(new Group(name.Length > 0 ? name : "group", i));
        }
        return line.EndOffset;
    }

    // Runs of MinCommentRun+ whole-line "--" comments (e.g. a script's header notes).
    private static void ScanCommentRuns(TextDocument doc, List<NewFolding> folds)
    {
        DocumentLine? first = null, last = null;
        int count = 0;
        foreach (var line in doc.Lines)
        {
            var t = doc.GetText(line).TrimStart();
            bool comment = t.StartsWith("--", StringComparison.Ordinal)
                        && !t.StartsWith("--[", StringComparison.Ordinal)
                        && !t.StartsWith("--#", StringComparison.Ordinal);
            if (comment)
            {
                first ??= line;
                last = line;
                count++;
                continue;
            }
            AddCommentRun(first, last, count, folds);
            first = last = null;
            count = 0;
        }
        AddCommentRun(first, last, count, folds);
    }

    private static void AddCommentRun(DocumentLine? first, DocumentLine? last, int count, List<NewFolding> folds)
    {
        if (first == null || last == null || count < MinCommentRun) return;
        folds.Add(new NewFolding(first.EndOffset, last.EndOffset) { Name = "--  ..." });
    }

    // At a quote: returns the offset just past the closing quote (or the line end if unterminated).
    private static int SkipQuoted(string text, int i)
    {
        char q = text[i];
        int j = i + 1;
        while (j < text.Length)
        {
            char c = text[j];
            if (c == '\\') j += 2;
            else if (c == q) return j + 1;
            else if (c == '\n') return j;
            else j++;
        }
        return text.Length;
    }

    // "[[" → 0, "[==[" → 2; -1 if the '[' at i doesn't open a long bracket.
    private static int LongBracketLevel(string text, int i)
    {
        int j = i + 1;
        while (j < text.Length && text[j] == '=') j++;
        return j < text.Length && text[j] == '[' ? j - i - 1 : -1;
    }

    private static int SkipLong(string text, int i, int level)
    {
        var close = "]" + new string('=', level) + "]";
        int idx = text.IndexOf(close, i + level + 2, StringComparison.Ordinal);
        return idx < 0 ? text.Length : idx + close.Length;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
