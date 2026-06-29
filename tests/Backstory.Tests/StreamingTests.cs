using Backstory.Adapters;
using Backstory.Core;

namespace Backstory.Tests;

public class StreamingTests
{
    [Fact]
    public async Task Telegram_streams_a_large_export_without_loading_it_all()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "result.json");
        const int messageCount = 100_000;
        WriteLargeTelegramExport(file, messageCount);
        var fileSize = new FileInfo(file).Length;

        // Measure the live managed heap while streaming. If the adapter loaded the whole file (as it
        // used to), the heap would hold the entire document. Streaming keeps it to a small buffer.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peakLive = 0;

        var events = 0;
        var foundLast = false;
        await foreach (var item in new TelegramAdapter().ParseAsync(file))
        {
            if (item is EventItem(var ev))
            {
                events++;
                if (ev.Text.Contains("FINAL_MESSAGE_MARKER")) foundLast = true;
                if (events % 20_000 == 0)
                    peakLive = Math.Max(peakLive, GC.GetTotalMemory(forceFullCollection: true) - baseline);
            }
        }

        Assert.Equal(messageCount, events);          // every message parsed
        Assert.True(foundLast, "the last message should be parsed");
        Assert.True(fileSize > 10_000_000, $"fixture should be large enough to matter ({fileSize} bytes)");
        Assert.True(peakLive < fileSize / 4,
            $"peak live heap {peakLive:N0} should be far below the file size {fileSize:N0} (proves streaming)");
    }

    [Fact]
    public async Task Telegram_streams_messages_split_across_buffer_boundaries()
    {
        // Messages with large text force objects to straddle the pipe's internal buffers, exercising
        // the partial-token rewind path.
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "result.json");
        var big = new string('x', 200_000); // each message is larger than a default pipe segment
        using (var w = new StreamWriter(file))
        {
            w.Write("""{"chats":{"list":[{"name":"C","type":"personal_chat","id":1,"messages":[""");
            for (var i = 0; i < 50; i++)
            {
                if (i > 0) w.Write(',');
                w.Write($$"""{"id":{{i}},"type":"message","date_unixtime":"{{1700000000 + i}}","from":"A","from_id":"u1","text":"{{big}} {{i}}"}""");
            }
            w.Write("]}]}}");
        }

        var events = 0;
        await foreach (var item in new TelegramAdapter().ParseAsync(file))
            if (item is EventItem) events++;

        Assert.Equal(50, events);
    }

    [Fact]
    public async Task Google_streams_a_large_activity_file_without_loading_it_all()
    {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "My Activity", "Search");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "MyActivity.json");

        const int recordCount = 100_000;
        using (var w = new StreamWriter(file))
        {
            w.Write('[');
            for (var i = 0; i < recordCount; i++)
            {
                if (i > 0) w.Write(',');
                w.Write($$"""{"header":"Search","title":"Searched for thing number {{i}} with padding","time":"2023-01-01T12:00:{{i % 60:D2}}.000Z"}""");
            }
            w.Write(']');
        }
        var fileSize = new FileInfo(file).Length;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peakLive = 0;

        var events = 0;
        await foreach (var item in new GoogleTakeoutAdapter().ParseAsync(tmp.Path))
        {
            if (item is EventItem) events++;
            if (events % 20_000 == 0)
                peakLive = Math.Max(peakLive, GC.GetTotalMemory(forceFullCollection: true) - baseline);
        }

        Assert.Equal(recordCount, events);
        Assert.True(fileSize > 10_000_000, $"fixture should be large enough to matter ({fileSize} bytes)");
        Assert.True(peakLive < fileSize / 4,
            $"peak live heap {peakLive:N0} should be far below the file size {fileSize:N0} (proves streaming)");
    }

    private static void WriteLargeTelegramExport(string file, int messageCount)
    {
        using var w = new StreamWriter(file);
        w.Write("""{"contacts":{"list":[{"first_name":"Sam","phone_number":"+1555"}]},"chats":{"list":[{"name":"Big Chat","type":"personal_chat","id":1,"messages":[""");
        for (var i = 0; i < messageCount; i++)
        {
            if (i > 0) w.Write(',');
            var text = i == messageCount - 1
                ? "FINAL_MESSAGE_MARKER the very last one"
                : $"message {i} with some padding text to make the export realistically large";
            w.Write($$"""{"id":{{i}},"type":"message","date_unixtime":"{{1700000000 + i}}","from":"User","from_id":"u1","text":"{{text}}"}""");
        }
        w.Write("]}]}}");
    }
}
