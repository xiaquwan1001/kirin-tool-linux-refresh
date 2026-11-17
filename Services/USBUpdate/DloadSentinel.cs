// Licensed under the repository's Business Source License 1.1. See LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services.USBUpdate;

/// <summary>
/// The verdict of the pre-delete check for a USB-update scratch directory.
/// <see cref="MayDelete"/> is the only thing a caller may branch on; <see cref="Unexpected"/> names
/// the entries that made the check refuse, so the log says what was found instead of what was
/// expected.
/// </summary>
public sealed record DloadCleanupVerdict(bool MayDelete, string Reason, IReadOnlyList<string> Unexpected, bool DirectoryExists)
{
    public static DloadCleanupVerdict Allow(string reason) => new(true, reason, Array.Empty<string>(), true);
}

/// <summary>
/// D-14 / audit BB-13+BB-16 (GUI side): the USB-update workflow used to call
/// <c>Directory.Delete(dload, recursive: true)</c> on a directory it did not have to recognise.
/// With the GUI's literal relative <c>"dload"</c> that meant "recursively delete a directory in the
/// user's current working directory" — any folder that happened to be called <c>dload</c> was
/// destroyed before a single frame was sent, and nothing checked what was inside.
///
/// Two independent defences now:
///   1. the GUI builds the directory with <see cref="PrivateWorkDirectory"/> (fresh random name,
///      0700, under the temp dir) instead of a cwd-relative literal, like the CLI already did;
///   2. this sentinel decides whether the directory may be deleted at all, and a refusal is never
///      silently ignored.
///
/// The sentinel is a positive identification: an empty directory may go (nothing can be lost),
/// otherwise <b>every</b> entry must be a name this workflow writes (the marker files plus
/// <c>*.img</c>/<c>*.img.header</c> built from a whitelisted partition name) <b>and</b> at least one
/// workflow marker must be present. "The names look harmless" is not enough — a directory holding
/// only <c>photo.img</c> is still not ours.
///
/// <see cref="Evaluate"/> is pure (path + entry names), so the rule is testable without a
/// filesystem; <see cref="EvaluateDirectory"/> is the thin filesystem reader.
/// </summary>
public static class DloadSentinel
{
    /// <summary>Files the workflow itself writes at the top level of the directory.</summary>
    public static readonly IReadOnlyList<string> MarkerFiles = new[]
    {
        "list.txt",
        "unlockcode",
        "XLOADER_BAK.img",
        "XLOADER_HEADER_BAK.img",
    };

    /// <summary>
    /// At least one of these must be present before a non-empty directory is deleted: they are the
    /// artefacts the tool cannot run without, so their presence identifies the directory as ours.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredMarkers = new[]
    {
        "list.txt",
        "XLOADER.img",
        "XLOADER.img.header",
        "unlockcode",
    };

    /// <summary>
    /// Is one directory entry a name this workflow creates? Anything else (a subdirectory, the
    /// user's own files, a planted symlink name, dotfiles) is foreign.
    /// </summary>
    public static bool IsOwnEntry(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (MarkerFiles.Any(marker => string.Equals(marker, name, StringComparison.OrdinalIgnoreCase))) return true;

        // <partition>.img / <partition>.img.header, where the partition name passed the shared
        // UPDATE.APP whitelist (PartitionNames) - the same rule the extractor writes them with.
        foreach (string suffix in new[] { ".img.header", ".img" })
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string stem = name[..^suffix.Length];
            return PartitionNames.IsSafe(stem);
        }
        return false;
    }

    /// <summary>
    /// The rule. Pure: <paramref name="entries"/> is whatever the caller listed (top level only).
    /// </summary>
    public static DloadCleanupVerdict Evaluate(string? directory, bool directoryExists, IReadOnlyList<string> entries)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new DloadCleanupVerdict(false, "no directory was given", Array.Empty<string>(), false);

        // Never delete a filesystem root, the user's home, or something too close to it. This is the
        // case that made the old cwd-relative literal dangerous: "dload" could resolve to anything.
        string full;
        try { full = Path.GetFullPath(directory!); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return new DloadCleanupVerdict(false, "the path cannot be resolved (" + ex.Message + ")", Array.Empty<string>(), false);
        }

        string trimmed = Path.TrimEndingDirectorySeparator(full);
        string? parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent) || parent == trimmed)
            return new DloadCleanupVerdict(false, "refusing to delete a filesystem root: " + full, Array.Empty<string>(), directoryExists);

        string? home = null;
        try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch (Exception) { }
        if (!string.IsNullOrEmpty(home) && string.Equals(Path.TrimEndingDirectorySeparator(home!), trimmed, StringComparison.Ordinal))
            return new DloadCleanupVerdict(false, "refusing to delete the user's home directory: " + full, Array.Empty<string>(), directoryExists);

        if (!directoryExists)
            return new DloadCleanupVerdict(false, "the directory does not exist: " + full, Array.Empty<string>(), false);

        if (entries.Count == 0)
            return DloadCleanupVerdict.Allow("the directory is empty, nothing can be lost: " + full);

        List<string> foreign = entries.Where(entry => !IsOwnEntry(entry)).ToList();
        if (foreign.Count > 0)
            return new DloadCleanupVerdict(false,
                "the directory holds entries this workflow did not create: " + string.Join(", ", foreign.Take(8))
                    + (foreign.Count > 8 ? " (+" + (foreign.Count - 8).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more)" : ""),
                foreign, true);

        bool markerSeen = entries.Any(entry => RequiredMarkers.Any(marker => string.Equals(marker, entry, StringComparison.OrdinalIgnoreCase)));
        if (!markerSeen)
            return new DloadCleanupVerdict(false,
                "no workflow marker (" + string.Join(" / ", RequiredMarkers) + ") was found, so the directory is not identifiable as ours",
                Array.Empty<string>(), true);

        return DloadCleanupVerdict.Allow("every entry is a file this workflow created: " + full);
    }

    /// <summary>
    /// Read the top level of <paramref name="directory"/> and run <see cref="Evaluate"/>. Touches no
    /// device; a listing failure refuses the delete (fail-closed: an unreadable directory is not a
    /// directory we can prove is ours).
    /// </summary>
    public static DloadCleanupVerdict EvaluateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return Evaluate(directory, false, Array.Empty<string>());

        bool exists;
        try { exists = Directory.Exists(directory!); }
        catch (Exception) { exists = false; }

        if (!exists) return Evaluate(directory, false, Array.Empty<string>());

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory!)
                .Select(entry => Path.GetFileName(Path.TrimEndingDirectorySeparator(entry)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DloadCleanupVerdict(false, "the directory could not be listed (" + ex.Message + ")", Array.Empty<string>(), true);
        }

        return Evaluate(directory, true, entries);
    }
}
