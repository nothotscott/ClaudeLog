namespace ClaudeLog.Core;

/// <summary>
/// One spell-check run over a prompt, with the filtering that makes it usable on Scott's prompts.
///
/// Windows flags anything it doesn't know, and these prompts are full of pasted SIP traces, JSON,
/// file paths, identifiers and product names. Unfiltered, the editor would be a wall of red and
/// worth nothing. Three filters, in order of how much they remove:
///
/// 1. anything inside a fence, an inline code span, a URL or a Windows path;
/// 2. anything that looks like code — digits, underscores, camelCase, acronyms;
/// 3. anything Scott has used at least a few times anywhere in the logs (<see cref="WordIndex"/>).
/// </summary>
public sealed class SpellCheckPass(SpellChecker checker, WordIndex index)
{
    public bool Available => checker.Available;

    public List<SpellingError> Run(string text)
    {
        var kept = new List<SpellingError>();
        if (text.Length == 0) return kept;

        var skip = TextScan.CodeAndPathSpans(text);
        var skipIndex = 0;

        foreach (var error in checker.Check(text))
        {
            // Both lists are in ascending offset order, so the skip list only moves forward.
            while (skipIndex < skip.Count && skip[skipIndex].End <= error.Start) skipIndex++;
            if (skipIndex < skip.Count && skip[skipIndex].Contains(error.Start)) continue;

            if (error.Start + error.Length > text.Length) continue;
            var word = text.Substring(error.Start, error.Length);

            if (TextScan.LooksLikeCode(word)) continue;
            if (TouchesSeparator(text, error)) continue;
            if (index.Knows(Base(word))) continue;

            kept.Add(error);
        }

        return kept;
    }

    /// <summary>
    /// A word touching or containing a dot, slash or at-sign is part of a filename, domain or path
    /// — `compose.yml`, `ghcr.io`, `voip.ms`. The code-span pass only catches paths that start a
    /// token, and Windows sometimes reports the whole dotted name as one error and sometimes only
    /// the half it doesn't recognize, so both the range and its neighbours have to be checked.
    /// </summary>
    private static bool TouchesSeparator(string text, SpellingError error)
    {
        foreach (var c in text.AsSpan(error.Start, error.Length))
        {
            if (c is '.' or '/' or '\\' or '@') return true;
        }

        var before = error.Start > 0 ? text[error.Start - 1] : ' ';
        var afterIndex = error.Start + error.Length;
        var after = afterIndex < text.Length ? text[afterIndex] : ' ';

        return before is '.' or '/' or '\\' or '@' || after is '.' or '/' or '\\' or '@';
    }

    /// <summary>`Telnyx's` is the same vocabulary word as `Telnyx`.</summary>
    private static string Base(string word) =>
        word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ? word[..^2] :
        word.EndsWith('\'') ? word[..^1] : word;

    public List<string> Suggest(string word) => checker.Suggest(word);

    public void AddToDictionary(string word)
    {
        checker.Add(word);
        index.Add($"{word} {word} {word}"); // known immediately, without a rebuild
    }
}
