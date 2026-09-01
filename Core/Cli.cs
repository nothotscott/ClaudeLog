using System.Runtime.InteropServices;

namespace ClaudeLog.Core;

/// <summary>
/// Headless commands. This is a WinExe, so the GUI can't be inspected from a script — these exist
/// so parsing and quota detection can be checked from a terminal, the way DevMem's --frame does
/// for its TUI.
/// </summary>
public static class Cli
{
    private static readonly string[] Commands =
        ["--parse", "--tree", "--quota", "--state", "--spell", "--selftest", "--help", "-h", "/?"];

    public static bool IsHeadless(string[] args) => args.Any(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        AttachToParentConsole();
        var settings = Settings.Load();

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "--parse" => Parse(args, settings),
                "--tree" => Tree(settings),
                "--quota" => Quota(settings),
                "--state" => State(),
                "--selftest" => SelfTest.Run(),
                "--spell" => Spell(args, settings),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            ClaudeLog — prompt log editor for Claude Code

              ClaudeLog                       launch the app
              ClaudeLog --tree                list projects, sessions and prompt counts
              ClaudeLog --parse <file>        show how a session file splits into prompts
                        [--legacy|--modern]   force a parse mode instead of the stored one
              ClaudeLog --quota               show the detected session-limit reset
              ClaudeLog --state               show where state and settings live
              ClaudeLog --spell <file>        words the spell checker would flag in a file
              ClaudeLog --selftest            check the parser and the save round-trip
            """);
        return 0;
    }

    private static int Parse(string[] args, Settings settings)
    {
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("usage: ClaudeLog --parse <file> [--legacy|--modern]");
            return 1;
        }

        path = Path.GetFullPath(path);
        var store = StateStore.Load();
        var key = Paths.RelativeKey(settings.LogRoot, path);

        var mode = args.Contains("--modern") ? ParseMode.Modern
            : args.Contains("--legacy") ? ParseMode.Legacy
            : ModeFor(store, key, path);

        var doc = SessionDocument.Load(path, mode);
        var lines = PromptParser.SplitLines(doc.Text);

        Console.WriteLine($"{path}");
        Console.WriteLine($"  mode {mode}   eol {(doc.Eol == "\n" ? "LF" : "CRLF")}   bom {doc.HasBom}   " +
                          $"lines {lines.Length}   prompts {doc.Prompts.Count}");
        Console.WriteLine();

        foreach (var prompt in doc.Prompts)
        {
            var state = store.PeekPrompt(key, prompt.Hash);
            Console.WriteLine($"  [{prompt.Index + 1,2}] lines {prompt.StartLine + 1}-{prompt.EndLine}  " +
                              $"{prompt.Hash}  {state?.Status ?? PromptStatus.Draft}");
            Console.WriteLine($"       {prompt.Preview}");
        }

        return 0;
    }

    private static int Tree(Settings settings)
    {
        var store = StateStore.Load();
        Console.WriteLine(settings.LogRoot);

        foreach (var project in LogTree.Scan(settings.LogRoot))
        {
            Console.WriteLine($"\n  {project.Name}/");
            foreach (var session in project.Sessions)
            {
                var mode = ModeFor(store, session.Key, session.Path);
                var count = "?";
                try
                {
                    count = SessionDocument.Load(session.Path, mode).Prompts.Count.ToString();
                }
                catch (Exception ex)
                {
                    Log.Warn(ex.Message);
                }

                Console.WriteLine($"    {session.Name,-40} {count,3} prompts  {mode,-6} {session.Modified:yyyy-MM-dd}");
            }

            foreach (var folder in project.Folders)
            {
                Console.WriteLine($"    {folder.Name + "/",-40} {folder.ItemCount,3} items    (attachments)");
            }
        }

        return 0;
    }

    /// <summary>
    /// What the spell checker would actually flag in a file, after filtering. The point is to see
    /// the false positives: anything listed here is a word the editor will squiggle.
    /// </summary>
    private static int Spell(string[] args, Settings settings)
    {
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("usage: ClaudeLog --spell <file>");
            return 1;
        }

        using var checker = new SpellChecker();
        if (!checker.Available)
        {
            Console.WriteLine("the Windows spell checker is not available");
            return 0;
        }

        var index = WordIndex.BuildFrom(settings.LogRoot);
        var pass = new SpellCheckPass(checker, index);
        var text = File.ReadAllText(Path.GetFullPath(path));
        var errors = pass.Run(text);

        Console.WriteLine($"{path}");
        Console.WriteLine($"  vocabulary {index.Count} words   flagged {errors.Count}");
        Console.WriteLine();

        foreach (var group in errors.Select(e => text.Substring(e.Start, e.Length))
                     .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count()))
        {
            var suggestion = pass.Suggest(group.Key).FirstOrDefault();
            Console.WriteLine($"  {group.Key,-24} x{group.Count(),-3} {(suggestion is null ? "" : "→ " + suggestion)}");
        }

        return 0;
    }

    /// <summary>Same rule the app uses: the stored mode, else Modern when the file already has `---`.</summary>
    private static ParseMode ModeFor(StateStore store, string key, string path)
    {
        var stored = store.PeekFileMode(key);
        if (stored is not null) return stored.Value;

        try
        {
            return PromptParser.LooksModern(File.ReadAllText(path)) ? ParseMode.Modern : ParseMode.Legacy;
        }
        catch
        {
            return ParseMode.Legacy;
        }
    }

    private static int Quota(Settings settings)
    {
        Console.WriteLine($"transcripts: {settings.ClaudeProjectsDir}");
        if (!Directory.Exists(settings.ClaudeProjectsDir))
        {
            Console.WriteLine("  directory not found — manual override only");
            return 0;
        }

        using var watcher = new QuotaWatcher(settings.ClaudeProjectsDir);
        var snapshot = watcher.Scan();

        if (snapshot is null)
        {
            Console.WriteLine("  no pending limit detected");
        }
        else
        {
            var remaining = snapshot.ResetsAt - DateTimeOffset.Now;
            Console.WriteLine($"  resets at {snapshot.ResetsAt.LocalDateTime:yyyy-MM-dd HH:mm} " +
                              $"(in {remaining.Hours}h {remaining.Minutes}m)");
            Console.WriteLine($"  type      {snapshot.RateLimitType}");
            Console.WriteLine($"  source    {snapshot.Source}");
        }

        var manual = StateStore.Load().State.ManualResetAt;
        if (manual is not null) Console.WriteLine($"  manual    {manual.Value.LocalDateTime:yyyy-MM-dd HH:mm}");
        return 0;
    }

    private static int State()
    {
        var store = StateStore.Load();
        Console.WriteLine($"settings  {Paths.SettingsFile}");
        Console.WriteLine($"state     {Paths.StateFile}");
        Console.WriteLine($"files     {store.State.Files.Count} tracked");
        Console.WriteLine($"queued    {store.State.Queue.Count}");
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    /// <summary>A WinExe has no console of its own; borrow the terminal's so output is visible.</summary>
    private static void AttachToParentConsole()
    {
        try
        {
            if (!AttachConsole(-1)) return;
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);
        }
        catch (Exception ex)
        {
            Log.Warn($"console attach failed: {ex.Message}");
        }
    }
}
