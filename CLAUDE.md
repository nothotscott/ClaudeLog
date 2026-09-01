# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`README.md` is the user-facing documentation. This file is the working rules: commands, the format
contracts, and the decisions that are easy to regress. Most of the constraints here come from data
that lives outside this repo — read the two "external" sections before changing anything that reads
or writes files.

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
copy with one click, queue what can't be sent yet, and get told the moment the limit resets.

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
dotnet run -- --state             # where settings and state live
dotnet run -- --startup           # boot Avalonia and load MainWindow without showing it, then exit
```

There is no test project. **`--selftest` is the regression net** — 67 checks over prompt splitting,
byte-exact saving, the quota record format, the state-store invariants, tree ordering, the
highlighting rules and the spelling filters (including a real round-trip through the Windows spell
checker). Add to `SelfTest.cs` when changing any of those; it
is far more useful than it looks, and it is how the parser rules below were validated against the
real corpus.

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
| On reset | Toast + taskbar flash + stage the next queued prompt on the clipboard | **The app never types into the terminal and never sends prompts on its own** |

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

## Detecting the session limit

Claude Code records the reset time in its own transcripts. A rejected request leaves this in
`%USERPROFILE%\.claude\projects\<cwd-slug>\<session>.jsonl`:

```json
"quotaLimits": { "status": "rejected", "resetsAt": 1788138000,
                 "rateLimitType": "five_hour", "overageStatus": "rejected" }
```

`resetsAt` is unix seconds. `QuotaWatcher` scans the whole `projects` tree, not one slug — the slug
comes from the directory Claude Code was launched in (`D--Source` for `D:\Source`) and Scott launches
from several. It reads only the last 2 MB of each of the 40 most recently written transcripts, with
`FileShare.ReadWrite | Delete` because Claude Code is appending to them live, and takes the newest
future `resetsAt` among rejected records.

**This is an undocumented internal format.** Every failure path degrades to "no reset detected" and
leaves the manual override in charge; nothing here ever writes under `.claude`. If the format
changes, `SelfTest.QuotaReadsARejectedRecord` is what fails — it builds a synthetic transcript in
the observed shape.

**The expiry trap** (already fixed once, don't reintroduce): the countdown crossing zero is what
fires the reset, so `EffectiveReset` must keep returning the manual override for the moment *after*
it passes. Dropping an expired override from that property makes the reset silently never fire —
the countdown just goes back to "No limit pending" and the queue sits there. `OnResetReached` clears
the override; the constructor clears one left stale by a previous run.

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
| `Core\QuotaWatcher.cs` | Tails Claude Code transcripts for `quotaLimits.resetsAt`. Best-effort by design. |
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
| `Views\MainWindow.axaml` | The three panes, prompt cards, queue, keybindings. |

Editing writes back by **splicing the prompt's line range into the original text**, never by
re-serializing the whole file — that's what keeps every hand-placed separator and blank line intact.

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

- **No typing into the terminal, no auto-send.** Explicitly rejected. The app stages the clipboard.
- **No response capture.** Scott doesn't save Claude's responses and the log format has no place for
  them. Further Claude Code integrations are expected — that's why parsing sits behind
  `PromptParser` and quota reading behind `QuotaWatcher` — but nothing speculative gets built now.
- **No reorganizing the log tree**, and nothing written into it but the session files themselves.
