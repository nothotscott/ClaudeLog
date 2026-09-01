using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ClaudeLog.Core;

public readonly record struct SpellingError(int Start, int Length);

/// <summary>
/// Spell checking through Windows' own spell-check service (the one Edge and Office use), reached
/// by COM. No NuGet package, no dictionary files to ship, and "Add to dictionary" adds to the user
/// dictionary the rest of Windows already reads.
///
/// Everything here is best-effort: if the service is unavailable the checker reports no errors
/// rather than failing, and the editor simply shows no squiggles.
/// </summary>
public sealed class SpellChecker : IDisposable
{
    private readonly ISpellChecker? _checker;

    public SpellChecker(string language = "en-US")
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"))
                       ?? throw new InvalidOperationException("spell checker factory not registered");
            var factory = (ISpellCheckerFactory)Activator.CreateInstance(type)!;

            if (factory.IsSupported(language) == 0)
            {
                Log.Warn($"spell check: {language} not supported");
                return;
            }

            _checker = factory.CreateSpellChecker(language);
        }
        catch (Exception ex)
        {
            Log.Warn($"spell check unavailable: {ex.Message}");
        }
    }

    public bool Available => _checker is not null;

    /// <summary>Errors in one chunk of text, as offsets into that text.</summary>
    public List<SpellingError> Check(string text)
    {
        var errors = new List<SpellingError>();
        if (_checker is null || string.IsNullOrWhiteSpace(text)) return errors;

        try
        {
            var enumerator = _checker.Check(text);
            while (true)
            {
                var error = enumerator.Next();
                if (error is null) break;
                errors.Add(new SpellingError((int)error.StartIndex, (int)error.Length));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"spell check failed: {ex.Message}");
        }

        return errors;
    }

    public List<string> Suggest(string word)
    {
        var suggestions = new List<string>();
        if (_checker is null || string.IsNullOrWhiteSpace(word)) return suggestions;

        try
        {
            var strings = _checker.Suggest(word);
            var buffer = new string[1];
            while (strings.Next(1, buffer, nint.Zero) == 0 && buffer[0] is not null)
            {
                suggestions.Add(buffer[0]);
                if (suggestions.Count >= 10) break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"suggest failed: {ex.Message}");
        }

        return suggestions;
    }

    /// <summary>Adds to the Windows user dictionary — it sticks, and other apps see it too.</summary>
    public void Add(string word)
    {
        try
        {
            _checker?.Add(word);
        }
        catch (Exception ex)
        {
            Log.Warn($"add to dictionary failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_checker is not null && Marshal.IsComObject(_checker)) Marshal.ReleaseComObject(_checker);
    }

    // ------------------------------------------------------------- interop
    // Declared in vtable order; only the methods actually used are listed, which is safe as long as
    // nothing below the last declaration is ever called. GUIDs are from the Windows SDK spellcheck.h
    // and are verified present in HKCR on this machine.

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumString get_SupportedLanguages();

        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);

        [return: MarshalAs(UnmanagedType.Interface)]
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string get_LanguageTag();

        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);

        [return: MarshalAs(UnmanagedType.Interface)]
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);

        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);

        void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        /// <summary>Returns null when the enumeration is finished (S_FALSE).</summary>
        [return: MarshalAs(UnmanagedType.Interface)]
        ISpellingError? Next();
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint StartIndex { [return: MarshalAs(UnmanagedType.U4)] get; }

        uint Length { [return: MarshalAs(UnmanagedType.U4)] get; }
    }
}
