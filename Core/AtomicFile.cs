namespace ClaudeLog.Core;

/// <summary>
/// The temp-file-then-swap half of every save in this app, with the retries Windows makes
/// necessary.
///
/// <c>File.Replace</c> deletes the destination as part of the swap, so it fails outright —
/// <c>"Unable to remove the file to be replaced"</c> — whenever anything else holds the file open
/// without FILE_SHARE_DELETE. The log tree has Syncthing scanning it, Notepad++ sometimes open on
/// a file, and Defender and the search indexer reading both, so this is not a rare race: it is the
/// save that appeared to do nothing, and the <see cref="IOException"/> that reached a command with
/// no try/catch around it.
///
/// Two things follow. The retry is short and blind, because those holders let go in milliseconds.
/// The fallback is a plain overwrite, which needs *write* access to the destination where a
/// replace needs *delete* access — that gets past a reader holding a share-read-write handle,
/// which is the common case. It isn't atomic, which is why it is second and not first.
/// </summary>
public static class AtomicFile
{
    private const int Attempts = 5;
    private const int DelayMs = 60;

    /// <summary>
    /// Moves <paramref name="tempPath"/> onto <paramref name="targetPath"/>, atomically when it
    /// can. Throws only when the content could not be written at all; the temp file is never left
    /// behind, because one in the log tree shows up in Explorer and in Syncthing.
    /// </summary>
    public static void Replace(string tempPath, string targetPath)
    {
        try
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    File.Replace(tempPath, targetPath, null);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt < Attempts - 1)
                    {
                        Thread.Sleep(DelayMs * (attempt + 1));
                        continue;
                    }

                    Log.Warn($"replace {targetPath} failed {Attempts} times, overwriting instead: {ex.Message}");
                    File.Copy(tempPath, targetPath, overwrite: true);
                    return;
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) Delete(tempPath);
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not remove {path}: {ex.Message}");
        }
    }
}
