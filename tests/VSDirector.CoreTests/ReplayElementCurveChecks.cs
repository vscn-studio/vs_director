using VSDirector.Data;

internal static class ReplayElementCurveChecks
{
    public static void Run()
    {
        var frames = new List<DirectorReplayElementKeyframe>();
        double[] times = { 0d, 0.3d, 2d, 7d, 8d };
        double[] values = { 1d, 4d, 5d, 12d, 13d };
        for (int i = 0; i < times.Length; i++)
            frames.Add(new() { TimeSeconds = times[i], X = values[i],
                TrackType = DirectorReplayElementTrackType.CameraOrbit });

        foreach (var mode in Enum.GetValues<DirectorReplayElementInterpolation>())
        {
            foreach (var frame in frames) frame.InterpolationToNext = mode;
            for (int i = 0; i + 1 < frames.Count; i++)
            {
                double previous = frames[i].X;
                for (int step = 0; step <= 100; step++)
                {
                    double time = times[i] + (times[i + 1] - times[i]) * step / 100d;
                    Check.True(DirectorReplayElementTimeline.TrySample(frames,
                        DirectorReplayElementTrackType.CameraOrbit, time, out var sample));
                    Check.True(double.IsFinite(sample.X));
                    if (mode != DirectorReplayElementInterpolation.Hermite)
                    {
                        Check.True(sample.X >= previous - 1e-8);
                        Check.True(sample.X >= values[i] - 1e-8 && sample.X <= values[i + 1] + 1e-8);
                    }
                    previous = sample.X;
                }
            }
        }
        foreach (var mode in Enum.GetValues<DirectorReplayElementInterpolation>())
        {
            frames[1].InterpolationToNext = mode;
            DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.CameraOrbit,
                1d, out var expected);
            foreach (var neighborMode in Enum.GetValues<DirectorReplayElementInterpolation>())
            {
                frames[0].InterpolationToNext = neighborMode;
                frames[2].InterpolationToNext = neighborMode;
                DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.CameraOrbit,
                    1d, out var actual);
                Check.NearlyEqual(expected.X, actual.X);
            }
        }
        frames[0].InterpolationToNext = DirectorReplayElementInterpolation.Linear;
        frames[1].InterpolationToNext = DirectorReplayElementInterpolation.EaseOut;
        DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.CameraOrbit,
            0.075d, out var quarter);
        Check.NearlyEqual(1.75d, quarter.X); // The next key's mode cannot reshape this segment.
        frames[0].Roll = 170f;
        frames[1].Roll = -170f;
        frames[0].TimeOfDayTick = 23000;
        frames[1].TimeOfDayTick = 1000;
        DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.CameraOrbit,
            0.15d, out var wrapped);
        Check.NearlyEqual(180d, Math.Abs(wrapped.Roll));
        Check.Equal(0, wrapped.TimeOfDayTick);

        // Local cubic interpolation cannot be affected by distant edits.
        frames[0].InterpolationToNext = DirectorReplayElementInterpolation.Hermite;
        DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.CameraOrbit,
            0.15d, out var local);
        frames[4].X = -100000d;
        DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.CameraOrbit,
            0.15d, out var unchanged);
        Check.NearlyEqual(local.X, unchanged.X);

        var freeze = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.Freeze };
        var key = new DirectorReplayElementKeyframe { TrackId = freeze.Id,
            TrackType = freeze.TrackType, Frozen = true, TimeSeconds = 1d };
        var timeline = new DirectorReplayViewportTimeline();
        timeline.Tracks.Add(freeze);
        timeline.ElementKeyframes.Add(key);
        for (int delay = 0; delay <= DirectorReplayElementTimeline.MaxFreezeDelayTicks; delay++)
        {
            key.FreezeDelayTicks = delay;
            double previous = 1d;
            double previousSpeed = 1d;
            for (int step = 1; step <= 100; step++)
            {
                double elapsed = step * 0.01d;
                var state = DirectorReplayElementTimeline.SampleFreeze(timeline, 1d + elapsed);
                double speed = (state.ReplayTimeSeconds - previous) / 0.01d;
                Check.True(speed >= -1e-8 && speed <= previousSpeed + 1e-8);
                Check.True(state.ReplayTimeSeconds <= 1d + elapsed + 1e-8);
                previousSpeed = speed;
                previous = state.ReplayTimeSeconds;
            }
            Check.NearlyEqual(1d + delay / (3d * DirectorReplayElementTimeline.TicksPerSecond), previous);
            Check.True(DirectorReplayElementTimeline.SampleFreeze(timeline, 2d).FullyFrozen);
        }

        // Capture and evaluation must be inverses across the full orientation range.
        for (int yaw = -180; yaw <= 180; yaw += 30)
        for (int pitch = -90; pitch <= 90; pitch += 15)
        {
            var target = new DirectorReplayTrackedTargetPose(10d, -3d, 70d, 2, 27d, -13d);
            var camera = new CameraPreset { X = 18d, Y = 15d, Z = 61d,
                Yaw = yaw, Pitch = pitch, Roll = 14f };
            var tracking = new DirectorReplayElementKeyframe();
            DirectorReplayCameraElementMath.ConfigureTrackFromCamera(tracking, target, camera);
            var restored = DirectorReplayCameraElementMath.EvaluateTrack(tracking, target, 70f);
            Check.NearlyEqual(camera.X, restored.X);
            Check.NearlyEqual(camera.Y, restored.Y);
            Check.NearlyEqual(camera.Z, restored.Z);
            Check.NearlyEqual(camera.Yaw, restored.Yaw);
            Check.NearlyEqual(camera.Pitch, restored.Pitch);
            Check.NearlyEqual(camera.Roll, restored.Roll);
        }
    }
}
