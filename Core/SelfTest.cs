using System.Text;

namespace ClaudeLog.Core;

/// <summary>
/// Checks the parts that must not regress silently: how prompts are split, and whether saving a
/// file gives back the exact bytes that went in. Run with `ClaudeLog --selftest`.
///
/// This exists instead of a test project for the same reason DevMem has `--frame`: the app is a
/// WinExe, so the behavior that matters has to be reachable from a terminal.
/// </summary>
public static class SelfTest
{
    private static int _failures;

    public static int Run()
    {
        _failures = 0;

        LegacySplitsOnProseBoundaries();
        LegacyKeepsListsAndFencesWithTheirPrompt();
        LegacyKeepsHeadedDocumentsWhole();
        ModernSplitsOnRules();
        ModernIgnoresRulesInsideFences();
        HashIgnoresTrailingWhitespace();
        SaveIsByteIdentical();
        SavePreservesCrlfAndAbsentBom();
        SaveDoesNotAccumulateTrailingBlankLines();
        SaveSurvivesAReaderHoldingTheFile();
        AppendUsesTheModeSeparator();
        ConvertToModernKeepsPromptCount();
        QuotaReadsARejectedRecord();
        QuotaIgnoresPastAndMalformedRecords();
        UsageParsesASessionAndWeeklyResponse();
        UsageIgnoresNullWindowsAndMalformedResponses();
        StateSurvivesAnEditAndAReparse();
        RenameCarriesFileState();
        SlugMatchesClaudeCodesProjectFolders();
        TranscriptPathFindsASessionOnDisk();
        TranscriptReadsTheLastPromptTime();
        SessionDirFallsBackFromProjectToDefaultToSource();
        ShellForFallsBackToTheDefault();
        SettingsNormalizeRestoresClaudeProjectsDir();
        SettingsCloneAndCopyCarryEveryField();
        TerminalSessionSurvivesAReload();
        SessionsAreSortedNewestFirst();
        SessionNamesAreValidated();
        MarkdownSpansCoverTheHighlightedShapes();
        CodeSpansCoverFencesInlineAndPaths();
        CodeLikeWordsAreRecognized();
        WordIndexCompletesFromTheCorpus();
        SpellCheckFindsTyposAndIgnoresCode();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "all checks passed" : $"{_failures} check(s) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- parsing

    private static void LegacySplitsOnProseBoundaries()
    {
        var text = "First prompt.\nStill the first.\n\nSecond prompt.\n\nThird prompt.";
        Equal(3, PromptParser.Parse(text, ParseMode.Legacy).Count, "blank line separates prose prompts");
    }

    private static void LegacyKeepsListsAndFencesWithTheirPrompt()
    {
        var text = string.Join("\n",
            "Here is the result of the script:",
            "",
            "```",
            "some output",
            "",
            "more output after a blank line",
            "```",
            "",
            "Next prompt entirely.");

        var prompts = PromptParser.Parse(text, ParseMode.Legacy);
        Equal(2, prompts.Count, "a fence stays with the line that introduces it");
        True(prompts[0].Text.Contains("more output"), "blank lines inside a fence are not boundaries");

        var listed = "Do these things:\n\n - one\n - two\n\nUnrelated follow-up.";
        Equal(2, PromptParser.Parse(listed, ParseMode.Legacy).Count, "a list stays with its lead-in");
    }

    private static void LegacyKeepsHeadedDocumentsWhole()
    {
        var text = string.Join("\n",
            "# Project Brief",
            "",
            "Some prose in the brief.",
            "",
            "More prose that is still the brief.",
            "",
            "",
            "This looks good, let's start.");

        var prompts = PromptParser.Parse(text, ParseMode.Legacy);
        Equal(2, prompts.Count, "a document with headings ends only at a double blank");
        True(prompts[0].Text.Contains("More prose"), "the whole document is one prompt");
    }

    private static void ModernSplitsOnRules()
    {
        var text = "One.\n\nStill one.\n\n---\n\nTwo.\n\n---\n\nThree.";
        var prompts = PromptParser.Parse(text, ParseMode.Modern);
        Equal(3, prompts.Count, "--- separates prompts in modern mode");
        True(prompts[0].Text.Contains("Still one"), "blank lines are not boundaries in modern mode");
    }

    private static void ModernIgnoresRulesInsideFences()
    {
        var text = "One.\n\n```\n---\n```\n\n---\n\nTwo.";
        Equal(2, PromptParser.Parse(text, ParseMode.Modern).Count, "--- inside a fence is not a separator");
    }

    private static void HashIgnoresTrailingWhitespace()
    {
        Equal(PromptParser.HashOf("a line\nanother"), PromptParser.HashOf("a line   \nanother\t"),
            "hash ignores trailing whitespace");
        NotEqual(PromptParser.HashOf("a line"), PromptParser.HashOf("a different line"),
            "hash distinguishes different text");
    }

    // -------------------------------------------------------------- saving

    private static void SaveIsByteIdentical()
    {
        var (path, original) = WriteSample("First prompt.\r\n\r\nSecond prompt.");
        try
        {
            var doc = SessionDocument.Load(path, ParseMode.Legacy);
            doc.ReplacePrompt(0, doc.Prompts[0].Text);
            doc.Save();

            True(File.ReadAllBytes(path).SequenceEqual(original), "rewriting a prompt unchanged is byte-identical");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void SavePreservesCrlfAndAbsentBom()
    {
        var (path, _) = WriteSample("First prompt.\r\n\r\nSecond prompt.");
        try
        {
            var doc = SessionDocument.Load(path, ParseMode.Legacy);
            doc.ReplacePrompt(1, "Edited second prompt.");
            doc.Save();

            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            True(bytes.Length < 3 || !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "no BOM is added");
            True(text.Contains("\r\n"), "CRLF is preserved");
            True(!text.Contains('\n') || !text.Replace("\r\n", "").Contains('\n'), "no bare LF is introduced");
            True(!text.EndsWith('\n'), "a missing trailing newline stays missing");
            True(text.Contains("Edited second prompt."), "the edit was written");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The editor holds the blank lines someone is in the middle of typing at the end of a prompt;
    /// the file must not collect one more of them on every Ctrl+S. Two saves of the same text is
    /// the shape that used to grow the file — the range spliced out is trimmed, the text spliced
    /// in was not.
    /// </summary>
    private static void SaveDoesNotAccumulateTrailingBlankLines()
    {
        var (path, _) = WriteSample("First prompt.\r\n\r\nSecond prompt.");
        try
        {
            var doc = SessionDocument.Load(path, ParseMode.Legacy);
            doc.ReplacePrompt(0, "First prompt.\n\n\n");
            var afterOne = doc.Text;

            doc.ReplacePrompt(0, "First prompt.\n\n\n");
            Equal(afterOne, doc.Text, "saving trailing blank lines twice doesn't grow the file");
            Equal(2, doc.Prompts.Count, "and doesn't split the prompt in two");
            Equal("First prompt.", doc.Prompts[0].Text, "the prompt keeps its own text");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The save that used to throw <c>"Unable to remove the file to be replaced"</c> out of a
    /// command with no try/catch around it. A handle that allows reading and writing but not
    /// deleting is exactly what Syncthing and the indexer hold, and it is what File.Replace can't
    /// get past — see <see cref="AtomicFile"/>.
    /// </summary>
    private static void SaveSurvivesAReaderHoldingTheFile()
    {
        var (path, _) = WriteSample("First prompt.\r\n\r\nSecond prompt.");
        try
        {
            var doc = SessionDocument.Load(path, ParseMode.Legacy);
            doc.ReplacePrompt(1, "Edited while held open.");

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                doc.Save();
            }

            var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".claudelog.tmp");
            True(File.ReadAllText(path).Contains("Edited while held open."), "a save gets past a reader on the file");
            True(!File.Exists(tmp), "and leaves no temp file behind");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AppendUsesTheModeSeparator()
    {
        var (path, _) = WriteSample("First prompt.");
        try
        {
            var doc = SessionDocument.Load(path, ParseMode.Modern);
            doc.AppendPrompt("Second prompt.");
            doc.Save();

            var text = File.ReadAllText(path);
            True(text.Contains("\r\n\r\n---\r\n\r\n"), "modern append writes a --- separator");
            Equal(2, SessionDocument.Load(path, ParseMode.Modern).Prompts.Count, "the appended prompt parses back");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ConvertToModernKeepsPromptCount()
    {
        var (path, _) = WriteSample("First prompt.\r\n\r\nSecond prompt.\r\n\r\nThird prompt.");
        try
        {
            var doc = SessionDocument.Load(path, ParseMode.Legacy);
            var before = doc.Prompts.Count;
            doc.ConvertToModern();
            doc.Save();

            var reloaded = SessionDocument.Load(path, ParseMode.Modern);
            Equal(before, reloaded.Prompts.Count, "converting to --- keeps the same prompts");
            True(File.ReadAllText(path).Contains("---"), "converted file has separators");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --------------------------------------------------------------- quota

    /// <summary>
    /// The record shape is copied from a real transcript. If Claude Code ever changes it, this is
    /// the check that fails, and the fix is here rather than in a user-visible countdown that
    /// quietly stops working.
    /// </summary>
    private static void QuotaReadsARejectedRecord()
    {
        var dir = TempDir();
        try
        {
            var resetsAt = DateTimeOffset.Now.AddHours(3).ToUnixTimeSeconds();
            WriteTranscript(dir, "session-a.jsonl", $$$"""
                {"type":"assistant","message":{"content":[{"type":"text","text":"You've hit your session limit"}]},"quotaLimits":{"status":"rejected","resetsAt":{{{resetsAt}}},"unifiedRateLimitFallbackAvailable":false,"rateLimitType":"five_hour","overageStatus":"rejected","upgradePaths":["upgrade_plan"],"isUsingOverage":false}}
                """);

            using var watcher = new QuotaWatcher(dir);
            var snapshot = watcher.Scan();

            True(snapshot is not null, "a rejected record is detected");
            Equal(resetsAt, snapshot?.ResetsAt.ToUnixTimeSeconds() ?? 0, "resetsAt is read as unix seconds");
            Equal("five_hour", snapshot?.RateLimitType, "rateLimitType is read");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void QuotaIgnoresPastAndMalformedRecords()
    {
        var dir = TempDir();
        try
        {
            var past = DateTimeOffset.Now.AddHours(-2).ToUnixTimeSeconds();
            WriteTranscript(dir, "old.jsonl",
                $$$"""{"quotaLimits":{"status":"rejected","resetsAt":{{{past}}},"rateLimitType":"five_hour"}}""");
            WriteTranscript(dir, "allowed.jsonl",
                $$$"""{"quotaLimits":{"status":"allowed","resetsAt":{{{DateTimeOffset.Now.AddHours(4).ToUnixTimeSeconds()}}}}}""");
            WriteTranscript(dir, "broken.jsonl", """{"quotaLimits":{"status":"rejected","resetsAt":}""");

            using var watcher = new QuotaWatcher(dir);
            True(watcher.Scan() is null, "past, allowed and malformed records are all ignored");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // --------------------------------------------------------------- usage

    /// <summary>
    /// Copied from a real call to GET /api/oauth/usage: utilization is a 0-100 percentage, not a
    /// fraction, and resets_at is an ISO-8601 string, not unix seconds like QuotaWatcher's field.
    /// If Anthropic ever reshapes this undocumented endpoint, this is what fails.
    /// </summary>
    private static void UsageParsesASessionAndWeeklyResponse()
    {
        const string json = """
            {"five_hour":{"utilization":50.0,"resets_at":"2026-09-01T22:49:59.570946+00:00","limit_dollars":null},
             "seven_day":{"utilization":5.0,"resets_at":"2026-09-08T12:59:59.570969+00:00","limit_dollars":null},
             "seven_day_opus":null,"seven_day_sonnet":null}
            """;

        var snapshot = UsageWatcher.Parse(json);

        True(snapshot is not null, "a normal response parses");
        Equal(50.0, snapshot?.SessionPercent ?? -1, "five_hour.utilization is the session percentage");
        Equal(5.0, snapshot?.WeeklyPercent ?? -1, "seven_day.utilization is the weekly percentage");
        True(snapshot?.SessionResetsAt.Year == 2026, "resets_at parses as a date, not unix seconds");
    }

    private static void UsageIgnoresNullWindowsAndMalformedResponses()
    {
        True(UsageWatcher.Parse("""{"five_hour":null,"seven_day":{"utilization":5.0,"resets_at":"2026-09-08T12:59:59Z"}}""")
            is null, "no session window at all means no usage to show, even with a weekly one present");

        var noWeekly = UsageWatcher.Parse("""{"five_hour":{"utilization":12.0,"resets_at":"2026-09-01T22:49:59Z"},"seven_day":null}""");
        True(noWeekly is not null, "a missing weekly window doesn't block the session percentage");
        True(noWeekly?.WeeklyPercent is null, "weekly stays absent rather than defaulting to 0");
    }

    // ------------------------------------------------------------ terminal

    /// <summary>
    /// The slug is how a known session id becomes a path on disk, and it is the one piece of the
    /// terminal integration that is pure guesswork about someone else's format. These are real
    /// directory names from %USERPROFILE%\.claude\projects on this machine.
    /// </summary>
    private static void SlugMatchesClaudeCodesProjectFolders()
    {
        Equal("D--Source", WinTerminal.SlugFor(@"D:\Source"), "slug for a drive root");
        Equal("D--Source-BrandBully", WinTerminal.SlugFor(@"D:\Source\BrandBully"), "slug for a project");
        Equal("C--Users-Scott", WinTerminal.SlugFor(@"C:\Users\Scott"), "slug for a profile directory");
        Equal("D--Source-repos-FileFixup", WinTerminal.SlugFor(@"D:\Source\repos\FileFixup"),
            "slug for a nested project");

        // A hyphen in a folder name is already the replacement character, so it survives unchanged.
        Equal("D--Source-proxmox-control", WinTerminal.SlugFor(@"D:\Source\proxmox-control"),
            "slug leaves an existing hyphen alone");

        // A trailing separator is not part of the name Claude Code sees.
        Equal(WinTerminal.SlugFor(@"D:\Source"), WinTerminal.SlugFor(@"D:\Source\"),
            "a trailing backslash doesn't change the slug");
    }

    private static void TranscriptPathFindsASessionOnDisk()
    {
        var path = WinTerminal.TranscriptPath(@"C:\p", @"D:\Source", "0434f382-4f95-47cf-b6fd-7d4ab748f378");
        Equal(Path.Combine(@"C:\p", "D--Source", "0434f382-4f95-47cf-b6fd-7d4ab748f378.jsonl"), path,
            "transcript path is projects/slug/session.jsonl");
    }

    /// <summary>
    /// Confirming a send means finding a user entry newer than the moment it was written. Only the
    /// timestamp is read — Claude Code reshapes the text it stores, so matching on content would
    /// report delivered prompts as missing.
    /// </summary>
    private static void TranscriptReadsTheLastPromptTime()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "session.jsonl");
            File.WriteAllLines(path, [
                """{"type":"user","timestamp":"2026-09-01T10:00:00.000Z","message":{"role":"user"}}""",
                """{"type":"assistant","timestamp":"2026-09-01T10:00:05.000Z"}""",
                """{"type":"user","timestamp":"2026-09-01T10:01:00.000Z","message":{"role":"user"}}""",
                """{"type":"assistant","timestamp":"2026-09-01T10:01:09.000Z"}""",
            ]);

            var last = SessionTranscript.LastPromptAt(path);
            Equal(DateTimeOffset.Parse("2026-09-01T10:01:00.000Z").UtcDateTime, last?.UtcDateTime,
                "the newest user entry is the one reported");

            True(SessionTranscript.LastPromptAt(Path.Combine(dir, "missing.jsonl")) is null,
                "a session with no transcript reads as unconfirmed, not as an error");

            File.WriteAllLines(path, ["not json at all", """{"type":"assistant","timestamp":"2026-09-01T10:00:00Z"}"""]);
            True(SessionTranscript.LastPromptAt(path) is null,
                "a transcript with no user entries reads as unconfirmed");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Where a session's Claude Code runs. The fallback order is the whole feature for anyone who
    /// hasn't configured anything: a fresh install runs in the project's own source folder, and
    /// one DefaultSessionDir moves every project to a shared root at once.
    /// </summary>
    private static void SessionDirFallsBackFromProjectToDefaultToSource()
    {
        var settings = new Settings
        {
            ProjectSources = { ["CallTree"] = @"D:\Source\repos\CallTree", ["DevMem"] = @"D:\Source\repos\DevMem" },
        };

        Equal(@"D:\Source\repos\CallTree", settings.SessionDirFor("CallTree"),
            "with nothing configured, a session runs in the project's source folder");
        Equal("", settings.SessionDirFor("Unknown"), "an unmapped project has no session directory");

        settings.DefaultSessionDir = @"D:\Source";
        Equal(@"D:\Source", settings.SessionDirFor("CallTree"),
            "a default session directory overrides the project source");
        Equal(@"D:\Source", settings.SessionDirFor("Unknown"),
            "the default also covers projects with no source mapping");

        settings.ProjectSessionDirs["CallTree"] = @"D:\Source\repos\CallTree";
        Equal(@"D:\Source\repos\CallTree", settings.SessionDirFor("CallTree"),
            "a project's own session directory wins over the default");
        Equal(@"D:\Source", settings.SessionDirFor("DevMem"),
            "and leaves the other projects on the default");
    }

    /// <summary>Same fallback shape as <see cref="SessionDirFor"/>, for which shell a session runs in.</summary>
    private static void ShellForFallsBackToTheDefault()
    {
        var settings = new Settings { DefaultShell = TerminalShell.PowerShell };

        Equal(TerminalShell.PowerShell, settings.ShellFor("BrandBully"), "with nothing configured, PowerShell");

        settings.DefaultShell = TerminalShell.GitBash;
        Equal(TerminalShell.GitBash, settings.ShellFor("BrandBully"), "changing the default changes every project");

        settings.ProjectShells["BrandBully"] = TerminalShell.PowerShell;
        Equal(TerminalShell.PowerShell, settings.ShellFor("BrandBully"), "a project's own shell wins over the default");
        Equal(TerminalShell.GitBash, settings.ShellFor("DevMem"), "and leaves the other projects on the default");
    }

    /// <summary>
    /// A `string` property is only non-null at compile time — System.Text.Json will still write a
    /// JSON `null` straight into it, which is exactly what a hand-trimmed "clean" settings.json can
    /// contain. Every terminal launch reads ClaudeProjectsDir, so a null here has to be repaired
    /// before it reaches <see cref="WinTerminal"/>, not discovered when a session fails to start.
    /// </summary>
    private static void SettingsNormalizeRestoresClaudeProjectsDir()
    {
        Equal(Paths.DefaultClaudeProjectsDir, Settings.Normalize(new Settings { ClaudeProjectsDir = null! }).ClaudeProjectsDir,
            "a null ClaudeProjectsDir falls back to the default");
        Equal(Paths.DefaultClaudeProjectsDir, Settings.Normalize(new Settings { ClaudeProjectsDir = "" }).ClaudeProjectsDir,
            "so does an empty one");

        const string custom = @"C:\custom\projects";
        Equal(custom, Settings.Normalize(new Settings { ClaudeProjectsDir = custom }).ClaudeProjectsDir,
            "a real value is left alone");
    }

    /// <summary>
    /// What the settings dialog is built on: it edits a detached copy, and Save puts that copy
    /// back into the instance the whole app is already holding. A field missed by either half is
    /// a setting that silently won't stick, which is the failure mode a dialog over a JSON file
    /// has to be proof against — hence CopyFrom reflecting over the type rather than listing it.
    /// </summary>
    private static void SettingsCloneAndCopyCarryEveryField()
    {
        var original = new Settings
        {
            LogRoot = @"C:\logs",
            SubmitDelayMs = 900,
            AutoSendOnReset = true,
            DefaultShell = TerminalShell.GitBash,
            NewFileMode = ParseMode.Legacy,
            ProjectSources = { ["CallTree"] = @"D:\Source\repos\CallTree" },
        };

        var copy = original.Clone();
        copy.LogRoot = @"C:\elsewhere";
        Equal(@"C:\logs", original.LogRoot, "the clone is detached — editing it doesn't touch the original");
        Equal(900, copy.SubmitDelayMs, "the clone carries the scalar settings");
        Equal(@"D:\Source\repos\CallTree", copy.ProjectSources["CallTree"], "and the maps");

        var live = new Settings();
        live.CopyFrom(copy);
        Equal(@"C:\elsewhere", live.LogRoot, "CopyFrom applies the edit");
        Equal(TerminalShell.GitBash, live.DefaultShell, "and the enums");
        Equal(ParseMode.Legacy, live.NewFileMode, "both of them");
        True(live.AutoSendOnReset, "and the flags");
        Equal(@"D:\Source\repos\CallTree", live.ProjectSources["CallTree"], "and the maps it never showed");
    }

    /// <summary>
    /// The session id and its directory are the two things that have to survive a restart: without
    /// them the app can neither resume the conversation nor find its transcript.
    /// </summary>
    private static void TerminalSessionSurvivesAReload()
    {
        var store = new StateStore();
        const string file = "ClaudeLog/claude_log.md";

        var state = store.ForFile(file);
        state.ClaudeSessionId = "0434f382-4f95-47cf-b6fd-7d4ab748f378";
        state.SessionDir = @"D:\Source";
        state.TerminalPid = 4242;

        store.RenameFile(file, "ClaudeLog/renamed.md");
        var moved = store.PeekFileMode("ClaudeLog/renamed.md");
        True(moved is not null, "a renamed file keeps its file state");
        Equal("0434f382-4f95-47cf-b6fd-7d4ab748f378", store.ForFile("ClaudeLog/renamed.md").ClaudeSessionId,
            "a rename carries the Claude session id with it");
        Equal(@"D:\Source", store.ForFile("ClaudeLog/renamed.md").SessionDir,
            "a rename carries the session directory with it");
    }

    // --------------------------------------------------------------- state

    /// <summary>
    /// The two ways per-prompt state gets lost if this is wrong: an edit changes a prompt's hash,
    /// and switching parse mode re-splits the file so every hash changes at once.
    /// </summary>
    private static void StateSurvivesAnEditAndAReparse()
    {
        var store = new StateStore();
        const string file = "CallTree/sms.md";

        store.Prompt(file, "aaaa").Status = PromptStatus.Sent;
        store.State.Queue.Add(new QueueEntry { File = file, Hash = "bbbb" });
        store.Prompt(file, "bbbb").Status = PromptStatus.Queued;
        store.Prompt(file, "cccc").Status = PromptStatus.Draft;

        store.Rekey(file, "aaaa", "dddd");
        Equal(PromptStatus.Sent, store.PeekPrompt(file, "dddd")?.Status, "an edited prompt keeps its status");
        True(store.PeekPrompt(file, "aaaa") is null, "the old hash is gone after a rekey");

        store.Rekey(file, "bbbb", "eeee");
        Equal("eeee", store.State.Queue[0].Hash, "the queue follows the rekey");

        // Nothing in this list is live — a mode switch looks exactly like this.
        store.Prune(file, []);
        Equal(PromptStatus.Sent, store.PeekPrompt(file, "dddd")?.Status, "prune keeps sent prompts");
        Equal(PromptStatus.Queued, store.PeekPrompt(file, "eeee")?.Status, "prune keeps queued prompts");
        True(store.PeekPrompt(file, "cccc") is null, "prune drops stale drafts");
    }

    /// <summary>
    /// Everything in state.json is keyed by relative path, so a rename that doesn't move the keys
    /// loses which prompts were sent and orphans anything queued out of the file.
    /// </summary>
    private static void RenameCarriesFileState()
    {
        var store = new StateStore();
        const string before = "CallTree/sms.md";
        const string after = "CallTree/messaging.md";

        store.ForFile(before).Mode = ParseMode.Modern;
        store.Prompt(before, "aaaa").Status = PromptStatus.Sent;
        store.State.Queue.Add(new QueueEntry { File = before, Hash = "bbbb" });
        store.State.LastSession = before;

        store.RenameFile(before, after);

        Equal(PromptStatus.Sent, store.PeekPrompt(after, "aaaa")?.Status, "prompt status follows a rename");
        Equal(ParseMode.Modern, store.PeekFileMode(after), "the parse mode follows a rename");
        True(store.PeekFileMode(before) is null, "the old key is gone after a rename");
        Equal(after, store.State.Queue[0].File, "queued prompts follow a rename");
        Equal(after, store.State.LastSession, "the last-open session follows a rename");
    }

    private static void SessionNamesAreValidated()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"claudelog-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var taken = Path.Combine(folder, "taken.md");
        File.WriteAllText(taken, "a prompt");

        Equal("sms.md", LogTree.NormalizeSessionName("sms"), "a bare name becomes .md");
        Equal("sms.txt", LogTree.NormalizeSessionName("sms.txt"), "an explicit .txt is kept");
        Equal("sms.md", LogTree.NormalizeSessionName("  sms  "), "surrounding space is trimmed");
        Equal("phase.2.md", LogTree.NormalizeSessionName("phase.2"), "a dot mid-name is name, not extension");

        True(LogTree.ValidateSessionName("new_session", folder) is null, "a free name is accepted");
        True(LogTree.ValidateSessionName("", folder) is not null, "an empty name is rejected");
        True(LogTree.ValidateSessionName("   ", folder) is not null, "a blank name is rejected");
        True(LogTree.ValidateSessionName("a/b", folder) is not null, "a path separator is rejected");
        True(LogTree.ValidateSessionName(".md", folder) is not null, "a bare extension is rejected");
        True(LogTree.ValidateSessionName("taken", folder) is not null, "an existing name is rejected");
        True(LogTree.ValidateSessionName("taken", folder, taken) is null, "renaming a file to itself is fine");

        Directory.Delete(folder, recursive: true);
    }

    // ------------------------------------------------- editing assistance

    // ------------------------------------------------------- tree and text

    private static void SessionsAreSortedNewestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), $"claudelog-selftest-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "Project");
        Directory.CreateDirectory(Path.Combine(project, "Plans"));

        // Alphabetical order would be the exact reverse of this.
        var stamp = new DateTime(2026, 1, 1, 9, 0, 0);
        foreach (var (name, days) in new[] { ("aaa.md", 0), ("mmm.txt", 5), ("zzz.md", 10) })
        {
            var path = Path.Combine(project, name);
            File.WriteAllText(path, "a prompt");
            File.SetLastWriteTime(path, stamp.AddDays(days));
        }

        var sessions = LogTree.Scan(root).Single().Sessions;

        Equal(3, sessions.Count, "only files directly in the project are sessions");
        Equal("zzz.md", sessions[0].Name, "the newest session sorts first");
        Equal("aaa.md", sessions[2].Name, "the oldest session sorts last");

        Directory.Delete(root, recursive: true);
    }

    private static void MarkdownSpansCoverTheHighlightedShapes()
    {
        var spans = new List<MarkdownSpan>();

        MarkdownScanner.ScanLine("- a bullet", false, spans);
        Equal(new MarkdownSpan(0, 1, MarkdownRole.Marker, true), spans.Single(), "a bullet marker is one span");

        spans.Clear();
        MarkdownScanner.ScanLine("  12. a numbered step", false, spans);
        Equal(new MarkdownSpan(2, 3, MarkdownRole.Marker, true), spans.Single(), "a numbered marker keeps its indent");

        spans.Clear();
        MarkdownScanner.ScanLine("run `dotnet build` now", false, spans);
        Equal(new MarkdownSpan(4, 14, MarkdownRole.Code, false), spans.Single(), "inline code spans its backticks");

        spans.Clear();
        MarkdownScanner.ScanLine("## Heading", false, spans);
        Equal(MarkdownRole.Heading, spans.Single().Role, "a heading colors the whole line");

        spans.Clear();
        MarkdownScanner.ScanLine("#hashtag not a heading", false, spans);
        Equal(0, spans.Count, "a bare hash is not a heading");

        spans.Clear();
        MarkdownScanner.ScanLine("- `x` and **y**", false, spans);
        Equal(3, spans.Count, "marker, code and bold coexist on one line");

        spans.Clear();
        MarkdownScanner.ScanLine("- not a bullet in here", true, spans);
        Equal(MarkdownRole.Code, spans.Single().Role, "a fenced line is code, whatever it contains");
    }

    private static void CodeSpansCoverFencesInlineAndPaths()
    {
        var text = "Run `dotnet build` first.\n\n```\nnot prose at all\n```\n\nSee C:\\Users\\Scott\\Documents and https://telnyx.com now.";
        var spans = TextScan.CodeAndPathSpans(text);

        True(Covered(spans, text.IndexOf("dotnet", StringComparison.Ordinal)), "inline code is covered");
        True(Covered(spans, text.IndexOf("not prose", StringComparison.Ordinal)), "fenced block is covered");
        True(Covered(spans, text.IndexOf("C:\\Users", StringComparison.Ordinal)), "windows path is covered");
        True(Covered(spans, text.IndexOf("https://", StringComparison.Ordinal)), "url is covered");
        True(!Covered(spans, text.IndexOf("first", StringComparison.Ordinal)), "prose is not covered");
    }

    private static bool Covered(List<TextSpan> spans, int offset) => spans.Any(s => s.Contains(offset));

    private static void CodeLikeWordsAreRecognized()
    {
        True(TextScan.LooksLikeCode("AIMediaSession"), "PascalCase is code");
        True(TextScan.LooksLikeCode("resetsAt"), "camelCase is code");
        True(TextScan.LooksLikeCode("net10"), "digits mean code");
        True(TextScan.LooksLikeCode("SIP"), "acronyms are code");
        True(!TextScan.LooksLikeCode("registration"), "an ordinary word is not code");
        True(!TextScan.LooksLikeCode("Telnyx"), "a capitalized name is not code — the word index decides that one");
    }

    private static void WordIndexCompletesFromTheCorpus()
    {
        var index = new WordIndex();
        index.Add("registration registration registration registry regicide unrelated");

        var matches = index.Matching("regis", new HashSet<string>(StringComparer.Ordinal), 5);
        Equal("registration", matches.FirstOrDefault(), "the most-used match comes first");
        True(matches.Contains("registry"), "other prefix matches are offered");
        True(!matches.Contains("unrelated"), "non-matches are excluded");

        True(index.Knows("registration"), "a repeated word is known vocabulary");
        True(!index.Knows("regicide"), "a word used once is not");

        var preferred = new HashSet<string>(StringComparer.Ordinal) { "registry" };
        Equal("registry", index.Matching("regis", preferred, 5).FirstOrDefault(),
            "a word from the current prompt outranks a more frequent one");
    }

    /// <summary>
    /// End to end against the real Windows spell checker: it must find plain typos, and the
    /// filtering must keep it off code and off Scott's own vocabulary.
    /// </summary>
    private static void SpellCheckFindsTyposAndIgnoresCode()
    {
        using var checker = new SpellChecker();
        if (!checker.Available)
        {
            Console.WriteLine("  skip  spell checker unavailable on this machine");
            return;
        }

        var index = new WordIndex();
        index.Add("Telnyx Telnyx Telnyx Proxmox Proxmox Proxmox");
        var pass = new SpellCheckPass(checker, index);

        var text = "This sentance has a typo. Telnyx and AIMediaSession do not, nor does `mispelled` in code.";
        var errors = pass.Run(text);
        var flagged = errors.Select(e => text.Substring(e.Start, e.Length)).ToList();

        True(flagged.Contains("sentance"), "a real typo is flagged");
        True(!flagged.Contains("Telnyx"), "a word from the logs is not flagged");
        True(!flagged.Contains("AIMediaSession"), "an identifier is not flagged");
        True(!flagged.Contains("mispelled"), "text inside backticks is not flagged");

        var suggestions = pass.Suggest("sentance");
        True(suggestions.Contains("sentence"), "suggestions include the obvious correction");

        var edges = "Telnyx's trunk in compose.yml on ghcr.io stays quiet, but hte typo does not.";
        var edgeFlags = pass.Run(edges).Select(e => edges.Substring(e.Start, e.Length)).ToList();

        True(!edgeFlags.Contains("Telnyx's"), "the possessive of a known word is not flagged");
        True(!edgeFlags.Any(w => w.Contains("compose") || w.Contains("yml")), "a filename is not flagged");
        True(!edgeFlags.Contains("ghcr"), "a domain fragment is not flagged");
        True(edgeFlags.Contains("hte"), "a typo among them is still flagged");
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"claudelog-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "D--Source"));
        return dir;
    }

    private static void WriteTranscript(string dir, string name, string line) =>
        File.WriteAllText(Path.Combine(dir, "D--Source", name), line + "\n");

    private static (string Path, byte[] Bytes) WriteSample(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"claudelog-selftest-{Guid.NewGuid():N}.md");
        var bytes = new UTF8Encoding(false).GetBytes(content);
        File.WriteAllBytes(path, bytes);
        return (path, bytes);
    }

    // ------------------------------------------------------------ asserts

    private static void True(bool condition, string what) => Report(condition, what, null, null);

    private static void Equal<T>(T expected, T actual, string what) =>
        Report(EqualityComparer<T>.Default.Equals(expected, actual), what, expected, actual);

    private static void NotEqual<T>(T unexpected, T actual, string what) =>
        Report(!EqualityComparer<T>.Default.Equals(unexpected, actual), what, unexpected, actual);

    private static void Report(bool ok, string what, object? expected, object? actual)
    {
        if (ok)
        {
            Console.WriteLine($"  ok    {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}" +
                          (expected is null ? "" : $"  (expected {expected}, got {actual})"));
    }
}
