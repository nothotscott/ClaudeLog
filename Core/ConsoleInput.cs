using System.Runtime.InteropServices;

namespace ClaudeLog.Core;

/// <summary>
/// Writes text into another process's console input buffer.
///
/// This is how a prompt reaches Claude Code. The alternative — focusing the terminal window and
/// synthesising Ctrl+V and Enter with SendInput — steals focus, clobbers the clipboard and lands
/// wherever focus happens to be a few milliseconds later. <c>WriteConsoleInput</c> has none of
/// those failure modes: it addresses one console by PID, works while the window is behind others
/// or minimised, and cannot be misdirected by the user clicking somewhere mid-send.
///
/// Windows Terminal gives every tab its own pseudoconsole, and a pseudoconsole is still a console
/// object from the client's side, so a tab is addressable this way like any other. All the
/// processes in a tab share one console, so the PID may be the shell that launched Claude Code
/// rather than Claude Code itself — whichever process is currently reading gets the input.
///
/// **The console attachment is process-wide.** A process may be attached to exactly one console,
/// so <see cref="SendPrompt"/> takes over this process's attachment for the length of the call and gives
/// it back afterwards. That is free in the GUI, which has no console at all, but it means the call
/// has to be serialised and that anything else reading a console must not run concurrently with
/// it — see the note on Cli.Send.
/// </summary>
public static class ConsoleInput
{
    /// <summary>ESC, spelled as a code point: a literal escape byte in source is invisible.</summary>
    private const char Esc = (char)0x1B;

    /// <summary>
    /// The bracketed-paste markers. Claude Code reads its input as raw bytes, so text arriving
    /// between these two is treated as one paste — newlines inside stay newlines instead of each
    /// submitting the prompt. This is exactly what a terminal sends on Ctrl+V, which is what makes
    /// a sent prompt behave identically to one Scott pastes by hand.
    /// </summary>
    private static readonly string PasteStart = Esc + "[200~";

    private static readonly string PasteEnd = Esc + "[201~";

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const ushort KeyEvent = 1;
    private const ushort VkReturn = 0x0D;
    private static readonly nint InvalidHandle = -1;

    /// <summary>
    /// One console input record. The layout has to match Win32's INPUT_RECORD exactly: a WORD
    /// event type, two bytes of padding to the union's alignment, then KEY_EVENT_RECORD.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InputRecord
    {
        public ushort EventType;
        public ushort Padding;
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern nint CreateFile(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInput(nint handle, InputRecord[] buffer, uint length, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    private static readonly Lock Gate = new();

    /// <summary>
    /// Sends <paramref name="text"/> to the console <paramref name="pid"/> belongs to as a
    /// bracketed paste, then Enter to submit it. Returns null on success, or a sentence describing
    /// what went wrong.
    ///
    /// The Enter goes in a second write after a pause, because a carriage return in the same write
    /// as the closing marker can be read as part of the paste and end up as a newline in the prompt
    /// instead of submitting it.
    /// </summary>
    public static string? SendPrompt(int pid, string text, int submitDelayMs)
    {
        var error = Write(pid, PasteStart + Sanitize(text) + PasteEnd);
        if (error is not null) return error;

        Thread.Sleep(Math.Clamp(submitDelayMs, 0, 5_000));
        return Write(pid, "\r");
    }

    /// <summary>
    /// Strips what a terminal would act on rather than display. Prompts are markdown, so nothing
    /// here should ever fire — but a stray escape byte in a pasted log would let the text end its
    /// own bracketed paste, and everything after it would be read as keystrokes.
    /// </summary>
    private static string Sanitize(string text)
    {
        var clean = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings("\r"))
        {
            if (c == '\r' || c == '\t' || !char.IsControl(c)) clean.Append(c);
        }
        return clean.ToString();
    }

    /// <summary>Writes raw characters as key events. Null on success, else what failed.</summary>
    public static string? Write(int pid, string text)
    {
        if (text.Length == 0) return null;

        lock (Gate)
        {
            // Detach from whatever console this process has before claiming another; in the GUI
            // there is none and this is a no-op.
            FreeConsole();

            if (!AttachConsole(pid))
            {
                var code = Marshal.GetLastWin32Error();
                return code == 5
                    ? $"Terminal {pid} refused the connection (access denied)"
                    : $"Terminal {pid} is not running (error {code})";
            }

            var handle = CreateFile("CONIN$", GenericRead | GenericWrite,
                FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);

            if (handle == InvalidHandle)
            {
                var code = Marshal.GetLastWin32Error();
                FreeConsole();
                return $"Could not open the terminal's input (error {code})";
            }

            try
            {
                // Key down and key up per character. Readers act on key-down alone, but a TUI that
                // tracks modifier state sees a stuck key without the matching release.
                var records = new InputRecord[text.Length * 2];
                var n = 0;
                foreach (var c in text)
                {
                    for (var down = 1; down >= 0; down--)
                    {
                        records[n].EventType = KeyEvent;
                        records[n].KeyDown = down;
                        records[n].RepeatCount = 1;
                        records[n].VirtualKeyCode = c == '\r' ? VkReturn : (ushort)0;
                        records[n].UnicodeChar = c;
                        n++;
                    }
                }

                if (!WriteConsoleInput(handle, records, (uint)n, out var written))
                {
                    return $"Writing to the terminal failed (error {Marshal.GetLastWin32Error()})";
                }

                return written == n ? null : $"Only {written / 2} of {text.Length} characters reached the terminal";
            }
            finally
            {
                CloseHandle(handle);
                FreeConsole();
            }
        }
    }

    /// <summary>
    /// Re-attaches to the console that launched this process. Only the headless commands need it:
    /// they print to the parent terminal, and <see cref="Write"/> has just taken that away.
    /// </summary>
    public static void ReattachToParent()
    {
        lock (Gate)
        {
            FreeConsole();
            AttachConsole(-1);
        }
    }
}
