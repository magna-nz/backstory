using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Backstory.Adapters;

/// <summary>
/// Recognises export artifacts as they land on disk (for the download watcher) and unpacks
/// Google Takeout zips into a directory the <see cref="GoogleTakeoutAdapter"/> can read.
/// </summary>
public static class ExportFiles
{
    private static readonly string[] PartialDownloadExtensions =
        [".crdownload", ".part", ".tmp", ".download", ".opdownload"];

    /// <summary>
    /// Classifies a path as an importable export trigger: "telegram" (a result.json),
    /// "google_zip" (a downloaded Takeout zip), or null if it isn't one.
    /// </summary>
    public static string? Detect(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name == "result.json") return "telegram";
        if (!name.EndsWith(".zip")) return null;
        if (name.StartsWith("takeout")) return "google_zip";
        if (name.StartsWith("instagram") || name.Contains("spotify")) return "zip";
        return null;
    }

    /// <summary>True while a browser is still writing the file (partial-download extension).</summary>
    public static bool IsPartialDownload(string path) =>
        PartialDownloadExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static readonly Regex PartSuffix = new(@"-\d{3}\.zip$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns every zip that belongs with this one. A large Takeout download comes as
    /// takeout-...-001.zip, -002.zip, … (each holds part of the tree). For a single zip the
    /// list is just that file.
    /// </summary>
    public static IReadOnlyList<string> ZipGroup(string zipPath)
    {
        var full = Path.GetFullPath(zipPath);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        if (!PartSuffix.IsMatch(name)) return [full];

        var prefix = PartSuffix.Replace(name, "");
        return Directory.GetFiles(dir, prefix + "-*.zip").OrderBy(f => f).ToArray();
    }

    /// <summary>
    /// Extracts one or more zips into a single directory under <paramref name="baseDir"/> and
    /// returns it. Passing all parts of a multi-part Takeout merges them back into the full tree.
    /// </summary>
    public static string ExtractZips(IReadOnlyList<string> zips, string baseDir)
    {
        var folderName = Regex.Replace(Path.GetFileNameWithoutExtension(zips[0]), @"-\d{3}$", "");
        var dest = Path.Combine(baseDir, folderName);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        foreach (var zip in zips)
            ZipFile.ExtractToDirectory(zip, dest, overwriteFiles: true);
        return dest;
    }
}
