namespace VSDirector.Data;

public enum DirectorReplayViewportSourceType
{
    FreeCamera,
    StaticCamera,
    Path,
    Sequence,
    Follow
}

public enum DirectorReplayElementTrackType
{
    Camera = 0,
    Marker = 1,
    Speed = 2,
    CameraOrbit = 3,
    TrackEntity = 4,
    CameraShake = 5,
    Fov = 6,
    Timelapse = 7,
    TimeOfDay = 8,
    Freeze = 9,
    BlockOverride = 10,
    /// <summary>Legacy manually-triggered audio element. Removed during normalization.</summary>
    Audio = 11,
    VoiceAudio = 12,
    FollowEntity = 13
}

public enum DirectorReplayElementInterpolation
{
    Hold,
    Linear,
    EaseInOut,
    Smooth,
    EaseIn,
    EaseOut,
    Hermite
}

public enum DirectorReplayTimelapseKeyframeRole
{
    Start,
    End
}

public enum DirectorReplayFollowEntityKeyframeRole
{
    Start,
    End
}

/// <summary>
/// A persisted timeline row. Tracks have identity so multiple rows of the
/// same Flashback element type can coexist and be enabled independently.
/// </summary>
public sealed class DirectorReplayElementTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DirectorReplayElementTrackType TrackType { get; set; }

    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = string.Empty;

    /// <summary>Stable speaker identity for an automatically-created voice row.</summary>
    public string SpeakerId { get; set; } = string.Empty;

    public int Color { get; set; }

    /// <summary>
    /// Playback duration of a timelapse segment in seconds. A value of zero
    /// keeps legacy tracks on their keyframe-spacing behaviour.
    /// </summary>
    public double TimelapseDurationSeconds { get; set; }

    public DirectorReplayElementTrack DeepClone()
        => (DirectorReplayElementTrack)MemberwiseClone();
}

/// <summary>
/// Replay-local keyframe data for Flashback-style element tracks that are not
/// represented by the legacy camera, marker, and speed collections.
/// </summary>
public sealed class DirectorReplayElementKeyframe
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DirectorReplayElementTrackType TrackType { get; set; }

    public Guid TrackId { get; set; }

    public double TimeSeconds { get; set; }

    public DirectorReplayElementInterpolation InterpolationToNext { get; set; }
        = DirectorReplayElementInterpolation.Smooth;

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public int Dimension { get; set; }

    public double Distance { get; set; } = 8d;

    public float Yaw { get; set; }

    public float Pitch { get; set; }

    public float Roll { get; set; }

    public float Fov { get; set; } = 70f;

    public long EntityId { get; set; }

    /// <summary>
    /// Stable replay actor binding used by entity picking and tracking. Entity
    /// ids can be reused by the game after a despawn, so old drafts continue
    /// to use EntityId as a compatibility fallback while new edits persist
    /// this key first.
    /// </summary>
    public string EntityActorKey { get; set; } = string.Empty;

    public string BodyPart { get; set; } = "head";

    public bool OpenClearView { get; set; }

    public DirectorReplayFollowEntityKeyframeRole FollowEntityRole { get; set; }
        = DirectorReplayFollowEntityKeyframeRole.Start;

    public CameraFollowViewMode FollowViewMode { get; set; }
        = CameraFollowViewMode.ThirdPerson;

    public CameraFollowMotionMode FollowMotionMode { get; set; }
        = CameraFollowMotionMode.Smooth;

    public CameraFollowAnchor FollowAnchor { get; set; }
        = CameraFollowAnchor.Head;

    public bool FollowOpenClearView { get; set; }

    public double FollowOffsetX { get; set; }

    public double FollowOffsetY { get; set; }

    public double FollowOffsetZ { get; set; }

    public double FollowDistance { get; set; } = CameraFollowMath.DefaultDistance;

    public double FollowHeight { get; set; } = CameraFollowMath.DefaultHeight;

    public double FollowOrbitAngleDegrees { get; set; }

    public double FollowOrbitSpeedDegreesPerSecond { get; set; }

    public double FollowSmoothTimeSeconds { get; set; } = CameraFollowMath.DefaultSmoothTimeSeconds;

    public double FollowDelaySeconds { get; set; } = CameraFollowMath.DefaultDelaySeconds;

    public float FollowFov { get; set; } = CameraFollowMath.DefaultFov;

    /// <summary>
    /// World-space position offset from the selected entity part.
    /// </summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double OffsetZ { get; set; }

    /// <summary>
    /// Camera-local offset rotated by the tracked yaw and pitch.
    /// </summary>
    public double ViewOffsetX { get; set; }

    public double ViewOffsetY { get; set; }

    /// <summary>
    /// Local view-space distance behind the tracked target. Positive Z is
    /// opposite the Vintage Story camera forward vector, matching Flashback's
    /// default third-person tracking offset.
    /// </summary>
    public double ViewOffsetZ { get; set; } = 3d;

    public float FrequencyX { get; set; } = 1f;

    public float AmplitudeX { get; set; }

    public float FrequencyY { get; set; } = 1f;

    public float AmplitudeY { get; set; }

    public bool SplitParameters { get; set; }

    public float TickRate { get; set; } = (float)DirectorReplayElementTimeline.TicksPerSecond;

    public int TimelapseTick { get; set; }

    public DirectorReplayTimelapseKeyframeRole TimelapseRole { get; set; }

    public Guid TimelapsePairId { get; set; }

    /// <summary>
    /// Optional per-keyframe copy used by drafts created before duration was
    /// moved to the track. New editors write the track value and both copies.
    /// </summary>
    public double TimelapseDurationSeconds { get; set; }

    public int TimeOfDayTick { get; set; } = 6000;

    // Kept only for loading schema-18 drafts created by the previous build.
    public double? TimeOfDayHours { get; set; }

    public bool Frozen { get; set; }

    public int FreezeDelayTicks { get; set; }

    public int BlockX { get; set; }

    public int BlockY { get; set; }

    public int BlockZ { get; set; }

    public string BlockCode { get; set; } = string.Empty;

    public string AudioAsset { get; set; } = string.Empty;

    public float Volume { get; set; } = 1f;

    public float AudioPitch { get; set; } = 1f;

    public DirectorReplayElementKeyframe DeepClone() => (DirectorReplayElementKeyframe)MemberwiseClone();
}

