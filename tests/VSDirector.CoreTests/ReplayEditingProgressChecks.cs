using VSDirector.Data;

internal static class ReplayEditingProgressChecks
{
    internal static void Run()
    {
        var history = new DirectorReplayEditHistory();
        var replay = new DirectorReplayDocument { DurationSeconds = 20 };
        var pose = new CameraPreset { X = 12, Yaw = 40, Roll = 7, Fov = 83 };
        DirectorReplayEditSnapshot Snapshot() => new(DirectorReplayTimelineEditIO.Create(replay), pose.DeepClone());
        var before = Snapshot();
        replay.ViewportTimeline.Visuals.Environment.Weather = DirectorReplayWeatherMode.Rain;
        replay.ViewportTimeline.Visuals.Composition.CenterGuide = true;
        replay.Markers.Add(new() { Label = "preset marker", TimeSeconds = 3 });
        var fovTrack = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.Fov };
        replay.ViewportTimeline.Tracks.Add(fovTrack);
        replay.ViewportTimeline.ElementKeyframes.Add(new() { TrackId = fovTrack.Id, TrackType = fovTrack.TrackType, Fov = 95 });
        var after = Snapshot();
        history.Record(before, after);
        // Stored snapshots must not follow later UI edits or a failed save.
        before.Edits.ViewportTimeline.Visuals.Composition.CenterGuide = true;
        Check.False(history.Restore(false, after, _ => false));
        Check.True(history.CanUndo); Check.False(history.CanRedo);
        bool Apply(DirectorReplayEditSnapshot snapshot)
        {
            DirectorReplayTimelineEditIO.Apply(replay, snapshot.Edits);
            pose = snapshot.Camera!.DeepClone();
            return true;
        }
        Check.True(history.Restore(false, after, Apply));
        Check.False(replay.ViewportTimeline.Visuals.Composition.CenterGuide);
        Check.False(replay.ViewportTimeline.Visuals.Environment.Weather == DirectorReplayWeatherMode.Rain);
        Check.Equal(0, replay.Markers.Count);
        Check.Equal(0, replay.ViewportTimeline.ElementKeyframes.Count);
        Check.NearlyEqual(83, pose.Fov, .001); Check.NearlyEqual(7, pose.Roll, .001);
        Check.True(history.Restore(true, Snapshot(), Apply));
        Check.Equal(DirectorReplayWeatherMode.Rain, replay.ViewportTimeline.Visuals.Environment.Weather);
        Check.Equal(1, replay.Markers.Count); Check.Equal(1, replay.ViewportTimeline.ElementKeyframes.Count);
        Check.True(history.Restore(false, Snapshot(), Apply));
        var branch = Snapshot(); replay.ViewportTimeline.Visuals.Composition.CenterGuide = true;
        history.Record(branch, Snapshot()); Check.False(history.CanRedo);
        history.Select(Guid.NewGuid()); Check.False(history.CanUndo);

        var progress = new DirectorOperationProgress();
        DirectorOperationStatus loading = new(true, false, .1, "document", "Loading", "Read document", "");
        progress.Update(loading, 0);
        for (int i = 1; i < 60; i++) progress.Update(loading, i / 60d);
        Check.Equal(1, progress.Lines.Count);
        progress.Update(loading with { Phase = "chunks", Detail = "Loading terrain" }, .99);
        Check.Equal(2, progress.Lines.Count);
        var export = loading with { Export = true, Phase = "rendering", Detail = "Frame 1", Progress = .2 };
        progress.Update(export, 1); Check.Equal(1, progress.Lines.Count);
        for (int i = 2; i < 200; i++) progress.Update(export with { Detail = $"Frame {i}" }, i);
        Check.Equal(150, progress.Lines.Count);
        progress.Update(export with { Active = false, Phase = "error", Error = "Encoder failed", Progress = double.NaN }, 200);
        Check.True(progress.Lines[^1].Contains("Encoder failed")); Check.NearlyEqual(0, progress.Current!.Progress, .001);
        progress.Update(loading, 201); Check.Equal(1, progress.Lines.Count);
        progress.Clear(); Check.True(progress.Current is null); Check.Equal(0, progress.Lines.Count);
    }
}
