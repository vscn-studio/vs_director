using System.Diagnostics;
using System.IO.Compression;
using VSDirector.Data;

internal static class ReplayStorageMeasurement
{
    // The input cache must already exist. A benchmark never regenerates a user's sidecars.
    internal static int WriteCopy(string source, string directory)
    {
        var old = DirectorReplayPages.TryOpen(source) ?? throw new InvalidDataException("Valid source index required.");
        old.Pages!.PrefetchEnabled = false;
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, Guid.NewGuid().ToString("N") + DirectorReplayPages.PrimaryExtension);
        DirectorReplayPages.Write(target, old, compression: CompressionLevel.Optimal);
        Console.WriteLine(target);
        return 0;
    }

    // Run in a fresh process for each format. Wall time includes JIT and index parsing.
    internal static int Measure(string path, bool eager = false)
    {
        using var process = Process.GetCurrentProcess();
        long initial = process.WorkingSet64;
        var timer = Stopwatch.StartNew();
        var doc = eager ? DirectorReplayIO.Load(path) :
            DirectorReplayPages.TryOpen(path) ?? throw new InvalidDataException("Valid index required.");
        long indexMs = timer.ElapsedMilliseconds;
        var pages = doc.Pages;
        if (pages != null) pages.PrefetchEnabled = false;
        long atOpen = pages?.PagesRead ?? 0;
        int visible = 0;
        var pose = new DirectorReplayActorSample();
        foreach (var actor in doc.Actors)
            if (DirectorReplayMath.IsActorVisible(actor, 0) && DirectorReplayMath.TrySample(actor, 0, pose)) visible++;
        process.Refresh();
        Console.WriteLine($"bytes={new FileInfo(path).Length} openMs={indexMs} openPages={atOpen} " +
            $"openAndSampleMs={timer.ElapsedMilliseconds} visible={visible} pages={pages?.PagesRead ?? 0} " +
            $"peakWorkingSetDeltaBytes={process.PeakWorkingSet64 - initial}");
        return 0;
    }
}
