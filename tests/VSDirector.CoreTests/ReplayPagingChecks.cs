using VSDirector.Data;

internal static class ReplayPagingChecks
{
    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "director-paging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "long.vsdreplay.gz");
            var doc = new DirectorReplayDocument { Name = "Long", WorldId = "test", DurationSeconds = 10000 };
            var track = new DirectorReplayActorTrack { ActorKey = "a", EntityCode = "test:actor", InitialState = [1, 2, 3] };
            for (int i = 0; i < 10000; i++)
            {
                track.Samples.Add(new() { TimeSeconds = i, X = i * .000123456789, Y = 5 });
                doc.BlockEvents.Add(new() { TimeSeconds = i, X = 4, Y = 5, Z = 6, SolidBlockId = i % 3 });
                doc.VoiceFrames.Add(new() { TimeSeconds = i, SourceId = "v", Pcm16 = new byte[1024] });
            }
            doc.Actors.Add(track);
            doc.WorldSnapshot.Chunks.Add(new() { SolidRuns = [new() { BlockId = 2, Length = 32768 }], FluidRuns = [new() { Length = 32768 }] });
            doc.WorldSnapshot.BlockEntities.Add(new() { X = 1, Y = 2, Z = 3, ClassName = "test", Data = [9, 8] });
            DirectorReplayIO.Save(path, doc);
            var loaded = DirectorReplayIO.LoadPagedAsync(path).GetAwaiter().GetResult();
            loaded.Pages!.PrefetchEnabled = false;
            Require(loaded.Pages is { PagesRead: 0 }, "Opening metadata decoded payload pages.");
            Require(loaded.Actors[0].Samples.Count == 10000 && loaded.BlockEvents.Count == 10000, "Paged counts differ.");
            Require(loaded.Actors[0].Samples[9876].X == track.Samples[9876].X, "Far seek changed sample precision.");
            Require(loaded.Actors[0].Samples[12].X == track.Samples[12].X, "Reverse seek failed.");
            Require(loaded.Pages!.PagesRead == 2, "Random actor seek scanned intermediate pages.");
            long beforeSampling = loaded.Pages.PagesRead;
            var sampled = new DirectorReplayActorSample();
            DirectorReplayMath.TrySample(loaded.Actors[0], 6401.5, sampled);
            DirectorReplayMath.TrySamplePosition(loaded.Actors[0], 6401.5, out var position);
            Require(Math.Abs(sampled.X - position.X) < 1e-12, "Indexed pose sampling disagrees with position sampling.");
            Require(loaded.Pages.PagesRead - beforeSampling == 1, "Timestamp search decoded unrelated actor pages.");
            beforeSampling = loaded.Pages.PagesRead;
            for (int frame = 0; frame < 120; frame++) DirectorReplayMath.TrySample(loaded.Actors[0], 6401.5 + frame / 120d, sampled);
            Require(loaded.Pages.PagesRead == beforeSampling, "Repeated playback sampling thrashed the page cache.");
            Require(loaded.Pages.BlockPositions.Single().Indices.Length == 10000, "Block position index lost history.");
            Require(loaded.WorldSnapshot.Chunks[0].SolidRuns[0].BlockId == 2 && loaded.WorldSnapshot.BlockEntities[0].Data.SequenceEqual(new byte[] {9, 8}), "Lazy baseline payload differs.");
            Require(loaded.Actors[0].InitialState.SequenceEqual(new byte[] {1, 2, 3}), "Lazy actor initial state differs.");
            for (int i = 0; i < 10000; i += 256) _ = loaded.VoiceFrames[i];
            Require(loaded.Pages.ResidentBytes <= 16 * 1024 * 1024, "Page cache exceeded its budget.");
            Require(loaded.VoiceFrames[0].Pcm16.Length == 1024, "Evicted page could not be read again.");
            Require(DirectorReplayExportIntegrity.Validate(10, 10, 0, 0, 0).Length == 0, "Complete export rejected.");
            Require(DirectorReplayExportIntegrity.Validate(10, 10, 1, 1, 1).Length > 0, "Dropped frames hidden by padding were accepted.");
            Require(DirectorReplayExportIntegrity.Validate(10, 9, 0, 0, 0).Length > 0, "Truncated export accepted.");
            var cancellation = new CancellationToken(true);
            try { DirectorReplayIO.LoadPagedAsync(path, cancellationToken: cancellation).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Require(DirectorReplayPages.TryOpen(path) is null, "Stale page cache was accepted.");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
