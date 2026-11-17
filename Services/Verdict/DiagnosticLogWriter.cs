namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// The diagnostic-log writer (debt D-25: audit S23).
///
/// <see cref="Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic"/> used to be three nested empty
/// <c>catch</c> blocks: an unwritable executable directory, an unwritable XDG state directory and any
/// other error all ended in the same place — nothing, with no record that anything was lost. The
/// per-directory loop is now a pure outcome-producing function (the actual append is injected), so
/// "wrote it here" and "could not write it anywhere, here is why" are two different answers.
/// </summary>
public static class DiagnosticLogWriter
{
    /// <summary>
    /// Try each candidate directory in order. <paramref name="append"/> receives the full file path
    /// and must throw on failure (the real one creates the directory and appends inside a lock).
    /// </summary>
    public static ReadOutcome<string> TryWrite(IReadOnlyList<string> directories, string fileName, string content,
        Action<string> append)
    {
        ArgumentNullException.ThrowIfNull(append);
        if (directories == null || directories.Count == 0)
            return ReadOutcome<string>.Failed("no diagnostic log directory candidate");

        var failures = new List<string>();
        foreach (string directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                failures.Add("<empty candidate>: skipped");
                continue;
            }

            string path = Path.Combine(directory, fileName);
            try
            {
                append(path);
                return ReadOutcome<string>.Ok(path);
            }
            catch (Exception ex)
            {
                failures.Add(directory + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        return ReadOutcome<string>.Failed("no writable diagnostic log directory (" + string.Join("; ", failures) + ")");
    }
}
