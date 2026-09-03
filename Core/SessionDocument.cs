using System.Text;

namespace ClaudeLog.Core;

/// <summary>
/// A session file in memory: its text, its prompts, and the byte-level traits that have to
/// survive a save. The log tree is Syncthing-synced and also opened by hand in Notepad++, so a
/// save that silently flips line endings or adds a BOM shows up as a whole-file change.
/// </summary>
public sealed class SessionDocument
{
    private SessionDocument(string path, string text, string eol, bool hasBom, bool trailingNewline, ParseMode mode)
    {
        Path = path;
        Eol = eol;
        HasBom = hasBom;
        TrailingNewline = trailingNewline;
        Mode = mode;
        Text = text;
        Prompts = PromptParser.Parse(text, mode);
    }

    public string Path { get; }
    public string Eol { get; }
    public bool HasBom { get; }
    public bool TrailingNewline { get; private set; }
    public ParseMode Mode { get; private set; }

    /// <summary>LF-normalized text. Line endings are restored from <see cref="Eol"/> on save.</summary>
    public string Text { get; private set; }

    public IReadOnlyList<Prompt> Prompts { get; private set; }

    public static SessionDocument Load(string path, ParseMode mode)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var raw = new UTF8Encoding(false).GetString(hasBom ? bytes.AsSpan(3) : bytes);

        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n') continue;
            if (i > 0 && raw[i - 1] == '\r') crlf++;
            else lf++;
        }

        var eol = lf > crlf ? "\n" : "\r\n";
        var trailing = raw.EndsWith('\n');
        var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        if (trailing) text = text[..^1];

        return new SessionDocument(path, text, eol, hasBom, trailing, mode);
    }

    public static SessionDocument CreateEmpty(string path, ParseMode mode) =>
        new(path, string.Empty, "\r\n", hasBom: false, trailingNewline: false, mode);

    public void SetMode(ParseMode mode)
    {
        Mode = mode;
        Prompts = PromptParser.Parse(Text, mode);
    }

    /// <summary>
    /// Replaces one prompt's lines in place. Splicing rather than re-serializing the whole file
    /// keeps every separator, indent and blank line the user put there by hand.
    /// </summary>
    public void ReplacePrompt(int index, string newText)
    {
        var prompt = Prompts[index];
        var lines = PromptParser.SplitLines(Text).ToList();
        var body = TrimBlankEnds(newText);

        lines.RemoveRange(prompt.StartLine, prompt.EndLine - prompt.StartLine);
        if (body.Length > 0) lines.InsertRange(prompt.StartLine, PromptParser.SplitLines(body));
        SetText(string.Join("\n", lines));
    }

    /// <summary>
    /// Drops blank lines from both ends of a replacement, because a prompt's line range never
    /// includes the ones around it — <see cref="PromptParser"/> trims them off when it emits the
    /// prompt. Without this, saving a prompt whose editor text ends in a blank line splices a
    /// trimmed range out and an untrimmed one back in, so every save adds one more blank line to
    /// the file. The editor keeps whatever the writer typed; the file gets the prompt.
    /// </summary>
    public static string TrimBlankEnds(string text)
    {
        var lines = PromptParser.SplitLines(text);
        var start = 0;
        var end = lines.Length;

        while (start < end && lines[start].Trim().Length == 0) start++;
        while (end > start && lines[end - 1].Trim().Length == 0) end--;

        return string.Join("\n", lines[start..end]);
    }

    public void DeletePrompt(int index)
    {
        var prompt = Prompts[index];
        var lines = PromptParser.SplitLines(Text).ToList();
        var end = prompt.EndLine;

        // Take the separator that follows with it, so deleting doesn't leave a stray `---`.
        while (end < lines.Count && lines[end].Trim().Length == 0) end++;
        if (Mode == ParseMode.Modern && end < lines.Count && PromptParser.IsRule(lines[end])) end++;
        if (end >= lines.Count) end = prompt.EndLine;

        lines.RemoveRange(prompt.StartLine, end - prompt.StartLine);
        SetText(string.Join("\n", lines).TrimEnd('\n'));
    }

    /// <summary>Appends a new prompt, writing the separator this file's mode calls for.</summary>
    public int AppendPrompt(string text)
    {
        var body = text.Replace("\r\n", "\n").Trim('\n');
        var sb = new StringBuilder(Text.TrimEnd('\n'));

        if (sb.Length > 0)
        {
            sb.Append('\n');
            if (Mode == ParseMode.Modern) sb.Append("\n---\n");
            sb.Append('\n');
        }

        sb.Append(body);
        SetText(sb.ToString());
        return Prompts.Count - 1;
    }

    /// <summary>Joins a prompt with the one after it — the fix when a pasted `---` split one prompt in two.</summary>
    public void MergeWithNext(int index)
    {
        if (index + 1 >= Prompts.Count) return;
        var a = Prompts[index];
        var b = Prompts[index + 1];
        var lines = PromptParser.SplitLines(Text).ToList();
        var merged = string.Join("\n", lines[a.StartLine..a.EndLine]) + "\n\n" +
                     string.Join("\n", lines[b.StartLine..b.EndLine]);
        lines.RemoveRange(a.StartLine, b.EndLine - a.StartLine);
        lines.InsertRange(a.StartLine, PromptParser.SplitLines(merged));
        SetText(string.Join("\n", lines));
    }

    /// <summary>Rewrites the file with explicit `---` separators and switches it to Modern.</summary>
    public void ConvertToModern()
    {
        var bodies = Prompts.Select(p => p.Text).ToList();
        Mode = ParseMode.Modern;
        SetText(string.Join("\n\n---\n\n", bodies));
    }

    public void SetText(string text)
    {
        Text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        Prompts = PromptParser.Parse(Text, Mode);
    }

    /// <summary>
    /// Atomic save: write a sibling temp file, then replace. Never truncate the real file in
    /// place — Syncthing and Notepad++ both watch it. The swap itself is
    /// <see cref="AtomicFile.Replace"/>, which is where the retries for a destination one of them
    /// has open live.
    /// </summary>
    public void Save()
    {
        var body = Text;
        if (TrailingNewline) body += "\n";
        if (Eol != "\n") body = body.Replace("\n", Eol);

        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var tmp = System.IO.Path.Combine(dir, "." + System.IO.Path.GetFileName(Path) + ".claudelog.tmp");

        File.WriteAllText(tmp, body, new UTF8Encoding(HasBom));
        AtomicFile.Replace(tmp, Path);
    }
}
