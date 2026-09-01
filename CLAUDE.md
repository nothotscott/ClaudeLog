# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`README.md` is the user-facing documentation. This file is the working rules: commands, the format
contracts, and the decisions that are easy to regress. Most of the constraints here come from data
that lives outside this repo — read the two "external" sections before changing anything that reads
or writes files.

Deeper write-ups for specific subsystems live under `skills/<Name>/SKILL.md` — this file links to
them from the relevant section rather than inlining everything. Read the linked skill before
changing the area it covers; this file stays the map, not the territory.

## Project Purpose

ClaudeLog streamlines the way Scott uses Claude Code on Windows.

The workflow it replaces: Claude Code runs in Windows Terminal, prompts get written in Notepad++
and saved as markdown, one file per session, grouped by project, under
`C:\Users\Scott\Documents\ClaudeLog`. Each prompt is its own section. When a prompt is finished it
gets copied and pasted into the terminal. Responses aren't saved.

Two things made that tedious, and they are what the app exists to fix:

- **Copying one section at a time.** Notepad++ has no notion of "this paragraph is one prompt", so
  every send is a manual select-to-the-next-blank-line.
- **Session limits.** Hitting the five-hour limit means the next prompt is written but unsendable,
  with nothing tracking when it can go. Prompts got lost or re-sent from memory.

So ClaudeLog is a markdown editor whose unit is the *prompt*: browse projects and sessions, write,
send with one click into the session file's own Claude Code conversation, queue what can't be sent
yet, and get told the moment the limit resets.

The log tree is the product of a year of use and Scott likes it. **The app conforms to the tree;
the tree is never reorganized to suit the app.**

## Commands

```powershell
dotnet build
dotnet run                        # the app
dotnet build -c Release

# headless — the app is a WinExe, so this is how behavior gets checked from a terminal
dotnet run -- --selftest          # parser, saving, quota format, state, spelling
dotnet run -- --spell <file>      # what the spell checker would flag in a file, after filtering
dotnet run -- --tree              # projects, sessions, prompt counts, parse mode per file
dotnet run -- --parse <file>      # prompt boundaries, hashes and statuses (--legacy / --modern)
dotnet run -- --quota             # the detected session-limit reset
dotnet run -- --usage             # the live session/weekly usage percentage
dotnet run -- --state             # where settings and state live
dotnet run -- --terminal          # each file's Claude session: directory, pid, last prompt
dotnet run -- --terminal --start <dir>   # open one there; prints session id and pid
dotnet run -- --send <pid> <text>        # write one prompt into that terminal
dotnet run -- --startup           # boot Avalonia and load MainWindow without showing it, then exit
```

There is no test project. **`--selftest` is the regression net** — 102 checks over prompt splitting,
byte-exact saving, the quota record format, the state-store invariants, tree ordering and naming,
the highlighting rules, the spelling filters (including a real round-trip through the Windows
spell checker). Add to `SelfTest.cs` when changing any of those; it is far more useful than it
looks, and it is how the parser rules below were validated against the real corpus.

`--parse` against a real file is the fastest way to see whether a parsing change helped or hurt.
Useful reference points, all with the current rules:

| File | Prompts | Why it's the interesting case |
|---|---|---|
| `CallTree\call_tree.md` | 36 | 540 lines: one 66-line briefing document, then ~35 conversational prompts with pasted SIP logs |
| `BrandBully\ai_image_feature.txt` | 37 | Numbered lists and colon-introduced blocks throughout |
| `ClaudeLog\claude_log.md` | 2 | Already uses `---`, so it opens in Modern mode automatically |
| `DevMem\dev_mem_continued.md` | 3 | Three short prompts separated by single blank lines |

## Decisions already made

Settled with Scott before any code was written. Don't relitigate without asking.

