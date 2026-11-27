using VSDirector.Data;

internal static class ReplayPagingChecks
{
    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "director-paging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CheckPrimary(folder);
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

    private static void CheckPrimary(string folder)
    {
        string path = Path.Combine(folder, "indexed.vsdreplay");
        var doc = new DirectorReplayDocument { RecordedGameVersion = "v1.22.3 (Stable)", NativePayloadVersion = 1 };
        var actor = new DirectorReplayActorTrack { ActorKey = "moving", InitialState = [1, 2] };
        for (int i = 0; i < 145; i++)
        {
            bool equipped = i < 64 || i >= 90;
            actor.Samples.Add(new()
            {
                TimeSeconds = i / 30d + (i < 80 ? 0 : 8), X = i * .000123456789, Yaw = i * .01f,
                CanStandUp = equipped, RightHandItem = equipped ? [0, 255] : [],
                EquipmentItems = equipped ? [[1, 2], []] : [], BackpackItems = equipped ? [[3]] : [],
                Animations = equipped ? ["walk"] : [],
                AnimationStates = equipped ? [new() { Code = "walk", RecordedFrame = i < 35 ? null : 5.5f,
                    IsRunning = i != 40, ElementWeights = new() { ["Head"] = i == 41 ? .5f : .25f },
                    ElementBlendModes = new() { ["Head"] = i == 42 ? 3 : 2 } }] : []
            });
        }
        actor.States.Add(new() { TimeSeconds = 1, Data = [7] });
        doc.Actors.Add(actor);
        doc.WorldSnapshot.Chunks.Add(new() { CapturedAtSeconds = 1, SolidRuns = [new() { BlockId = 9, Length = 32768 }] });
        doc.WorldSnapshot.BlockEntities.Add(new() { ClassName = "KnappingSurface", Data = [9, 8] });
        doc.BlockEvents.Add(new() { TimeSeconds = 2, BlockEntityCaptured = true, BlockEntityData = [6] });
        var expected = DirectorReplayMath.Normalize(doc);
        DirectorReplayIO.Save(path, doc);
        Require(!File.Exists(path + ".pages"), "New recording duplicated its primary file.");
        var loaded = DirectorReplayIO.LoadPagedAsync(path).GetAwaiter().GetResult();
        var pages = loaded.Pages!;
        pages.PrefetchEnabled = false;
        Require(pages.PagesRead == 0 && loaded.RecordedGameVersion == doc.RecordedGameVersion && loaded.NativePayloadVersion == 1, "Index or recording origin differs.");
        var track = loaded.Actors.Single();
        foreach (int i in new[] { 144, 79, 80, 63, 64, 89, 90, 0 }.Concat(Enumerable.Range(0, 145)))
        {
            var sample = track.Samples[i];
            Require(System.Text.Json.JsonSerializer.Serialize(sample) == System.Text.Json.JsonSerializer.Serialize(expected.Actors[0].Samples[i]), $"Delta mismatch at {i}.");
            Require(DirectorReplayTimeSearch.LowerBound(track.Samples, sample.TimeSeconds, s => s.TimeSeconds) == i, "Time seek mismatch.");
        }
        track.Samples[0].RightHandItem[0] = 99;
        Require(track.Samples[1].RightHandItem[0] == 0, "Mutable equipment leaked between samples.");
        Require(track.InitialState.SequenceEqual(new byte[]{1, 2}) && track.States[0].Data.Single() == 7, "Native actor state lost.");
        Require(loaded.WorldSnapshot.Chunks[0].SolidRuns[0].BlockId == 9 && loaded.WorldSnapshot.BlockEntities[0].Data[1] == 8
            && loaded.BlockEvents[pages.BlockPositions.Single().Indices.Single()].BlockEntityData[0] == 6, "Historical terrain lost.");
        var editable = DirectorReplayIO.LoadAsync(path).GetAwaiter().GetResult();
        Require(!editable.Actors[0].Samples.IsReadOnly && editable.Actors[0].Samples[0].RightHandItem[0] == 0, "Eager load must be independently editable.");
        var stale = DirectorReplayPages.TryOpen(path)!;
        try { DirectorReplayPages.Write(path, expected, token: new CancellationToken(true)); throw new Exception("Cancelled save committed."); }
        catch (OperationCanceledException) { }
        Require(stale.Actors[0].Samples[0].X == 0, "Cancelled replacement damaged previous recording.");
        DirectorReplayIO.Save(path, doc);
        try { _ = stale.Actors[0].InitialState; throw new Exception("Reader mixed two replay generations."); }
        catch (IOException) { }
        using (var file = File.OpenWrite(path)) { file.Position = 12; file.WriteByte(99); }
        try { DirectorReplayIO.LoadPagedAsync(path).GetAwaiter().GetResult(); throw new Exception("Future container accepted."); }
        catch (NotSupportedException) { }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
