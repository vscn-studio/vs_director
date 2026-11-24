using System.Text.Json;
using VSDirector.Data;

internal static class HeadMotionChecks
{
    internal static void Run()
    {
        var clip = new DirectorHeadMotionClip
        {
            Samples = [new(0, 0, 0, 0, 170, 0, 0), new(2, 2, 0, 0, -170, 0, 0, .5f, .8f)], TrimEnd = 2
        };
        Check.True(clip.TrySample(1, out var middle));
        Check.NearlyEqual(180, middle.Yaw); Check.NearlyEqual(1, middle.X); Check.NearlyEqual(.75, middle.FovScale);
        Check.False(clip.TrySample(-1, out _)); Check.False(clip.TrySample(2, out _));
        clip.TrimStart = .5; clip.TrimEnd = 1.5; clip.PlaybackRate = 2;
        Check.NearlyEqual(.5, clip.Duration);
        Check.True(clip.TrySample(.25, out var trimmed)); Check.NearlyEqual(middle.X, trimmed.X);
        clip.Translation = false; clip.Rotation = false; clip.Zoom = false;
        Check.True(clip.TrySample(0, out var disabled)); Check.NearlyEqual(0, disabled.X); Check.NearlyEqual(0, disabled.Yaw); Check.NearlyEqual(1, disabled.FovScale);
        clip.Translation = clip.Rotation = clip.Zoom = true;
        var source = new CameraPreset { X = 12, Y = 14, Z = 16, Yaw = 90, Roll = 0, Fov = 80 };
        var pose = DirectorHeadMotion.Apply(source, new(0, 1, 0, 0, 0, 0, 0, .5f));
        Check.NearlyEqual(12, pose.X); Check.NearlyEqual(15, pose.Z); Check.NearlyEqual(40, pose.Fov);
        Check.NearlyEqual(90, pose.Yaw, .001); Check.NearlyEqual(16, source.Z);
        var tilted = new CameraPreset { Yaw = 25, Pitch = -36, Roll = 52 };
        var identity = DirectorHeadMotion.Apply(tilted, new(0, 0, 0, 0, 0, 0, 0));
        Check.NearlyEqual(tilted.Yaw, identity.Yaw, .001); Check.NearlyEqual(tilted.Pitch, identity.Pitch, .001); Check.NearlyEqual(tilted.Roll, identity.Roll, .001);
        CheckNativeAxes();
        CheckRecordingGroups();

        var timeline = new DirectorReplayViewportTimeline();
        var group = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.PresetGroup };
        var track = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.HeadMotion, ParentGroupId = group.Id };
        var frame = new DirectorReplayElementKeyframe { TrackId = track.Id, TrackType = track.TrackType, TimeSeconds = 3, HeadMotion = clip };
        timeline.Tracks.AddRange([group, track]); timeline.ElementKeyframes.Add(frame);
        foreach (double time in new[] { 3.25, 3.1, 3.25, 3.25 })
        {
            Check.True(DirectorHeadMotion.TrySample(timeline, time, out var sample));
            Check.True(sample.IsFinite);
            Check.NearlyEqual(DirectorHeadMotion.Apply(source, sample).X, DirectorHeadMotion.Apply(source, sample).X);
        }
        Check.False(DirectorHeadMotion.TrySample(timeline, 3.5, out _));
        group.Enabled = false; Check.False(DirectorHeadMotion.TrySample(timeline, 3.25, out _)); group.Enabled = true;
        track.Enabled = false; Check.False(DirectorHeadMotion.TrySample(timeline, 3.25, out _)); track.Enabled = true;
        var copy = timeline.DeepClone(); copy.ElementKeyframes[0].HeadMotion!.Samples.Clear(); Check.Equal(2, clip.Samples.Count);
        var replay = new DirectorReplayDocument { DurationSeconds = 10, ViewportTimeline = timeline };
        var edits = DirectorReplayTimelineEditIO.Create(replay);
        var json = JsonSerializer.Serialize(edits);
        var loaded = JsonSerializer.Deserialize<DirectorReplayTimelineEditDocument>(json)!;
        DirectorReplayTimelineEditIO.Apply(replay, loaded);
        Check.True(DirectorHeadMotion.TrySample(replay.ViewportTimeline, 3.25, out var persisted));
        Check.NearlyEqual(1, persisted.X);
        var history = new DirectorReplayEditHistory();
        var before = new DirectorReplayEditSnapshot(edits, source);
        DirectorReplayViewportTimeline.RemoveTrack(replay.ViewportTimeline, track.Id);
        var after = new DirectorReplayEditSnapshot(DirectorReplayTimelineEditIO.Create(replay), source);
        history.Record(before, after);
        Check.False(DirectorHeadMotion.TrySample(replay.ViewportTimeline, 3.25, out _));
        Check.True(history.Restore(false, after, snapshot => { DirectorReplayTimelineEditIO.Apply(replay, snapshot.Edits); return true; }));
        Check.True(DirectorHeadMotion.TrySample(replay.ViewportTimeline, 3.25, out _));
        clip.Samples.Add(new(double.NaN, 0, 0, 0, 0, 0, 0)); clip.PlaybackRate = double.NaN; clip.Strength = float.PositiveInfinity;
        clip.Normalize(); Check.Equal(2, clip.Samples.Count); Check.NearlyEqual(1, clip.PlaybackRate); Check.NearlyEqual(1, clip.Strength);
    }

    private static void CheckNativeAxes()
    {
        foreach (float yaw in new[] { 0f, 40f, 150f })
        foreach (float pitch in new[] { 180f, 150f, 215f, -24f })
        foreach (float roll in new[] { 0f, 30f, 90f })
        {
            var native = new CameraPreset { X = 512000, Y = 131, Z = 512000, Yaw = yaw, Pitch = pitch, Roll = roll };
            var canonical = native.DeepClone(); CameraViewAngles.Normalize(canonical);
            var basis = DirectorCameraManipulation.CameraHeadBasis(native);
            foreach (var (sample, expected) in new[]
            {
                (new DirectorHeadMotionSample(0, 1, 0, 0, 0, 0, 0), basis.Right),
                (new DirectorHeadMotionSample(0, 0, 1, 0, 0, 0, 0), basis.Up),
                (new DirectorHeadMotionSample(0, 0, 0, 1, 0, 0, 0), basis.Back)
            })
            {
                var result = DirectorHeadMotion.Apply(native, sample);
                Check.NearlyEqual(expected.X, result.X - native.X, .0001);
                Check.NearlyEqual(expected.Y, result.Y - native.Y, .0001);
                Check.NearlyEqual(expected.Z, result.Z - native.Z, .0001);
                Check.NearlyEqual(native.Roll, result.Roll, .001);
            }
            foreach (var sample in new DirectorHeadMotionSample[]
            {
                new(0, 0, 0, 0, 25, 0, 0), new(0, 0, 0, 0, 0, 20, 0),
                new(0, 0, 0, 0, 0, 0, 15), new(0, .2f, -.1f, .3f, -108, -83, 20)
            })
            {
                var a = DirectorHeadMotion.Apply(native, sample);
                var b = DirectorHeadMotion.Apply(canonical, sample);
                Check.NearlyEqual(a.X, b.X, .0001); Check.NearlyEqual(a.Y, b.Y, .0001); Check.NearlyEqual(a.Z, b.Z, .0001);
                var ab = DirectorCameraManipulation.CameraHeadBasis(a); var bb = DirectorCameraManipulation.CameraHeadBasis(b);
                Check.NearlyEqual(0, (ab.Right - bb.Right).Length(), .0001);
                Check.NearlyEqual(0, (ab.Up - bb.Up).Length(), .0001);
                Check.NearlyEqual(0, (ab.Back - bb.Back).Length(), .0001);
            }
        }
        var horizontal = DirectorHeadMotion.Apply(new CameraPreset { Pitch = 180 }, new(0, 0, 0, 0, 25, 0, 0));
        Check.NearlyEqual(0, CameraViewAngles.Forward(horizontal.Yaw, horizontal.Pitch).Y, .001);
        Check.NearlyEqual(0, horizontal.Roll, .001);
        var vertical = DirectorHeadMotion.Apply(new CameraPreset { Pitch = 180 }, new(0, 0, 0, 0, 0, 20, 0));
        Check.NearlyEqual(Math.Sin(20 * Math.PI / 180), CameraViewAngles.Forward(vertical.Yaw, vertical.Pitch).Y, .001);
        Check.NearlyEqual(0, vertical.Roll, .001);
    }

    private static void CheckRecordingGroups()
    {
        var timeline = new DirectorReplayViewportTimeline();
        var track = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.HeadMotion };
        var frame = new DirectorReplayElementKeyframe { TrackId = track.Id, TrackType = track.TrackType, TimeSeconds = 2,
            HeadMotion = new() { Samples = [new(0, 0, 0, 0, 0, 0, 0), new(3, 1, 0, 0, 30, 0, 0)], TrimEnd = 3 } };
        timeline.Tracks.Add(track); timeline.ElementKeyframes.Add(frame);
        var migrated = DirectorReplayViewportTimeline.Normalize(timeline, 10);
        var group = migrated.Tracks.Single(t => t.TrackType == DirectorReplayElementTrackType.PresetGroup);
        Check.Equal(group.Id, DirectorReplayViewportTimeline.Normalize(timeline, 10).Tracks.Single(t => t.TrackType == group.TrackType).Id);
        Check.Equal(1, migrated.VisibleTracks(new HashSet<Guid>()).Count());
        Check.Equal(3, migrated.VisibleTracks(new HashSet<Guid> { group.Id }).Count());
        Check.Equal(0, migrated.Clips.Count); // No fabricated baseline for an old recording.
        var motion = migrated.Tracks.Single(t => t.Id == track.Id);
        var key = migrated.ElementKeyframes.Single();
        DirectorHeadMotion.AddCameraAnchor(migrated, motion, key, new CameraPreset { X = 50, Pitch = 180, Fov = 80 });
        Check.Equal(2, migrated.Clips.Count);
        Check.True(DirectorReplayCameraPriority.ResolveActive(migrated, 2) is not null);
        Check.True(DirectorReplayCameraPriority.ResolveActive(migrated, 4.99) is not null);
        Check.True(DirectorReplayCameraPriority.ResolveActive(migrated, 5) is null);
        group.Enabled = false;
        Check.False(DirectorHeadMotion.TrySample(migrated, 3, out _));
        Check.True(DirectorReplayCameraPriority.ResolveActive(migrated, 3) is null);
        group.Enabled = true;
        key.TimeSeconds = 4; key.HeadMotion!.PlaybackRate = 2;
        DirectorHeadMotion.ResizeCameraAnchor(migrated, key, 2, 3);
        Check.NearlyEqual(4, migrated.Clips[0].TimelineStartSeconds);
        Check.NearlyEqual(5.5, migrated.Clips[1].TimelineStartSeconds);
        key.HeadMotion.TrimEnd = 0;
        DirectorHeadMotion.ResizeCameraAnchor(migrated, key, 4, 1.5);
        Check.True(DirectorReplayCameraPriority.ResolveActive(migrated, 4) is null);
        key.HeadMotion.TrimEnd = 3;
        DirectorHeadMotion.ResizeCameraAnchor(migrated, key, 4, 0);
        Check.True(DirectorReplayCameraPriority.ResolveActive(migrated, 4) is not null);
        migrated.Clips[0].CameraPose!.X = 90;
        DirectorHeadMotion.AddCameraAnchor(migrated, motion, key, new CameraPreset { X = 100 });
        Check.NearlyEqual(90, migrated.Clips[0].CameraPose!.X);
        var restored = DirectorReplayViewportTimeline.Normalize(JsonSerializer.Deserialize<DirectorReplayViewportTimeline>(JsonSerializer.Serialize(migrated))!, 10);
        Check.Equal(3, restored.Tracks.Count); Check.Equal(2, restored.Clips.Count);
        Check.Equal(2, restored.ElementKeyframes.Single().HeadMotion!.Samples.Count);
        Check.True(DirectorReplayViewportTimeline.RemoveTrack(restored, group.Id));
        Check.Equal(0, restored.Tracks.Count); Check.Equal(0, restored.Clips.Count); Check.Equal(0, restored.ElementKeyframes.Count);
    }
}