/// <summary>
/// A replay-local camera keyframe. New keyframes store their pose directly;
/// SourceId remains for loading timelines created by older versions.
/// </summary>
public sealed class DirectorReplayViewportClip
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public DirectorReplayViewportSourceType SourceType { get; set; }

    public Guid SourceId { get; set; }

    public Guid TrackId { get; set; }

    public CameraPreset? CameraPose { get; set; }

    public CameraPathInterpolation? CameraInterpolationToNext { get; set; }

    public double TimelineStartSeconds { get; set; }

    public double DurationSeconds { get; set; } = 5d;

    public double SourceStartSeconds { get; set; }

    public double PlaybackRate { get; set; } = 1d;

    /// <summary>
    /// Stable vertical order in the replay viewport editor. This is separate
    /// from timeline time so overlapping sources can be arranged deliberately.
    /// </summary>
    public int TrackOrder { get; set; }

    public bool Enabled { get; set; } = true;

    public bool Loop { get; set; }

    public DirectorVideoTransition? TransitionIn { get; set; }

    public double TimelineEndSeconds
        => TimelineStartSeconds + DurationSeconds;

    public DirectorReplayViewportClip DeepClone()
    {
        DirectorReplayViewportClip clone = (DirectorReplayViewportClip)MemberwiseClone();
        clone.CameraPose = CameraPose?.DeepClone();
        clone.TransitionIn = TransitionIn?.DeepClone();
        return clone;
    }
}

/// <summary>
/// A named, independently editable collection of replay elements. The active
/// scene is mirrored onto DirectorReplayViewportTimeline's legacy top-level
/// collections so existing playback and export samplers keep using one view.
/// </summary>
public sealed class DirectorReplayElementScene
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public List<DirectorReplayViewportClip> Clips { get; set; } = new();

    public List<DirectorReplayElementTrack> Tracks { get; set; } = new();

    public List<DirectorReplayElementKeyframe> ElementKeyframes { get; set; } = new();

    public DirectorReplayVisualSettings Visuals { get; set; } = new();

    public Guid SelectedClipId { get; set; }

    public double? ExportStartSeconds { get; set; }

    public double? ExportEndSeconds { get; set; }

    public DirectorReplayElementScene DeepClone()
        => new()
        {
            Id = Id,
            Name = Name,
            Clips = (Clips ?? new List<DirectorReplayViewportClip>())
                .Where(clip => clip is not null)
                .Select(clip => clip.DeepClone())
                .ToList(),
            Tracks = (Tracks ?? new List<DirectorReplayElementTrack>())
                .Where(track => track is not null)
                .Select(track => track.DeepClone())
                .ToList(),
            ElementKeyframes = (ElementKeyframes ?? new List<DirectorReplayElementKeyframe>())
                .Where(keyframe => keyframe is not null)
                .Select(keyframe => keyframe.DeepClone())
                .ToList(),
            Visuals = DirectorReplayVisualSettings.Normalize(Visuals),
            SelectedClipId = SelectedClipId,
            ExportStartSeconds = ExportStartSeconds,
            ExportEndSeconds = ExportEndSeconds
        };

    internal static DirectorReplayElementScene FromTimeline(
        DirectorReplayViewportTimeline timeline,
        Guid id,
        string name)
        => new()
        {
            Id = id,
            Name = name,
            Clips = timeline.Clips.Select(clip => clip.DeepClone()).ToList(),
            Tracks = timeline.Tracks.Select(track => track.DeepClone()).ToList(),
            ElementKeyframes = timeline.ElementKeyframes
                .Select(keyframe => keyframe.DeepClone())
                .ToList(),
            Visuals = DirectorReplayVisualSettings.Normalize(timeline.Visuals),
            SelectedClipId = timeline.SelectedClipId,
            ExportStartSeconds = timeline.ExportStartSeconds,
            ExportEndSeconds = timeline.ExportEndSeconds
        };

    internal void CopyTo(DirectorReplayViewportTimeline timeline)
    {
        timeline.Clips = Clips.Select(clip => clip.DeepClone()).ToList();
        timeline.Tracks = Tracks.Select(track => track.DeepClone()).ToList();
        timeline.ElementKeyframes = ElementKeyframes
            .Select(keyframe => keyframe.DeepClone())
            .ToList();
        timeline.Visuals = DirectorReplayVisualSettings.Normalize(Visuals);
        timeline.SelectedClipId = SelectedClipId;
        timeline.ExportStartSeconds = ExportStartSeconds;
        timeline.ExportEndSeconds = ExportEndSeconds;
        timeline.ElementTracks = timeline.Tracks
            .Select(track => track.TrackType)
            .Distinct()
            .ToList();
        timeline.DisabledElementTracks = timeline.Tracks
            .Where(track => !track.Enabled)
            .Select(track => track.TrackType)
            .Distinct()
            .ToList();
    }
}