| Decision | Choice | Why |
|---|---|---|
| Form factor | Avalonia desktop app, three panes | Replaces Notepad++ for these files rather than automating around it |
| Stack | C#/.NET 10, Avalonia 12 | Same toolchain as DevMem, CallTree, FileFixup, SnapSiphon |
| Prompt delimiter | `---` (**Modern**), with per-file **Legacy** blank-line mode | Legacy is what every existing file uses; Modern is unambiguous |
| State store | `%LOCALAPPDATA%\ClaudeLog\` | The log tree is Syncthing-synced; state files there would conflict |
| Reset countdown | Auto-detected from Claude Code transcripts, manual override | The timestamp is already on disk; manual entry is the fallback |
| On reset | Toast + taskbar flash + stage the next queued prompt on the clipboard | Sending it instead is opt-in (`AutoSendOnReset`); the reset usually lands while Scott is away |
| Sending | Write the prompt into the terminal's console, addressed by PID | See below — this **reversed** the original "never types into the terminal" |
| Terminal | Windows Terminal, launched by the app, one window per session | Delivery is a Win32 console mechanism, so the host is a setting, not an assumption |
| Session identity | ClaudeLog mints the GUID and passes `--session-id` | Known before the process exists, so state.json can record it rather than guess |

### The reversed decision

The app originally staged the clipboard and never touched the terminal, on the grounds that
sending was Scott's to do. Scott asked for direct sending in September 2026: copy-and-paste was the
last piece of manual work left, and it is the thing the app exists to remove.

What the original decision was actually protecting against is still honoured — nothing sends
without an explicit action. `AutoSendOnReset` is off by default, and it is the only path that
could ever send unattended.

## The log tree (external, authoritative)

Root: `C:\Users\Scott\Documents\ClaudeLog`, configurable in settings.json.

```
ClaudeLog\
  BrandBully\            project = folder, mirrors a project under D:\Source
    user_sync.md         session = file, prompts inside
    creative_media.txt   .txt is just as valid as .md
    Examples\            attachments (har, png, html) — NOT sessions
    Plans\               Claude-authored plan docs — NOT sessions
  CallTree\  ClaudeLog\  DevMem\  SnapSiphon\
