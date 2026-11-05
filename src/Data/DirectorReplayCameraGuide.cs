namespace VSDirector.Data;

public readonly record struct DirectorReplayOrbitGuideSample(
    double TimeSeconds,
    CameraPreset Camera);

public readonly record struct DirectorReplayOrbitGuideKeyframe(
    double TimeSeconds,
    CameraPreset Camera,
    double CenterX,
    double CenterY,
    double CenterZ,
    int Dimension);

public sealed class DirectorReplayOrbitGuide
{
    public Guid TrackId { get; init; }

    public List<DirectorReplayOrbitGuideSample> Samples { get; init; } = new();

    public List<DirectorReplayOrbitGuideKeyframe> Keyframes { get; init; } = new();
}

public static class DirectorReplayCameraGuide
{
    public const int DefaultOrbitSamplesPerSegment = 24;

    public static DirectorReplayOrbitGuide BuildOrbit(
        DirectorReplayViewportTimeline timeline,
        Guid trackId,
        Func<double, float>? fovProvider = null,
        int samplesPerSegment = DefaultOrbitSamplesPerSegment)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        samplesPerSegment = Math.Clamp(samplesPerSegment, 1, 256);

        List<DirectorReplayElementKeyframe> keyframes = timeline.ElementKeyframes
            .Where(keyframe => keyframe is not null
                && keyframe.TrackId == trackId
                && keyframe.TrackType == DirectorReplayElementTrackType.CameraOrbit)
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .ThenBy(keyframe => keyframe.Id)
            .ToList();
        DirectorReplayOrbitGuide guide = new() { TrackId = trackId };
        if (keyframes.Count == 0)
        {
            return guide;
        }

        float FovAt(double timeSeconds)
        {
            float value = fovProvider?.Invoke(timeSeconds) ?? 70f;
            return float.IsFinite(value)
                ? Math.Clamp(
                    value,
                    DirectorReplayElementTimeline.MinFovDegrees,
                    DirectorReplayElementTimeline.MaxFovDegrees)
                : 70f;
        }

        foreach (DirectorReplayElementKeyframe keyframe in keyframes)
        {
            guide.Keyframes.Add(new DirectorReplayOrbitGuideKeyframe(
                keyframe.TimeSeconds,
                DirectorReplayCameraElementMath.EvaluateOrbit(keyframe, FovAt(keyframe.TimeSeconds)),
                keyframe.X,
                keyframe.Y,
                keyframe.Z,
                keyframe.Dimension));
        }

        AddSample(keyframes[0]);
        for (int index = 0; index < keyframes.Count - 1; index++)
        {
            DirectorReplayElementKeyframe left = keyframes[index];
            DirectorReplayElementKeyframe right = keyframes[index + 1];
            if (right.TimeSeconds <= left.TimeSeconds)
            {
                continue;
            }

            for (int sampleIndex = 1; sampleIndex <= samplesPerSegment; sampleIndex++)
            {
                double timeSeconds = left.TimeSeconds
                    + (right.TimeSeconds - left.TimeSeconds) * sampleIndex / samplesPerSegment;
                if (DirectorReplayElementTimeline.TrySample(
                        timeline.ElementKeyframes,
                        DirectorReplayElementTrackType.CameraOrbit,
                        trackId,
                        timeSeconds,
                        out DirectorReplayElementKeyframe sample))
                {
                    AddSample(sample);
                }
            }
        }

        return guide;

        void AddSample(DirectorReplayElementKeyframe sample)
        {
            guide.Samples.Add(new DirectorReplayOrbitGuideSample(
                sample.TimeSeconds,
                DirectorReplayCameraElementMath.EvaluateOrbit(sample, FovAt(sample.TimeSeconds))));
        }
    }
}