public sealed class DirectorReplayViewportTimeline
{
    public List<DirectorReplayViewportClip> Clips { get; set; } = new();

    public List<DirectorReplayElementTrackType> ElementTracks { get; set; } = new();

    public List<DirectorReplayElementTrackType> DisabledElementTracks { get; set; } = new();

    public List<DirectorReplayElementTrack> Tracks { get; set; } = new();

    public List<DirectorReplayElementKeyframe> ElementKeyframes { get; set; } = new();

    public DirectorReplayVisualSettings Visuals { get; set; } = new();

    public List<DirectorReplayElementScene> Scenes { get; set; } = new();

    public Guid ActiveSceneId { get; set; }

    public Guid SelectedClipId { get; set; }

    public double? ExportStartSeconds { get; set; }

    public double? ExportEndSeconds { get; set; }

    public double DurationSeconds { get; set; }

    public DirectorReplayViewportTimeline DeepClone()
    {
        return new DirectorReplayViewportTimeline
        {
            Clips = (Clips ?? new List<DirectorReplayViewportClip>())
                .Where(clip => clip is not null)
                .Select(clip => clip.DeepClone())
                .ToList(),
            ElementTracks = (ElementTracks ?? new List<DirectorReplayElementTrackType>())
                .Where(Enum.IsDefined)
                .Distinct()
                .ToList(),
            DisabledElementTracks = (DisabledElementTracks
                    ?? new List<DirectorReplayElementTrackType>())
                .Where(Enum.IsDefined)
                .Distinct()
                .ToList(),
            Tracks = (Tracks ?? new List<DirectorReplayElementTrack>())
                .Where(track => track is not null)
                .Select(track => track.DeepClone())
                .ToList(),
            ElementKeyframes = (ElementKeyframes ?? new List<DirectorReplayElementKeyframe>())
                .Where(keyframe => keyframe is not null)
                .Select(keyframe => keyframe.DeepClone())
                .ToList(),
            Visuals = DirectorReplayVisualSettings.Normalize(Visuals),
            Scenes = (Scenes ?? new List<DirectorReplayElementScene>())
                .Where(scene => scene is not null)
                .Select(scene => scene.DeepClone())
                .ToList(),
            ActiveSceneId = ActiveSceneId,
            SelectedClipId = SelectedClipId,
            ExportStartSeconds = ExportStartSeconds,
            ExportEndSeconds = ExportEndSeconds,
            DurationSeconds = DurationSeconds
        };
    }

    public static DirectorReplayViewportTimeline Normalize(
        DirectorReplayViewportTimeline? source,
        double replayDurationSeconds)
    {
        double duration = double.IsFinite(replayDurationSeconds)
            ? Math.Max(0d, replayDurationSeconds)
            : 0d;
        List<DirectorReplayElementScene> sourceScenes = (source?.Scenes
                ?? new List<DirectorReplayElementScene>())
            .Where(scene => scene is not null)
            .ToList();
        if (sourceScenes.Count == 0)
        {
            DirectorReplayViewportTimeline legacy = NormalizeContent(source, duration);
            Guid sceneId = Guid.NewGuid();
            legacy.ActiveSceneId = sceneId;
            legacy.Scenes.Add(DirectorReplayElementScene.FromTimeline(
                legacy,
                sceneId,
                "Scene 1"));
            return legacy;
        }

        Guid requestedActiveId = sourceScenes.Any(scene => scene.Id == source?.ActiveSceneId)
            ? source!.ActiveSceneId
            : sourceScenes[0].Id;
        HashSet<Guid> usedSceneIds = new();
        HashSet<string> usedSceneNames = new(StringComparer.OrdinalIgnoreCase);
        List<DirectorReplayElementScene> normalizedScenes = new();
        foreach ((DirectorReplayElementScene sourceScene, int index) in sourceScenes
            .Select((scene, index) => (scene, index)))
        {
            Guid sceneId = sourceScene.Id;
            if (sceneId == Guid.Empty || !usedSceneIds.Add(sceneId))
            {
                do
                {
                    sceneId = Guid.NewGuid();
                }
                while (!usedSceneIds.Add(sceneId));
            }

            bool active = sourceScene.Id == requestedActiveId;
            DirectorReplayViewportTimeline sceneSource = active
                ? new DirectorReplayViewportTimeline
                {
                    Clips = source?.Clips ?? new List<DirectorReplayViewportClip>(),
                    ElementTracks = source?.ElementTracks
                        ?? new List<DirectorReplayElementTrackType>(),
                    DisabledElementTracks = source?.DisabledElementTracks
                        ?? new List<DirectorReplayElementTrackType>(),
                    Tracks = source?.Tracks ?? new List<DirectorReplayElementTrack>(),
                    ElementKeyframes = source?.ElementKeyframes
                        ?? new List<DirectorReplayElementKeyframe>(),
                    Visuals = source?.Visuals ?? sourceScene.Visuals,
                    SelectedClipId = source?.SelectedClipId ?? Guid.Empty,
                    ExportStartSeconds = source?.ExportStartSeconds,
                    ExportEndSeconds = source?.ExportEndSeconds
                }
                : new DirectorReplayViewportTimeline
                {
                    Clips = sourceScene.Clips,
                    Tracks = sourceScene.Tracks,
                    ElementKeyframes = sourceScene.ElementKeyframes,
                    Visuals = sourceScene.Visuals,
                    SelectedClipId = sourceScene.SelectedClipId,
                    ExportStartSeconds = sourceScene.ExportStartSeconds,
                    ExportEndSeconds = sourceScene.ExportEndSeconds
                };
            DirectorReplayViewportTimeline normalizedContent = NormalizeContent(
                sceneSource,
                duration);
            string baseName = string.IsNullOrWhiteSpace(sourceScene.Name)
                ? $"Scene {index + 1}"
                : sourceScene.Name.Trim();
            string name = UniqueSceneName(baseName, usedSceneNames);
            usedSceneNames.Add(name);
            normalizedScenes.Add(DirectorReplayElementScene.FromTimeline(
                normalizedContent,
                sceneId,
                name));
            if (active)
            {
                requestedActiveId = sceneId;
            }
        }

        DirectorReplayElementScene activeScene = normalizedScenes.FirstOrDefault(scene =>
                scene.Id == requestedActiveId)
            ?? normalizedScenes[0];
        DirectorReplayViewportTimeline result = new()
        {
            Scenes = normalizedScenes,
            ActiveSceneId = activeScene.Id,
            DurationSeconds = duration
        };
        activeScene.CopyTo(result);
        return result;
    }