```

- **Both `.md` and `.txt` are sessions.** 23 and 15 of them respectively; the extension means nothing.
- **Subfolders are not sessions.** Only files directly in a project folder are. Subfolders appear in
  the tree with a ⧉ button and in the session header's shortcut row, and open in Explorer. Scott
  asked for this specifically — `Examples\` and `Plans\` hold the HARs, screenshots and plan
  documents that go with a session.
- **Project → source folder mapping** lives in settings.json (`ProjectSources`); it isn't mechanical
  (`BrandBully` is `D:\Source\BrandBully`, `CallTree` is `D:\Source\repos\CallTree`).
- **UTF-8, no BOM, CRLF, usually no trailing newline.** All three are detected per file and restored
  on save; `SelfTest.SaveIsByteIdentical` proves a no-op edit rewrites identical bytes. Getting this
  wrong shows up as a whole-file diff in Syncthing and in Notepad++.
- **Files change underneath the app.** Syncthing writes them, and Notepad++ may still be open on one.
  `TreeWatcher` reloads when nothing is unsaved and says so in the status bar when something is.
  Saves are atomic (temp file in the same directory, then `File.Replace`), and the watcher ignores
  the `.claudelog.tmp` files that produces.

## Parsing prompts

The whole app rests on this, and the rules were derived from the real corpus, not invented.

**Modern** — a `---` line outside a code fence separates prompts; blank lines are just formatting.
New files use it, and a file that already contains `---` opens in Modern automatically
(`PromptParser.LooksModern`).

**Legacy** — a blank line outside a fence separates prompts, *except*:

- the next block opens with markdown structure (heading, `-`/`*`/`+` list, `>`, `|`, fence,
  numbered item, `**bold`) — it continues the block above rather than starting a prompt;
- the previous line is a heading or ends with a colon — it's introducing what follows
  ("I ran the script and got the following result:" owns its output block);
- the prompt so far contains a markdown heading — it's a pasted document, and only a double blank
  line ends it;
- a double blank line always separates, whatever else is true.

Why not something simpler: a blank line genuinely means both things in this corpus. It separates
prompts in `dev_mem_continued.md` and separates paragraphs of one briefing document in
`call_tree.md`. Counting blank lines can't tell them apart — call_tree.md holds exactly one double
blank in 540 lines, so "double blank = boundary" collapses its 36 prompts into 2, and "single blank
= boundary" shreds its briefing document into ~20 fragments. The rules above get both right, and
`--selftest` pins each one.

Legacy parsing is a heuristic and will occasionally be wrong. The recovery paths are deliberate:
**Merge with next** in the prompt context menu, the mode toggle, and **Convert to `---`** once the
boundaries look right. A pasted document containing its own `---` will over-split in Modern mode —
merge is the fix there too.

**Prompt identity is a content hash** (`PromptParser.HashOf`, trailing whitespace normalized away),
never an index. Prompts get inserted, reordered and edited in Notepad++ and synced between machines;
index-based identity would lose sent/queued state on all of those. `StateStore.Rekey` carries state
across an edit that changes the hash.

`StateStore.Prune` only ever drops **Draft** entries. Switching a file between Legacy and Modern
re-splits it and changes every hash at once, so pruning on "not in the current parse" would erase
which prompts had been sent. Stale Sent/Queued entries are a few bytes; the information isn't
recoverable.

Deleting a prompt and converting a file both rewrite it with no undo, in a folder with no version
control, so `Backups.Snapshot` copies the file into `%LOCALAPPDATA%\ClaudeLog\backups\` first
(10 kept per file). Ordinary edits aren't snapshotted — that's just typing.

## Detecting the session limit, and showing live usage

Two independent, best-effort watchers feed the SESSION LIMIT panel, neither ever writing anything
under `.claude`: **`QuotaWatcher`** reads the reset time out of Claude Code's own transcripts — it
only has data *after* a rejection has actually happened, from an undocumented `quotaLimits` field
recorded on the rejected request. **`UsageWatcher`** fills in the percentage leading up to that, by
polling the same undocumented usage endpoint Claude Code's own status line and the Desktop app call
— continuously, whether or not anything's been rejected. `MainWindowViewModel` shows both: the
headline is the `Xh Ym Zs` countdown while actually blocked, `{percent}% used` otherwise; the
progress bars stay visible in both states.

Full detail — the exact transcript/JSON shapes, the expiry trap in `EffectiveReset`, how the
`/api/oauth/usage` endpoint was reverse-engineered and how to redo that if it moves, and why nothing
here refreshes the OAuth token itself — is in **`skills/SessionIntegration/SKILL.md`**. Read that
before changing either watcher; this paragraph is only the map.

## Sending prompts to a terminal

Three mechanisms were on the table. The one in use was chosen after checking all three actually
work on this machine, not from first principles.

| | Verdict |
|---|---|
| Focus the terminal, `SendInput` Ctrl+V and Enter | Rejected. Steals focus, clobbers the clipboard, and lands wherever focus is a few ms later |
| A terminal emulator inside the app (ConPTY + VT rendering in Avalonia) | Rejected. Thousands of lines of someone else's problem — alternate screen, mouse, resize, colour — to end up with a worse Windows Terminal |
| `WriteConsoleInput` into the tab's console, addressed by PID | **In use.** No focus change, no clipboard, exact target, works minimised |

A Windows Terminal tab gets its own pseudoconsole, and a pseudoconsole is a console object like
any other — another process can `AttachConsole` to it and write input records. Verified end to
end before any of this was built: injected bytes arrive at a Node raw-mode reader byte-identical,
bracketed-paste markers included, and a real `claude` accepted a two-line prompt as one prompt.

Four things about `ConsoleInput` are load-bearing:

- **Bracketed paste is what makes multi-line work.** Text is wrapped in `ESC[200~` / `ESC[201~`,
  exactly what a terminal sends on Ctrl+V. Without it every newline submits the prompt.
- **The Enter goes in a second write, after `SubmitDelayMs`.** A carriage return in the same write
  as the closing marker can be read as part of the paste and end up as a newline in the prompt.
- **Console attachment is process-wide**, so `Write` is serialised behind a lock and gives the
  attachment back when it's done. That's free in the GUI, which has no console — but `--send` is
  printing to the parent console it just took away, hence `ReattachToParent` and the rebind of
  `Console.Out` before it prints anything.
- **The prompt is stripped of control characters.** A stray `ESC` in a pasted log would let the
  text end its own bracketed paste, and everything after it would arrive as keystrokes.

**A successful write proves nothing about acceptance.** Writing to a console succeeds whenever the
console exists; at a permission prompt the same keystrokes answer that instead. `SessionTranscript`
is the check that matters — it watches for a `"type":"user"` entry newer than the send. Only the
*timestamp* is compared: Claude Code reshapes what it stores (long pastes, command expansion,
attachments), so matching on text would report delivered prompts as missing.

### Sessions and where they run

`claude --session-id <uuid>` takes the id as an argument, so **ClaudeLog picks the GUID** and
writes it into `state.json` next to the log file before anything runs. Discovering it afterwards by
watching for a new transcript would be a guess whenever two sessions start close together. A file
that already has an id is resumed with `--resume` instead — chosen by whether the transcript exists
on disk, because `--session-id` on an existing session is an error.

`FileState.SessionDir` is stored per file, not looked up per project, because it is half the path
to the transcript: `ClaudeTerminal.SlugFor` turns `D:\Source` into `D--Source` (every non
-alphanumeric character becomes a hyphen — the same rule `QuotaWatcher` relies on). Changing a
project's default directory later must not strand the sessions started under the old one.

`Settings.SessionDirFor` falls back project → global → `ProjectSources`. That ordering is the whole
feature for anyone who hasn't configured anything: a fresh install runs each project's session in
its own source folder, and one `DefaultSessionDir` moves every project to a shared root at once.
Scott's is `D:\Source`, because the root `CLAUDE.md` there already knows where every project is.

### Launch traps

- **Windows Terminal treats `;` as its own argument separator**, so inline PowerShell needs
  escaping through two layers of quoting. The tab runs a generated `.ps1` in `%LOCALAPPDATA%\
  ClaudeLog\tabs\` instead — no separators, no nesting.
- **`wt.exe` hands the tab to the running Windows Terminal process and exits**, so the PID it
  returns is worthless. The script reports `$PID` to a file, and that is polled. The shell and
  Claude Code share one console, so writing to the shell's PID reaches Claude Code's input.
- **Claude Code marks its own environment.** A session that inherits `CLAUDE_CODE_CHILD_SESSION`
  and friends writes *no transcript at all* — the terminal opens, prompts arrive, and only the
  confirmation and the quota countdown quietly stop working. `Start` clears those variables, which
  is why it uses `UseShellExecute = false`. This is not hypothetical: it is what happens every time
  the app is launched from a Claude Code session, which is what "run the app" does.

`ClaudeLog --terminal` lists every file's session, directory, PID and last recorded prompt;
`--terminal --start <dir>` and `--send <pid> <text>` are the whole loop without the UI, and are how
a wrong `TerminalArgs` shows itself.

## The editor

The prompt editor is AvaloniaEdit, not a `TextBox` — per-run coloring and squiggle rendering both
need a real editor control. Three things sit on top of it, all driven by `EditorController`:

**Highlighting** (`MarkdownScanner`) covers fenced and inline code, bullets, numbered items,
headings, bold and quotes. Deliberately narrow: it's for seeing the shape of a prompt, not for
being a markdown renderer. Fenced lines come from `PromptParser.MapFences`, the same function the
parser uses, so the editor can never color something as code that the parser treats as prose. The
map is recomputed per document change rather than per line, because a line only knows it's inside a
fence by looking at everything above it and the colorizer runs for every visible line on every
repaint.

The rules produce **spans**, and two renderers consume them: `MarkdownColorizer` paints them
through AvaloniaEdit, `MarkdownBlock` builds them as TextBlock inlines for the prompt list. Keep
them behind the one scanner — the panes show the same text a few pixels apart, and any drift
between them is immediately visible. One trap in the inline renderer: assigning `null` to a
`Run.Foreground` is not "inherit", it's "no brush", and the run paints nothing. Only ever *set*
the non-default values.

**Completion** (`WordIndex`) is Notepad++'s word completion widened to the whole log tree — ~4,200
distinct words. Words in the current prompt outrank words from months ago. The index is built off
the UI thread at startup and swapped in whole; it is only ever mutated on the UI thread afterwards,
so no reader sees a half-built one.

**Spell checking** goes through Windows' own spell-check COM service (`SpellChecker`) — no package,
no dictionary files, and *Add to dictionary* writes to the user dictionary the rest of Windows
reads. The GUIDs in that file are from the SDK's `spellcheck.h` and are present in HKCR.

The filtering in `SpellCheckPass` is the part that matters. Windows flags everything it doesn't
know, and these prompts are pasted SIP traces, JSON, paths and product names — unfiltered it is a
wall of red and worth nothing. Four filters: code spans and paths (`TextScan.CodeAndPathSpans`),
identifier-shaped words (`TextScan.LooksLikeCode`), anything touching or containing `.` `/` `\` `@`,
and anything in `WordIndex` with three or more uses. On `call_tree.md` that's 13 flags in 540 lines,
five of them real typos. **`--spell <file>` prints exactly that list** — use it after any change to
the filters, because the failure mode is silent noise rather than an error.

Everything degrades: no spell-check service means no squiggles and a logged warning, not a crash.

## The prompt list

The list shows every prompt **in full**, not as a one-line preview, so the pane reads like the
session file rather than like an index of it. Three things follow from that and are easy to undo by
accident:

- **Virtualization is off** (`ItemsPanel` is a plain `StackPanel`). Item heights run from one line
  to seventy; the virtualizer estimates them and the scrollbar jumps. The largest file in the tree
  holds 51 prompts, so measuring them all costs nothing.
- **`PromptViewModel.Text` is observable and follows the editor keystroke by keystroke**
  (`OnEditorTextChanged`). The card sits directly above the caret — showing the last saved version
  there would contradict what's being typed. `Copy` therefore copies what's on screen, including
  unsaved edits; `StateStore.Rekey` carries the status across the hash change on save.
- **Prompt cards are `TextBlock`s, not editors.** Fifty `TextEditor`s would each bring a caret, an
  undo stack and a text area to a view that is only ever read.

## Architecture

`Core\` is pure logic with no Avalonia dependency and is exercised by `--selftest`; `ViewModels\`
coordinates; `Views\` is one window. The view hands the view model its clipboard, toast and
taskbar-flash callbacks (`CopyToClipboard`, `Notify`, `FlashWindow`) rather than the view model
reaching for a `TopLevel`.

| File | Role |
|---|---|
| `Core\PromptParser.cs` | Text → prompts in both modes, fence mapping, content hashing. The rules above live here. |
| `Core\SessionDocument.cs` | One session file: load, splice-edit, append, merge, convert, atomic save preserving encoding/EOL. |
| `Core\LogTree.cs` | Scans projects/sessions/attachment folders, sessions newest-first; `TreeWatcher` debounces external changes. |
| `Core\MarkdownScanner.cs` | The markdown highlighting rules, as spans over a line. Shared by both renderers. |
| `Core\StateStore.cs` | `state.json` — per-prompt status, per-file mode, queue, manual reset, last session. |
| `Core\QuotaWatcher.cs` | Tails Claude Code transcripts for `quotaLimits.resetsAt`. Best-effort by design — see `skills/SessionIntegration/SKILL.md`. |
| `Core\UsageWatcher.cs` | Polls Anthropic's usage endpoint for the live session/weekly percentage. Same skill, same best-effort rule. |
| `Core\ConsoleInput.cs` | `WriteConsoleInput` into another process's console: bracketed paste, then Enter. |
| `Core\ClaudeTerminal.cs` | Starts Claude Code in a terminal; session ids, project slugs, PID discovery. |
| `Core\SessionTranscript.cs` | Reads back the session's own transcript to confirm a prompt landed. |
| `Core\Settings.cs`, `Paths.cs` | settings.json and the `%LOCALAPPDATA%` locations. |
| `Core\Shell.cs` | Explorer open/reveal and the `FlashWindowEx` taskbar flash. |
| `Core\Backups.cs` | Pre-destructive-write snapshots, kept in `%LOCALAPPDATA%`, out of the synced tree. |
| `Core\SpellChecker.cs` | COM interop onto the Windows spell-check service. |
| `Core\SpellCheckPass.cs` | One filtered spell-check run — the filters that keep it usable. |
| `Core\TextScan.cs` | Code spans, paths, identifier-shaped words, word tokens. Shared by both features. |
| `Core\WordIndex.cs` | Vocabulary of the whole log tree: completion source and spell-check silencer. |
| `Views\Editing\EditorController.cs` | Wires the editor: text sync with the VM, highlighting, completion, squiggles, shortcuts. |
| `Views\Editing\MarkdownColorizer.cs` | Paints those spans into the editor through AvaloniaEdit. |
| `Views\Editing\MarkdownBlock.cs` | Paints those spans as TextBlock inlines — the prompt cards in the list. |
| `Views\Editing\MarkdownPalette.cs` | The colors, shared by both, immutable and allocated once. |
| `Views\Editing\SquiggleRenderer.cs` | Wavy underlines for spelling errors, visible lines only. |
| `Core\Cli.cs`, `SelfTest.cs` | Headless commands; attaches to the parent console since this is a WinExe. |
| `ViewModels\MainWindowViewModel.cs` | Tree, session, editor, copy/queue, countdown, reset handling. |
| `Views\MainWindow.axaml` | The three panes, prompt cards, queue, keybindings, the tree's context menu. |
| `Views\TextPrompt.axaml` | The one-line input dialog — Avalonia has none — used to name and rename files. |

Editing writes back by **splicing the prompt's line range into the original text**, never by
re-serializing the whole file — that's what keeps every hand-placed separator and blank line intact.

## The tree's context menu

Creating, renaming and re-splitting a file all live on the tree's right-click menu, and the header
keeps only **New prompt** — the split-mode and convert buttons that used to sit there come up once
in the life of a file, not once a session.

Three things about it are load-bearing:

- **The commands are on `TreeNodeViewModel`, not on the window's view model.** A ContextMenu is its
  own popup tree, so a binding inside one cannot walk up to the TreeView and reach the window's
  DataContext — the node is the only thing in scope. `TreeNodeViewModel.Owner` forwards to the real
  implementations. Bindings there are compile-checked (`x:DataType` on the style), so a renamed
  command is a build error rather than a menu item that silently does nothing.
- **Right-clicking a TreeViewItem does not select it**, so a command can arrive for a file that
  isn't open. `MainWindowViewModel.Open(node)` opens it first; everything that edits "the open file"
  goes through it.
- **A rename has to move state as well as bytes.** Everything in state.json is keyed by relative
  path — prompt statuses, queue entries, `LastSession` — so `StateStore.RenameFile` moves all three.
  `SelfTest.RenameCarriesFileState` pins it.

A new file is created empty, which looks like neither parse mode, so `NewSessionIn` records
`Settings.NewFileMode` in the store *before* the file is first opened — otherwise `LoadSession`
guesses Legacy and the setting is silently ignored.

`LogTree.NormalizeSessionName` appends `.md` to anything that isn't already `.md`/`.txt`, so a dot
mid-name (`phase.2`) is part of the name rather than an unknown extension to reject.
`ValidateSessionName` runs before the dialog closes, so bad names are refused in the dialog instead
of being explained in the status bar afterwards.

## Releases

Same shape as DevMem. `.github/workflows/build.yml` publishes a self-contained, single-file,
compressed `win-x64` binary and **runs it**; `release.yml` calls that workflow on a `v*` tag and
attaches what it produced, so a release ships exactly the binary CI already smoke-tested.

```powershell
git tag v0.1.0 && git push origin v0.1.0
```

Two things here are specific to this app being a GUI program, and both are easy to undo by accident:

- **PowerShell does not wait for a WinExe.** `.\ClaudeLog.exe --selftest` returns immediately,
  `$LASTEXITCODE` stays empty and nothing is captured — written the obvious way, every CI assertion
  passes no matter what the binary does. The smoke-test step uses `Start-Process -Wait -PassThru`
  with redirected output for exactly this reason. Verified: the obvious form really does capture
  zero lines.
- **`--startup` is the only check that touches Avalonia.** `Cli.IsHeadless` returns before any UI
  code, so `--selftest` cannot catch a single-file bundle that failed to unpack its native
  libraries or a window whose XAML stopped loading. `Program.Startup` calls
  `SetupWithoutStarting()` and constructs `MainWindow`, which loads the whole window's XAML,
  styles and templates, then exits.

**Not trimmed**, deliberately: Avalonia resolves controls, styles and converters by reflection from
compiled XAML, and a trimmer dropping something only the released binary needs is the silent,
release-only breakage this project has no test suite to catch. `TreatWarningsAsErrors` is on in the
publish, holding the line at the zero warnings the project builds with today.

`Settings.ProjectSources` defaults to **empty**. It's the one setting whose useful value is specific
to one machine, and a downloaded binary shouldn't arrive pre-filled with paths from another.

## Environment gotchas

- **`NuGet.config` is required.** The machine-wide config is an explicit package-source allowlist
  (it exists for work GitLab feeds), so a new package fails to restore with NU1100 until it's
  listed. This repo scopes itself to nuget.org instead, the same way CallTree does. Don't "fix" a
  restore failure by editing the global file.
- **The target framework is `net10.0-windows`**, so output lands in `bin\Debug\net10.0-windows\`.
  The platform suffix is what makes the COM and Win32 calls warning-free; it's an honest
  declaration, since Explorer, the taskbar flash and the spell checker are all Windows-only.
- **`CLAUDELOG_HOME`** overrides the `%LOCALAPPDATA%` directory. The app is usually already running
  while it's being worked on, so a second instance can be pointed at throwaway settings and state
  instead of fighting over the real ones. Combined with a `LogRoot` in settings.json pointing at a
  scratch folder, a test instance touches nothing real.
- **The running app locks its own exe.** If a build fails with MSB3027, the app is open — build with
  `-o <dir>` to a scratch folder rather than closing someone's editor out from under them.
- **`claudelog.log`** in the app-data directory records what got swallowed. A GUI app that degrades
  quietly needs somewhere to have degraded.

## Conventions

- Central package management (`Directory.Packages.props`), Avalonia 12.0.4 + AvaloniaEdit 12.0.0 +
  CommunityToolkit.Mvvm 8.4.2, compiled bindings on, MVVM with `[ObservableProperty]` /
  `[RelayCommand]`.
- Nothing in this app should crash over a file it couldn't parse: `Log.Warn` and degrade. Parsing,
  scanning, quota reading and Explorer calls are all wrapped.
- Status-bar text is the app's voice — say what happened ("Copied prompt 36 · marked sent"), not
  "Done".

## Non-goals

- **No unattended sending.** Every send is an explicit action. `AutoSendOnReset` is the single
  exception and it is off by default. (Typing into the terminal at all *was* a non-goal until
  September 2026 — see "The reversed decision" above.)
- **No response capture.** Scott doesn't save Claude's responses and the log format has no place
  for them. `SessionTranscript` reads the transcript only for a timestamp, and never stores
  anything from it.
- **No terminal emulator.** The app starts a terminal and writes to it; it does not draw one. See
  the table in "Sending prompts to a terminal".
- **No reorganizing the log tree**, and nothing written into it but the session files themselves.
