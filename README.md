# ClaudeLog

A prompt editor for the way I actually use Claude Code: write the prompt in markdown, copy it,
paste it into the terminal — and keep a log of every prompt, organized by project.

It replaces Notepad++ for the files under `C:\Users\Scott\Documents\ClaudeLog`, without changing
them. Same folders, same files, same plain markdown.

![ClaudeLog](docs/screenshot.png)

## What it does

- **Projects and sessions** in a tree — one folder per project, one file per session, exactly the
  layout that's already there. `.md` and `.txt` both count. Sessions are listed newest-edited
  first, so whatever you were working on is at the top.
- **Prompts as units.** A session file is split into its prompts, each with a one-click **Copy**
  that also marks it sent and timestamps it. No more selecting to the next blank line by hand.
- **The whole file, visible.** Each prompt is shown in full and highlighted, so the list reads like
  the session file itself rather than an index of it — and the prompt you're editing updates there
  as you type.
- **A queue for when the session limit hits.** Queue the prompts you couldn't send; ClaudeLog
  reads the reset time out of Claude Code's own transcripts, counts down, and when the limit
  resets it toasts you, flashes the taskbar and puts the next queued prompt on the clipboard.
  It never types into your terminal — the paste stays yours.
- **Explorer shortcuts** for the project folder and its attachment folders (`Examples`, `Plans`),
  from the tree or the session header.

## Writing

The editor is built for getting a prompt down fast and reading it back.

- **Markdown highlighting** — fenced and inline code, bullets, numbered steps, headings, bold,
  quotes. Enough to see the shape of a prompt at a glance, and nothing else. The same colors in the
  editor and in the list above it.
- **Completion from your own logs.** Every word across every session is indexed, so `SIPSorc…`
  completes to `SIPSorcery` in a file that has never mentioned it. Type three letters, or press
  `Ctrl+Space`. Words from the prompt you're writing rank above words from June.
- **Spell checking** through Windows' own spell checker, filtered hard so it stays quiet: nothing
  inside fences, inline code, URLs or paths; nothing that looks like an identifier
  (`AIMediaSession`, `resetsAt`, `SIP`); and nothing you've used more than a couple of times
  anywhere in your logs. `Ctrl+Space` on a squiggled word offers corrections and
  *Add to dictionary*, which adds to the Windows user dictionary the rest of the OS reads.

Run `ClaudeLog --spell <file>` to see exactly what would be flagged in a file, and why the filter
matters — on a 540-line session it's about a dozen words, most of them real typos.

## Prompt separators

Two ways a file can be split, per file:

- **`---` separators** (default for new files) — a `---` line ends a prompt. Unambiguous when a
  prompt spans paragraphs or contains fenced code.
- **Blank-line (legacy)** — what the existing files use. A blank line ends a prompt, except where
  the text says otherwise: lists, fences, headings, tables and anything introduced by a line
  ending in a colon stay with the prompt above them, and a prompt containing markdown headings
  ends only at a double blank line.

A file that already contains `---` opens in `---` mode automatically. **Convert to `---`** rewrites
a legacy file with explicit separators once its boundaries look right.

## Keys

| | |
|---|---|
| `Ctrl+S` | save the editor into the file |
| `Ctrl+Enter` | copy the selected prompt, mark it sent |
| `Ctrl+N` | new prompt (focus jumps to the editor) |
| `Ctrl+Q` | queue the selected prompt |
| `Ctrl+E` | jump into the editor |
| `Esc` | leave the editor for the prompt list |
| `Ctrl+Space` | suggestions at the cursor — completions, or spelling fixes on a squiggled word |
| `F5` | rescan the log folder |

In the suggestion popup: `↑`/`↓` to move, `Enter` or `Tab` to accept, `Esc` to dismiss.

## Command line

```powershell
ClaudeLog                    # the app
ClaudeLog --tree             # projects, sessions, prompt counts
ClaudeLog --parse <file>     # how a file splits into prompts (--legacy / --modern to force)
ClaudeLog --quota            # the detected session-limit reset
ClaudeLog --state            # where settings and state live
ClaudeLog --spell <file>     # words the spell checker would flag in a file
ClaudeLog --selftest         # parser, save round-trip, spelling and state checks
```

## Build

```powershell
dotnet build
dotnet run
```

.NET 10, Avalonia 12, AvaloniaEdit. Windows only — Explorer integration, the taskbar flash and the
spell checker are all Win32/COM.

## Where things are

- Logs: `C:\Users\Scott\Documents\ClaudeLog` — never modified except the session files themselves.
- Settings, per-prompt state, backups and `claudelog.log`: `%LOCALAPPDATA%\ClaudeLog\` —
  deliberately outside the Syncthing-synced log folder. Set `CLAUDELOG_HOME` to point elsewhere
  (handy for running a second instance against throwaway state).