    private static DirectorReplayViewportTimeline NormalizeContent(
        DirectorReplayViewportTimeline? source,
        double replayDurationSeconds)
    {
        double duration = double.IsFinite(replayDurationSeconds)
            ? Math.Max(0d, replayDurationSeconds)
            : 0d;
        DirectorReplayViewportTimeline result = new()
        {
            SelectedClipId = source?.SelectedClipId ?? Guid.Empty,
            DurationSeconds = duration,
            Visuals = DirectorReplayVisualSettings.Normalize(source?.Visuals),
            ElementTracks = (source?.ElementTracks ?? new List<DirectorReplayElementTrackType>())
                .Where(type => Enum.IsDefined(type)
                    && type != DirectorReplayElementTrackType.Audio)
                .Distinct()
                .ToList(),
            DisabledElementTracks = (source?.DisabledElementTracks
                    ?? new List<DirectorReplayElementTrackType>())
                .Where(type => Enum.IsDefined(type)
                    && type != DirectorReplayElementTrackType.Audio)
                .Distinct()
                .ToList()
        };
        if (source?.ExportStartSeconds is double exportStart
            && source.ExportEndSeconds is double exportEnd
            && double.IsFinite(exportStart)
            && double.IsFinite(exportEnd)
            && duration > 0d)
        {
            exportStart = Math.Clamp(exportStart, 0d, duration);
            exportEnd = Math.Clamp(exportEnd, 0d, duration);
            if (exportEnd > exportStart)
            {
                result.ExportStartSeconds = exportStart;
                result.ExportEndSeconds = exportEnd;
            }
        }
        HashSet<Guid> usedIds = new();
        foreach (DirectorReplayElementTrack? item in source?.Tracks
            ?? new List<DirectorReplayElementTrack>())
        {
            if (item is null
                || !Enum.IsDefined(item.TrackType)
                || item.TrackType == DirectorReplayElementTrackType.Audio)
            {
                continue;
            }
            DirectorReplayElementTrack track = item.DeepClone();
            if (track.Id == Guid.Empty || !usedIds.Add(track.Id))
            {
                do
                {
                    track.Id = Guid.NewGuid();
                }
                while (!usedIds.Add(track.Id));
            }
            track.Name = track.Name?.Trim() ?? string.Empty;
            track.SpeakerId = track.SpeakerId?.Trim() ?? string.Empty;
            track.Color &= 0x00FFFFFF;
            track.TimelapseDurationSeconds = track.TrackType == DirectorReplayElementTrackType.Timelapse
                && double.IsFinite(track.TimelapseDurationSeconds)
                ? Math.Clamp(track.TimelapseDurationSeconds, 0d, 86400d)
                : 0d;
            result.Tracks.Add(track);
        }

        DirectorReplayElementTrack EnsureTrack(
            DirectorReplayElementTrackType type,
            bool? enabled = null)
        {
            DirectorReplayElementTrack? found = result.Tracks.FirstOrDefault(track =>
                track.TrackType == type);
            if (found is not null)
            {
                return found;
            }
            found = new DirectorReplayElementTrack
            {
                TrackType = type,
                Enabled = enabled ?? true
            };
            usedIds.Add(found.Id);
            result.Tracks.Add(found);
            return found;
        }

        foreach (DirectorReplayElementTrackType legacyType in result.ElementTracks)
        {
            if (legacyType is DirectorReplayElementTrackType.Marker
                or DirectorReplayElementTrackType.Audio
                or DirectorReplayElementTrackType.VoiceAudio)
            {
                continue;
            }
            EnsureTrack(legacyType, !result.DisabledElementTracks.Contains(legacyType));
        }

        foreach (DirectorReplayViewportClip? item in source?.Clips ?? new List<DirectorReplayViewportClip>())
        {
            if (item is null || !Enum.IsDefined(item.SourceType))
            {
                continue;
            }

            DirectorReplayViewportClip clip = item.DeepClone();
            if (clip.Id == Guid.Empty || !usedIds.Add(clip.Id))
            {
                do
                {
                    clip.Id = Guid.NewGuid();
                }
                while (!usedIds.Add(clip.Id));
            }
            clip.Name = string.IsNullOrWhiteSpace(clip.Name) ? DefaultName(clip.SourceType) : clip.Name.Trim();
            clip.TimelineStartSeconds = NormalizeTime(clip.TimelineStartSeconds, duration);
            clip.DurationSeconds = Math.Clamp(
                double.IsFinite(clip.DurationSeconds) ? clip.DurationSeconds : 5d,
                0.05d,
                Math.Max(0.05d, duration - clip.TimelineStartSeconds));
            clip.SourceStartSeconds = Math.Max(
                0d,
                double.IsFinite(clip.SourceStartSeconds) ? clip.SourceStartSeconds : 0d);
            clip.PlaybackRate = Math.Clamp(
                double.IsFinite(clip.PlaybackRate) ? clip.PlaybackRate : 1d,
                0.05d,
                32d);
            clip.TrackOrder = Math.Max(0, clip.TrackOrder);
            if (clip.SourceType == DirectorReplayViewportSourceType.FreeCamera)
            {
                clip.SourceId = Guid.Empty;
            }
            if (clip.SourceType == DirectorReplayViewportSourceType.StaticCamera)
            {
                DirectorReplayElementTrack? cameraTrack = result.Tracks.FirstOrDefault(track =>
                    track.Id == clip.TrackId
                    && track.TrackType == DirectorReplayElementTrackType.Camera);
                clip.TrackId = (cameraTrack
                    ?? EnsureTrack(DirectorReplayElementTrackType.Camera, clip.Enabled)).Id;
            }
            if (clip.CameraPose is not null)
            {
                clip.CameraPose = clip.CameraPose.DeepClone();
                clip.CameraPose.Id = Guid.Empty;
                clip.CameraPose.Name = clip.Name;
                clip.CameraPose.Fov = float.IsFinite(clip.CameraPose.Fov)
                    ? Math.Clamp(clip.CameraPose.Fov, 10f, 170f)
                    : 70f;
            }
            if (clip.CameraInterpolationToNext is CameraPathInterpolation interpolation
                && !Enum.IsDefined(interpolation))
            {
                clip.CameraInterpolationToNext = null;
            }
            if (clip.TransitionIn is not null)
            {
                clip.TransitionIn.DurationSeconds = Math.Clamp(
                    double.IsFinite(clip.TransitionIn.DurationSeconds)
                        ? clip.TransitionIn.DurationSeconds
                        : 0d,
                    0d,
                    clip.DurationSeconds);
            }
            result.Clips.Add(clip);
        }

        // Existing sidecars did not store TrackOrder. Keep their historical
        // chronological layout once, then persist a normalized track order.
        bool hasStoredTrackOrder = result.Clips.Any(clip => clip.TrackOrder != 0);
        result.Clips = (hasStoredTrackOrder
                ? result.Clips.OrderBy(clip => clip.TrackOrder)
                : result.Clips.OrderBy(clip => clip.TimelineStartSeconds))
            .ThenBy(clip => clip.Id)
            .ToList();
        for (int trackIndex = 0; trackIndex < result.Clips.Count; trackIndex++)
        {
            result.Clips[trackIndex].TrackOrder = trackIndex;
        }
        if (!result.Clips.Any(clip => clip.Id == result.SelectedClipId))
        {
            result.SelectedClipId = result.Clips.FirstOrDefault()?.Id ?? Guid.Empty;
        }

        HashSet<Guid> usedKeyframeIds = new();
        foreach (DirectorReplayElementKeyframe? item in source?.ElementKeyframes
            ?? new List<DirectorReplayElementKeyframe>())
        {
            if (item is null
                || !Enum.IsDefined(item.TrackType)
                || item.TrackType is DirectorReplayElementTrackType.Camera
                    or DirectorReplayElementTrackType.Marker
                    or DirectorReplayElementTrackType.Audio)
            {
                continue;
            }

            DirectorReplayElementKeyframe keyframe = NormalizeElementKeyframe(item, duration);
            DirectorReplayElementTrack? keyframeTrack = result.Tracks.FirstOrDefault(track =>
                track.Id == keyframe.TrackId
                && track.TrackType == keyframe.TrackType);
            keyframe.TrackId = (keyframeTrack ?? EnsureTrack(keyframe.TrackType)).Id;
            if (keyframe.Id == Guid.Empty || !usedKeyframeIds.Add(keyframe.Id))
            {
                do
                {
                    keyframe.Id = Guid.NewGuid();
                }
                while (!usedKeyframeIds.Add(keyframe.Id));
            }
            result.ElementKeyframes.Add(keyframe);
        }
        foreach (DirectorReplayElementTrack track in result.Tracks
            .Where(track => track.TrackType == DirectorReplayElementTrackType.Timelapse))
        {
            DirectorReplayTimelapse.NormalizePairs(result.ElementKeyframes, track.Id);
        }
        result.ElementKeyframes = result.ElementKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .ThenBy(keyframe => keyframe.Id)
            .ToList();
        result.ElementTracks = result.Tracks
            .Select(track => track.TrackType)
            .Distinct()
            .ToList();
        result.DisabledElementTracks = result.Tracks
            .Where(track => !track.Enabled)
            .Select(track => track.TrackType)
            .Distinct()
            .ToList();
        return result;
    }

