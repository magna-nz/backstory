using System.IO.Compression;

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
        if (name.StartsWith("takeout") && name.EndsWith(".zip")) return "google_zip";
        return null;
    }

    /// <summary>True while a browser is still writing the file (partial-download extension).</summary>
    public static bool IsPartialDownload(string path) =>
        PartialDownloadExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Extracts a zip into <paramref name="baseDir"/>/&lt;zip-name&gt; and returns that directory.</summary>
    public static string ExtractZip(string zipPath, string baseDir)
    {
        var dest = Path.Combine(baseDir, Path.GetFileNameWithoutExtension(zipPath));
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        ZipFile.ExtractToDirectory(zipPath, dest);
        return dest;
    }
}
