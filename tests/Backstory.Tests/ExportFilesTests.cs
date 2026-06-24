using System.IO.Compression;
using Backstory.Adapters;
using Backstory.Core;

namespace Backstory.Tests;

public class ExportFilesTests
{
    [Fact]
    public void Detect_classifies_known_export_artifacts()
    {
        Assert.Equal("telegram", ExportFiles.Detect("/downloads/result.json"));
        Assert.Equal("google_zip", ExportFiles.Detect("/downloads/takeout-20240101T120000Z-001.zip"));
        Assert.Null(ExportFiles.Detect("/downloads/notes.txt"));
        Assert.Null(ExportFiles.Detect("/downloads/holiday.zip"));
    }

    [Fact]
    public void IsPartialDownload_spots_in_progress_downloads()
    {
        Assert.True(ExportFiles.IsPartialDownload("/downloads/takeout.zip.crdownload"));
        Assert.True(ExportFiles.IsPartialDownload("/downloads/takeout.zip.part"));
        Assert.False(ExportFiles.IsPartialDownload("/downloads/takeout.zip"));
    }

    [Fact]
    public async Task ExtractZip_then_takeout_adapter_imports()
    {
        using var tmp = new TempDir();

        // Build a minimal Takeout tree and zip it like a real download.
        var src = Path.Combine(tmp.Path, "src");
        Directory.CreateDirectory(Path.Combine(src, "My Activity", "Search"));
        File.WriteAllText(
            Path.Combine(src, "My Activity", "Search", "MyActivity.json"),
            """[ { "header": "Search", "title": "Searched for ramen near me", "time": "2024-03-10T12:00:00.000Z" } ]""");

        var zip = Path.Combine(tmp.Path, "takeout-test.zip");
        ZipFile.CreateFromDirectory(src, zip);

        var dest = ExportFiles.ExtractZips([zip], Path.Combine(tmp.Path, "imports"));

        var adapter = new GoogleTakeoutAdapter();
        Assert.True(adapter.CanHandle(dest));

        var events = new List<Event>();
        await foreach (var item in adapter.ParseAsync(dest))
            if (item is EventItem e) events.Add(e.Event);

        Assert.Contains(events, e => e.SubType == "search_query" && e.Text.Contains("ramen"));
    }

    [Fact]
    public async Task Multipart_takeout_zips_merge_into_one_import()
    {
        using var tmp = new TempDir();

        // Part 1 holds Search; part 2 holds YouTube. They share a directory tree once merged.
        var p1 = Path.Combine(tmp.Path, "p1", "My Activity", "Search");
        Directory.CreateDirectory(p1);
        File.WriteAllText(Path.Combine(p1, "MyActivity.json"),
            """[ { "header": "Search", "title": "Searched for ramen", "time": "2024-03-10T12:00:00Z" } ]""");

        var p2 = Path.Combine(tmp.Path, "p2", "YouTube and YouTube Music", "history");
        Directory.CreateDirectory(p2);
        File.WriteAllText(Path.Combine(p2, "watch-history.json"),
            """[ { "header": "YouTube", "title": "Watched Tokyo vlog", "time": "2024-03-11T12:00:00Z" } ]""");

        ZipFile.CreateFromDirectory(Path.Combine(tmp.Path, "p1"), Path.Combine(tmp.Path, "takeout-acct-001.zip"));
        ZipFile.CreateFromDirectory(Path.Combine(tmp.Path, "p2"), Path.Combine(tmp.Path, "takeout-acct-002.zip"));

        var group = ExportFiles.ZipGroup(Path.Combine(tmp.Path, "takeout-acct-001.zip"));
        Assert.Equal(2, group.Count);

        var dest = ExportFiles.ExtractZips(group, Path.Combine(tmp.Path, "imports"));

        var events = new List<Event>();
        await foreach (var item in new GoogleTakeoutAdapter().ParseAsync(dest))
            if (item is EventItem e) events.Add(e.Event);

        Assert.Contains(events, e => e.SubType == "search_query");
        Assert.Contains(events, e => e.SubType == "youtube_watch");
    }
}