    public DirectorReplayElementScene? ActiveScene
        => Scenes.FirstOrDefault(scene => scene.Id == ActiveSceneId);

    public void SyncActiveSceneFromTimeline()
    {
        DirectorReplayElementScene? active = ActiveScene;
        if (active is null)
        {
            return;
        }
        DirectorReplayElementScene updated = DirectorReplayElementScene.FromTimeline(
            this,
            active.Id,
            active.Name);
        int index = Scenes.FindIndex(scene => scene.Id == active.Id);
        Scenes[index] = updated;
    }

    public bool ActivateScene(Guid sceneId)
    {
        DirectorReplayElementScene? target = Scenes.FirstOrDefault(scene => scene.Id == sceneId);
        if (target is null)
        {
            return false;
        }
        SyncActiveSceneFromTimeline();
        ActiveSceneId = target.Id;
        target = Scenes.First(scene => scene.Id == sceneId);
        target.CopyTo(this);
        return true;
    }

    public DirectorReplayElementScene AddScene(string? name, bool activate = true)
    {
        SyncActiveSceneFromTimeline();
        HashSet<string> usedNames = Scenes
            .Select(scene => scene.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string sceneName = UniqueSceneName(
            string.IsNullOrWhiteSpace(name) ? $"Scene {Scenes.Count + 1}" : name.Trim(),
            usedNames);
        DirectorReplayElementScene scene = new() { Name = sceneName };
        Scenes.Add(scene);
        if (activate)
        {
            ActiveSceneId = scene.Id;
            scene.CopyTo(this);
        }
        return scene;
    }

    public bool RemoveScene(Guid sceneId)
    {
        if (Scenes.Count <= 1)
        {
            return false;
        }
        SyncActiveSceneFromTimeline();
        int index = Scenes.FindIndex(scene => scene.Id == sceneId);
        if (index < 0)
        {
            return false;
        }
        bool active = ActiveSceneId == sceneId;
        Scenes.RemoveAt(index);
        if (active)
        {
            DirectorReplayElementScene replacement = Scenes[Math.Min(index, Scenes.Count - 1)];
            ActiveSceneId = replacement.Id;
            replacement.CopyTo(this);
        }
        return true;
    }

    public static bool MoveTrack(
        DirectorReplayViewportTimeline timeline,
        Guid trackId,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        int sourceIndex = timeline.Tracks.FindIndex(track => track.Id == trackId);
        if (sourceIndex < 0 || timeline.Tracks.Count < 2)
        {
            return false;
        }
        targetIndex = Math.Clamp(targetIndex, 0, timeline.Tracks.Count - 1);
        if (targetIndex == sourceIndex)
        {
            return false;
        }
        DirectorReplayElementTrack track = timeline.Tracks[sourceIndex];
        timeline.Tracks.RemoveAt(sourceIndex);
        timeline.Tracks.Insert(targetIndex, track);
        timeline.SyncActiveSceneFromTimeline();
        return true;
    }

    private static string UniqueSceneName(string baseName, IReadOnlySet<string> usedNames)
    {
        string normalized = string.IsNullOrWhiteSpace(baseName) ? "Scene" : baseName.Trim();
        if (!usedNames.Contains(normalized))
        {
            return normalized;
        }
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{normalized} {suffix}";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static DirectorReplayElementKeyframe NormalizeElementKeyframe(
        DirectorReplayElementKeyframe source,
        double duration)
    {
        DirectorReplayElementKeyframe result = source.DeepClone();
        result.TimeSeconds = NormalizeTime(result.TimeSeconds, duration);
        result.InterpolationToNext = Enum.IsDefined(result.InterpolationToNext)
            ? result.InterpolationToNext
            : DirectorReplayElementInterpolation.Smooth;
        result.X = NormalizeFinite(result.X);
        result.Y = NormalizeFinite(result.Y);
        result.Z = NormalizeFinite(result.Z);
        result.Distance = Math.Clamp(NormalizeFinite(result.Distance, 8d), 0.01d, 4096d);
        result.Yaw = NormalizeFinite(result.Yaw);
        result.Pitch = NormalizeFinite(result.Pitch);
        result.Roll = NormalizeFinite(result.Roll);
        result.Fov = Math.Clamp(
            NormalizeFinite(result.Fov, 70f),
            DirectorReplayElementTimeline.MinFovDegrees,
            DirectorReplayElementTimeline.MaxFovDegrees);
        result.EntityActorKey = result.EntityActorKey?.Trim() ?? string.Empty;
        if (result.TrackType == DirectorReplayElementTrackType.CameraOrbit)
        {
            // CameraOrbit is a static center in Flashback. Older popup drafts
            // wrote the picked actor here even though playback never used it.
            result.EntityActorKey = string.Empty;
            result.EntityId = 0L;
            result.Roll = 0f;
        }
        string bodyPart = string.IsNullOrWhiteSpace(result.BodyPart)
            ? "head"
            : result.BodyPart.Trim().ToLowerInvariant();
        result.BodyPart = bodyPart is "head" or "body" or "root" ? bodyPart : "head";
        result.OpenClearView = result.OpenClearView && result.TrackType == DirectorReplayElementTrackType.TrackEntity;
        if (!Enum.IsDefined(result.FollowEntityRole))
        {
            result.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start;
        }
        if (!Enum.IsDefined(result.FollowViewMode))
        {
            result.FollowViewMode = CameraFollowViewMode.ThirdPerson;
        }
        if (!Enum.IsDefined(result.FollowMotionMode))
        {
            result.FollowMotionMode = CameraFollowMotionMode.Smooth;
        }
        if (!Enum.IsDefined(result.FollowAnchor))
        {
            result.FollowAnchor = CameraFollowAnchor.Head;
        }
        result.FollowOpenClearView = result.FollowOpenClearView
            && result.TrackType == DirectorReplayElementTrackType.FollowEntity;
        result.FollowOffsetX = Math.Clamp(NormalizeFinite(result.FollowOffsetX), -CameraFollowMath.MaxOffset, CameraFollowMath.MaxOffset);
        result.FollowOffsetY = Math.Clamp(NormalizeFinite(result.FollowOffsetY), -CameraFollowMath.MaxOffset, CameraFollowMath.MaxOffset);
        result.FollowOffsetZ = Math.Clamp(NormalizeFinite(result.FollowOffsetZ), -CameraFollowMath.MaxOffset, CameraFollowMath.MaxOffset);
        result.FollowDistance = CameraFollowMath.NormalizeDistance(result.FollowDistance);
        result.FollowHeight = CameraFollowMath.NormalizeHeight(result.FollowHeight);
        result.FollowOrbitAngleDegrees = CameraFollowMath.NormalizeDegrees(result.FollowOrbitAngleDegrees);
        result.FollowOrbitSpeedDegreesPerSecond = CameraFollowMath.NormalizeOrbitSpeed(result.FollowOrbitSpeedDegreesPerSecond);
        result.FollowSmoothTimeSeconds = CameraFollowMath.NormalizeSmoothTime(result.FollowSmoothTimeSeconds);
        result.FollowDelaySeconds = CameraFollowMath.NormalizeDelay(result.FollowDelaySeconds);
        result.FollowFov = CameraFollowMath.NormalizeFov(result.FollowFov);
        result.OffsetX = NormalizeFinite(result.OffsetX);
        result.OffsetY = NormalizeFinite(result.OffsetY);
        result.OffsetZ = NormalizeFinite(result.OffsetZ);
        result.ViewOffsetX = NormalizeFinite(result.ViewOffsetX);
        result.ViewOffsetY = NormalizeFinite(result.ViewOffsetY);
        result.ViewOffsetZ = NormalizeFinite(result.ViewOffsetZ, 3d);
        result.FrequencyX = Math.Clamp(NormalizeFinite(result.FrequencyX, 1f), 0.1f, 10f);
        result.FrequencyY = Math.Clamp(NormalizeFinite(result.FrequencyY, 1f), 0.1f, 10f);
        result.AmplitudeX = Math.Clamp(NormalizeFinite(result.AmplitudeX), 0f, 10f);
        result.AmplitudeY = Math.Clamp(NormalizeFinite(result.AmplitudeY), 0f, 10f);
        result.TickRate = Math.Clamp(
            NormalizeFinite(result.TickRate, (float)DirectorReplayElementTimeline.TicksPerSecond),
            1f,
            (float)(DirectorReplayElementTimeline.TicksPerSecond * 64d));
        result.TimelapseTick = Math.Max(0, result.TimelapseTick);
        result.TimelapseDurationSeconds = double.IsFinite(result.TimelapseDurationSeconds)
            ? Math.Clamp(result.TimelapseDurationSeconds, 0d, 86400d)
            : 0d;
        if (result.TimeOfDayHours is double legacyHours
            && double.IsFinite(legacyHours))
        {
            result.TimeOfDayTick = (int)Math.Round(
                Math.Clamp(legacyHours, 0d, 24d) / 24d * 24000d);
            result.TimeOfDayHours = null;
        }
        result.TimeOfDayTick = Math.Clamp(result.TimeOfDayTick, 0, 24000);
        result.FreezeDelayTicks = Math.Clamp(
            result.FreezeDelayTicks,
            0,
            DirectorReplayElementTimeline.MaxFreezeDelayTicks);
        result.BlockCode = result.BlockCode?.Trim() ?? string.Empty;
        result.AudioAsset = result.AudioAsset?.Trim() ?? string.Empty;
        result.Volume = result.TrackType == DirectorReplayElementTrackType.VoiceAudio
            ? Math.Clamp(NormalizeFinite(result.Volume, 1f), 0f, 2f)
            : Math.Clamp(NormalizeFinite(result.Volume, 1f), 0f, 4f);
        result.AudioPitch = Math.Clamp(NormalizeFinite(result.AudioPitch, 1f), 0.1f, 4f);
        return result;
    }

    private static double NormalizeFinite(double value, double fallback = 0d)
        => double.IsFinite(value) ? value : fallback;

    private static float NormalizeFinite(float value, float fallback = 0f)
        => float.IsFinite(value) ? value : fallback;

    private static double NormalizeTime(double value, double duration)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, duration) : 0d;

