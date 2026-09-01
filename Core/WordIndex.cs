namespace ClaudeLog.Core;

/// <summary>
/// The vocabulary of the logs themselves — every word Scott has already written across every
/// session, with counts. Two jobs:
///
/// - **Completion.** Notepad++ completes from words in the current document; this widens that to
///   the whole log tree, so `SIPSorcery`, `AIMediaSession` and `Telnyx` complete in a file that has
///   never mentioned them.
/// - **Silencing the spell checker.** A word Scott has used repeatedly is his vocabulary, not a
///   typo. Windows has never heard of `Avalonia` or `Proxmox`; he's written them dozens of times.
/// </summary>
public sealed class WordIndex
{
    /// <summary>Uses below this are probably typos, not vocabulary.</summary>
    private const int KnownThreshold = 3;

    private const int MinWordLength = 4;

    private readonly Dictionary<string, int> _forms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _words = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _forms.Count;

    public static WordIndex BuildFrom(string logRoot)
    {
        var index = new WordIndex();
        try
        {
            foreach (var project in LogTree.Scan(logRoot))
            {
                foreach (var session in project.Sessions)
                {
                    try
                    {
                        index.Add(File.ReadAllText(session.Path));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"word index: {session.Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"word index build failed: {ex.Message}");
        }

        Log.Info($"word index: {index.Count} distinct words");
        return index;
    }

    public void Add(string text)
    {
        foreach (var word in TextScan.Words(text, MinWordLength))
        {
            _forms[word] = _forms.GetValueOrDefault(word) + 1;
            _words[word] = _words.GetValueOrDefault(word) + 1;
        }
    }

    /// <summary>True when this is a word Scott uses, whatever Windows thinks of it.</summary>
    public bool Knows(string word) => _words.GetValueOrDefault(word) >= KnownThreshold;

    /// <summary>
    /// Completions for a prefix, words from the current document first — what you're writing about
    /// now beats what you wrote in June. A linear scan: tens of thousands of entries is well under a
    /// millisecond, and it runs once per keystroke.
    /// </summary>
    public List<string> Matching(string prefix, ISet<string> documentWords, int max)
    {
        if (prefix.Length == 0) return [];

        var matches = new List<(string Word, int Rank, int Count)>();
        foreach (var (word, count) in _forms)
        {
            if (word.Length <= prefix.Length) continue;
            if (!word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var exactCase = word.StartsWith(prefix, StringComparison.Ordinal);
            var rank = documentWords.Contains(word) ? 0 : 1;
            matches.Add((word, rank * 2 + (exactCase ? 0 : 1), count));
        }

        return matches
            .OrderBy(m => m.Rank)
            .ThenByDescending(m => m.Count)
            .ThenBy(m => m.Word.Length)
            .Select(m => m.Word)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }
}