    public static DirectorReplayElementTrack? TrackFor(
        DirectorReplayViewportTimeline timeline,
        Guid trackId)
        => timeline.Tracks.FirstOrDefault(track => track.Id == trackId);

    public static bool RemoveTrack(
        DirectorReplayViewportTimeline timeline,
        Guid trackId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.Id == trackId);
        if (track is null)
        {
            return false;
        }
        if (track.TrackType == DirectorReplayElementTrackType.VoiceAudio)
        {
            return false;
        }

        timeline.Tracks.RemoveAll(item => item.Id == trackId);
        HashSet<Guid> removedClipIds = timeline.Clips
            .Where(clip => clip.TrackId == trackId)
            .Select(clip => clip.Id)
            .ToHashSet();
        timeline.Clips.RemoveAll(clip => clip.TrackId == trackId);
        timeline.ElementKeyframes.RemoveAll(keyframe => keyframe.TrackId == trackId);
        if (removedClipIds.Contains(timeline.SelectedClipId))
        {
            timeline.SelectedClipId = Guid.Empty;
        }

        // These lists are retained only for loading older sidecars. Keep them
        // synchronized so normalization cannot recreate a deliberately deleted row.
        timeline.ElementTracks = timeline.Tracks
            .Select(item => item.TrackType)
            .Distinct()
            .ToList();
        timeline.DisabledElementTracks = timeline.Tracks
            .Where(item => !item.Enabled)
            .Select(item => item.TrackType)
            .Distinct()
            .ToList();
        timeline.SyncActiveSceneFromTimeline();
        return true;
    }

    public static void MigrateLegacySpeedKeyframes(
        DirectorReplayViewportTimeline timeline,
        IReadOnlyCollection<DirectorReplaySpeedKeyframe> speedKeyframes)
    {
        if (speedKeyframes.Count == 0
            || timeline.ElementKeyframes.Any(keyframe =>
                keyframe.TrackType == DirectorReplayElementTrackType.Speed))
        {
            return;
        }

        DirectorReplayElementTrack? track = timeline.Tracks.FirstOrDefault(item =>
            item.TrackType == DirectorReplayElementTrackType.Speed);
        if (track is null)
        {
            track = new DirectorReplayElementTrack
            {
                TrackType = DirectorReplayElementTrackType.Speed,
                Enabled = !timeline.DisabledElementTracks.Contains(
                    DirectorReplayElementTrackType.Speed)
            };
            timeline.Tracks.Add(track);
        }
        foreach (DirectorReplaySpeedKeyframe legacy in speedKeyframes)
        {
            timeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
            {
                Id = legacy.Id,
                TrackId = track.Id,
                TrackType = DirectorReplayElementTrackType.Speed,
                TimeSeconds = legacy.TimeSeconds,
                TickRate = (float)Math.Clamp(
                    legacy.Speed * DirectorReplayElementTimeline.TicksPerSecond,
                    1d,
                    DirectorReplayElementTimeline.TicksPerSecond * 20d),
                InterpolationToNext = legacy.InterpolationToNext switch
                {
                    DirectorReplaySpeedInterpolation.Hold => DirectorReplayElementInterpolation.Hold,
                    DirectorReplaySpeedInterpolation.EaseInOut => DirectorReplayElementInterpolation.EaseInOut,
                    _ => DirectorReplayElementInterpolation.Linear
                }
            });
        }
        if (!timeline.ElementTracks.Contains(DirectorReplayElementTrackType.Speed))
        {
            timeline.ElementTracks.Add(DirectorReplayElementTrackType.Speed);
        }
        timeline.SyncActiveSceneFromTimeline();
    }

    public static void MigrateLegacyTwentyTickTimebase(
        DirectorReplayViewportTimeline? timeline)
    {
        if (timeline is null)
        {
            return;
        }

        const double legacyTicksPerSecond = 20d;
        double scale = DirectorReplayElementTimeline.TicksPerSecond / legacyTicksPerSecond;
        HashSet<DirectorReplayElementKeyframe> migrated = new(ReferenceEqualityComparer.Instance);

        void Migrate(IEnumerable<DirectorReplayElementKeyframe>? keyframes)
        {
            foreach (DirectorReplayElementKeyframe keyframe in keyframes
                ?? Array.Empty<DirectorReplayElementKeyframe>())
            {
                if (keyframe is null || !migrated.Add(keyframe))
                {
                    continue;
                }

                switch (keyframe.TrackType)
                {
                    case DirectorReplayElementTrackType.Speed:
                        keyframe.TickRate = (float)(keyframe.TickRate * scale);
                        break;
                    case DirectorReplayElementTrackType.Timelapse:
                        keyframe.TimelapseTick = ScaleLegacyTick(keyframe.TimelapseTick, scale);
                        break;
                    case DirectorReplayElementTrackType.Freeze:
                        keyframe.FreezeDelayTicks = ScaleLegacyTick(
                            keyframe.FreezeDelayTicks,
                            scale);
                        break;
                }
            }
        }

        Migrate(timeline.ElementKeyframes);
        foreach (DirectorReplayElementScene scene in timeline.Scenes
            ?? new List<DirectorReplayElementScene>())
        {
            Migrate(scene?.ElementKeyframes);
        }
    }

    private static int ScaleLegacyTick(int tick, double scale)
        => Math.Max(0, (int)Math.Round(
            Math.Max(0, tick) * scale,
            MidpointRounding.AwayFromZero));

    public static string DefaultName(DirectorReplayViewportSourceType sourceType)
        => sourceType switch
        {
            DirectorReplayViewportSourceType.FreeCamera => "Free camera",
            DirectorReplayViewportSourceType.StaticCamera => "Camera",
            DirectorReplayViewportSourceType.Path => "Path",
            DirectorReplayViewportSourceType.Sequence => "Sequence",
            DirectorReplayViewportSourceType.Follow => "Follow",
            _ => "Viewport"
        };
}
