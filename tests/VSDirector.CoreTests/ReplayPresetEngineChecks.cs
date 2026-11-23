using VSDirector.Data;

/// <summary>Engine pieces the replay preset library relies on.</summary>
internal static class ReplayPresetEngineChecks
{
    public static void Run()
    {
        EndsElementReleasesTrack();
        TimeEffectsMapContentTime();
        FollowBlendAnimatesRig();
        LookAtAimsAndFrames();
        UsagePersists();
        CameraPriorityReturnsToOriginalTrack();
        NewTrackTypesSurviveSaveAndLoad();
        OlderFilesKeepTheirPlayback();
        NewerFilesAreRejected();
    }

    private static void EndsElementReleasesTrack()
    {
        var frames = new List<DirectorReplayElementKeyframe>
        {
            new() { TimeSeconds = 1d, Fov = 40f, TrackType = DirectorReplayElementTrackType.Fov,
                InterpolationToNext = DirectorReplayElementInterpolation.Linear },
            new() { TimeSeconds = 3d, Fov = 80f, TrackType = DirectorReplayElementTrackType.Fov,
                InterpolationToNext = DirectorReplayElementInterpolation.Hold, EndsElement = true }
        };
        Check.True(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 2d, out var middle));
        Check.NearlyEqual(60d, middle.Fov, 1e-4);
        Check.False(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 3d, out _));
        Check.False(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 10d, out _));
        frames[1].EndsElement = false;
        Check.True(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 10d, out var held));
        Check.NearlyEqual(80d, held.Fov, 1e-4);

        // A clone keeps the flag (presets copy keyframes to build end markers).
        Check.True(new DirectorReplayElementKeyframe { EndsElement = true }.DeepClone().EndsElement);
    }

    private static DirectorReplayViewportTimeline FreezeTimeline(DirectorReplayTimeEffect effect, double seconds, double rate)
    {
        var timeline = new DirectorReplayViewportTimeline();
        var track = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.Freeze };
        timeline.Tracks.Add(track);
        timeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
        {
            TrackId = track.Id, TrackType = DirectorReplayElementTrackType.Freeze, TimeSeconds = 10d, Frozen = true,
            TimeEffect = effect, TimeEffectSeconds = seconds, TimeEffectRate = rate,
            InterpolationToNext = DirectorReplayElementInterpolation.Hold
        });
        timeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
        {
            TrackId = track.Id, TrackType = DirectorReplayElementTrackType.Freeze, TimeSeconds = 15d, Frozen = false,
            InterpolationToNext = DirectorReplayElementInterpolation.Hold
        });
        return timeline;
    }

    private static void TimeEffectsMapContentTime()
    {
        DirectorReplayViewportTimeline rewind = FreezeTimeline(DirectorReplayTimeEffect.Rewind, 3d, 1d);
        var start = DirectorReplayElementTimeline.SampleFreeze(rewind, 10d);
        Check.NearlyEqual(10d, start.ReplayTimeSeconds, 1e-6);
        var back = DirectorReplayElementTimeline.SampleFreeze(rewind, 11d);
        Check.NearlyEqual(7d, back.ReplayTimeSeconds, 1e-6);
        Check.NearlyEqual(11d, back.CameraTimeSeconds ?? double.NaN, 1e-6);
        var replaying = DirectorReplayElementTimeline.SampleFreeze(rewind, 12.5d);
        Check.NearlyEqual(8.5d, replaying.ReplayTimeSeconds, 1e-6);
        Check.True(DirectorReplayElementTimeline.SampleFreeze(rewind, 10.5d).RewindAmount > 0.5d);
        Check.NearlyEqual(16d, DirectorReplayElementTimeline.SampleFreeze(rewind, 16d).ReplayTimeSeconds, 1e-6);

        DirectorReplayViewportTimeline stutter = FreezeTimeline(DirectorReplayTimeEffect.Stutter, 3d, 4d);
        Check.NearlyEqual(10d, DirectorReplayElementTimeline.SampleFreeze(stutter, 10.2d).ReplayTimeSeconds, 1e-6);
        Check.NearlyEqual(10.25d, DirectorReplayElementTimeline.SampleFreeze(stutter, 10.3d).ReplayTimeSeconds, 1e-6);

        DirectorReplayViewportTimeline slow = FreezeTimeline(DirectorReplayTimeEffect.Slow, 3d, 0.25d);
        var slowed = DirectorReplayElementTimeline.SampleFreeze(slow, 12d);
        Check.NearlyEqual(10.5d, slowed.ReplayTimeSeconds, 1e-6);
        Check.NearlyEqual(12d, slowed.CameraTimeSeconds ?? double.NaN, 1e-6);
    }

    private static void FollowBlendAnimatesRig()
    {
        var start = new DirectorReplayElementKeyframe
        {
            TimeSeconds = 0d, FollowDistance = 3d, FollowHeight = 0.5d, Roll = 0f,
            FollowBlend = DirectorReplayElementInterpolation.Linear
        };
        var end = new DirectorReplayElementKeyframe { TimeSeconds = 4d, FollowDistance = 11d, FollowHeight = 8.5d, Roll = 20f };
        DirectorReplayElementKeyframe half = DirectorReplayFollowBlend.Sample(start, end, 2d);
        Check.NearlyEqual(7d, half.FollowDistance);
        Check.NearlyEqual(4.5d, half.FollowHeight);
        Check.NearlyEqual(10d, half.Roll, 1e-4);
        start.FollowBlend = DirectorReplayElementInterpolation.Hold;
        Check.NearlyEqual(3d, DirectorReplayFollowBlend.Sample(start, end, 2d).FollowDistance);
    }

    private static void LookAtAimsAndFrames()
    {
        var tripod = new DirectorReplayElementKeyframe
        {
            TrackMode = DirectorReplayTrackMode.Tripod, X = 0d, Y = 0d, Z = 10d,
            TrackAutoFrame = true, TrackFrameSize = 0.5d
        };
        var subject = new DirectorReplayCameraElementMath.LookSubject(0d, 0d, 0d, 1d);
        CameraPreset pose = DirectorReplayCameraElementMath.EvaluateLookAt(tripod, subject, null, 0, 70f);
        Check.NearlyEqual(0d, pose.Yaw, 1e-4);
        Check.NearlyEqual(0d, pose.Pitch, 1e-4);
        double expectedFov = 2d * Math.Atan(1d / (10d * 0.5d)) * 180d / Math.PI;
        Check.NearlyEqual(expectedFov, pose.Fov, 1e-3);

        tripod.TrackAutoFrame = false;
        tripod.Y = 10d;
        CameraPreset high = DirectorReplayCameraElementMath.EvaluateLookAt(tripod, subject, null, 0, 70f);
        Check.NearlyEqual(-45d, high.Pitch, 1e-3);
        Check.NearlyEqual(70d, high.Fov, 1e-4);
    }

    private static void UsagePersists()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vsdirector-usage-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "usage.json");
        try
        {
            DirectorReplayPresetUsage usage = DirectorReplayPresetUsage.Load(path);
            usage.RecordUse("a");
            usage.RecordUse("b");
            usage.RecordUse("a");
            usage.ToggleFavorite("b");
            usage.SetLastCategory("shake");
            DirectorReplayPresetUsage loaded = DirectorReplayPresetUsage.Load(path);
            Check.Equal("a", loaded.Recent[0]);
            Check.Equal(2, loaded.Recent.Count);
            Check.Equal(2, loaded.Count("a"));
            Check.Equal("a", loaded.MostUsed().First());
            Check.True(loaded.IsFavorite("b"));
            Check.Equal("shake", loaded.LastCategory);
            for (int i = 0; i < 40; i++) loaded.RecordUse("p" + i);
            Check.Equal(DirectorReplayPresetUsage.MaxRecent, DirectorReplayPresetUsage.Load(path).Recent.Count);
            File.WriteAllText(path, "{ not json");
            Check.Equal(0, DirectorReplayPresetUsage.Load(path).Recent.Count);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    // ----- Camera row priority ------------------------------------------------------

    private static DirectorReplayElementTrack AddTrack(DirectorReplayViewportTimeline timeline,
        DirectorReplayElementTrackType type)
    {
        var track = new DirectorReplayElementTrack { TrackType = type };
        timeline.Tracks.Add(track);
        return track;
    }

    private static void AddCameraClip(DirectorReplayViewportTimeline timeline, DirectorReplayElementTrack track,
        double at, bool endsShot = false)
        => timeline.Clips.Add(new DirectorReplayViewportClip
        {
            SourceType = DirectorReplayViewportSourceType.StaticCamera,
            CameraPose = new CameraPreset { X = at, Y = 100d, Z = 0d, Fov = 70f },
            TrackId = track.Id,
            TimelineStartSeconds = at,
            DurationSeconds = 1d,
            PlaybackRate = 1d,
            Enabled = true,
            EndsShot = endsShot
        });

    private static void AddElementKey(DirectorReplayViewportTimeline timeline, DirectorReplayElementTrack track,
        double at, Action<DirectorReplayElementKeyframe>? configure = null)
    {
        var keyframe = new DirectorReplayElementKeyframe
        {
            TrackId = track.Id, TrackType = track.TrackType, TimeSeconds = at, EntityActorKey = "player:a",
            InterpolationToNext = DirectorReplayElementInterpolation.Linear
        };
        configure?.Invoke(keyframe);
        timeline.ElementKeyframes.Add(keyframe);
    }

    /// <summary>
    /// A timeline that already has an active camera row gets a preset at 5 s. Presets add
    /// a new row at the bottom; it drives the view while it runs and the original row
    /// takes over again when it ends. This covers the three ways presets end a row.
    /// </summary>
    private static void CameraPriorityReturnsToOriginalTrack()
    {
        foreach (string kind in new[] { "follow", "track", "camera" })
        {
            var timeline = new DirectorReplayViewportTimeline();
            DirectorReplayElementTrack original = AddTrack(timeline, DirectorReplayElementTrackType.Camera);
            AddCameraClip(timeline, original, 0d);
            Check.Equal(original.Id, DirectorReplayCameraPriority.ResolveActive(timeline, 4d)?.Id);

            DirectorReplayElementTrack preset;
            switch (kind)
            {
                case "follow":
                    // Follow presets write a start/end interval.
                    preset = AddTrack(timeline, DirectorReplayElementTrackType.FollowEntity);
                    AddElementKey(timeline, preset, 5d, k => k.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start);
                    AddElementKey(timeline, preset, 13d, k => k.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End);
                    break;
                case "track":
                    // Tracking presets end with an "Element ends here" keyframe.
                    preset = AddTrack(timeline, DirectorReplayElementTrackType.TrackEntity);
                    AddElementKey(timeline, preset, 5d, k => k.TrackMode = DirectorReplayTrackMode.Tripod);
                    AddElementKey(timeline, preset, 13d, k => k.EndsElement = true);
                    break;
                default:
                    // Camera-move presets end with an "ends shot" camera keyframe.
                    preset = AddTrack(timeline, DirectorReplayElementTrackType.Camera);
                    AddCameraClip(timeline, preset, 5d);
                    AddCameraClip(timeline, preset, 9d);
                    AddCameraClip(timeline, preset, 13d, endsShot: true);
                    break;
            }

            Check.Equal(original.Id, DirectorReplayCameraPriority.ResolveActive(timeline, 4.9d)?.Id);
            Check.Equal(preset.Id, DirectorReplayCameraPriority.ResolveActive(timeline, 5d)?.Id);
            Check.Equal(preset.Id, DirectorReplayCameraPriority.ResolveActive(timeline, 12.9d)?.Id);
            Check.Equal(original.Id, DirectorReplayCameraPriority.ResolveActive(timeline, 13d)?.Id);
            Check.Equal(original.Id, DirectorReplayCameraPriority.ResolveActive(timeline, 60d)?.Id);

            // A disabled preset row never takes the view.
            preset.Enabled = false;
            Check.Equal(original.Id, DirectorReplayCameraPriority.ResolveActive(timeline, 8d)?.Id);
        }

        // Non-camera rows (FOV, shake, speed...) do not compete for the view.
        var mixed = new DirectorReplayViewportTimeline();
        DirectorReplayElementTrack camera = AddTrack(mixed, DirectorReplayElementTrackType.Camera);
        AddCameraClip(mixed, camera, 0d);
        DirectorReplayElementTrack fov = AddTrack(mixed, DirectorReplayElementTrackType.Fov);
        AddElementKey(mixed, fov, 0d, k => k.Fov = 40f);
        Check.Equal(camera.Id, DirectorReplayCameraPriority.ResolveActive(mixed, 2d)?.Id);
        Check.True(DirectorReplayCameraPriority.ResolveActive(new DirectorReplayViewportTimeline(), 2d) is null);
    }

    // ----- Saved data ---------------------------------------------------------------

    private static DirectorReplayDocument PresetReplay(out Guid gradeTrack, out Guid titleTrack, out Guid cameraTrack)
    {
        var timeline = new DirectorReplayViewportTimeline();
        DirectorReplayElementTrack camera = AddTrack(timeline, DirectorReplayElementTrackType.Camera);
        AddCameraClip(timeline, camera, 1d);
        AddCameraClip(timeline, camera, 6d, endsShot: true);
        DirectorReplayElementTrack grade = AddTrack(timeline, DirectorReplayElementTrackType.ColorGrade);
        AddElementKey(timeline, grade, 2d, k => { k.GradeSepia = 0.5f; k.GradeContrast = -0.25f; k.GradeVignette = 0.4f;
            k.GradeBloom = 1.5f; k.GradeGlitch = 0.3f; k.GradeFade = 0.75f; });
        AddElementKey(timeline, grade, 8d, k => k.EndsElement = true);
        DirectorReplayElementTrack title = AddTrack(timeline, DirectorReplayElementTrackType.Title);
        AddElementKey(timeline, title, 3d, k => { k.Text = "Chapter one"; k.SubText = "The long road";
            k.TitleDurationSeconds = 4.5d; k.TitlePosition = DirectorReplayTitlePosition.Top;
            k.InterpolationToNext = DirectorReplayElementInterpolation.Hold; });
        DirectorReplayElementTrack freeze = AddTrack(timeline, DirectorReplayElementTrackType.Freeze);
        AddElementKey(timeline, freeze, 4d, k => { k.Frozen = true; k.TimeEffect = DirectorReplayTimeEffect.Rewind;
            k.TimeEffectSeconds = 2.5d; k.TimeEffectRate = 0.75d; k.FreezeCameraContinues = true;
            k.InterpolationToNext = DirectorReplayElementInterpolation.Hold; });
        DirectorReplayElementTrack track = AddTrack(timeline, DirectorReplayElementTrackType.TrackEntity);
        AddElementKey(timeline, track, 5d, k => { k.TrackMode = DirectorReplayTrackMode.OverShoulder;
            k.SecondaryEntityActorKey = "player:b"; k.SecondaryEntityId = 42L; k.TargetBlend = 0.25d;
            k.TrackFrameGroup = true; k.TrackAutoFrame = true; k.TrackFrameSize = 0.6d; k.TrackSmoothTimeSeconds = 0.5d; });
        DirectorReplayElementTrack follow = AddTrack(timeline, DirectorReplayElementTrackType.FollowEntity);
        AddElementKey(timeline, follow, 6d, k => { k.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start;
            k.FollowBlend = DirectorReplayElementInterpolation.EaseInOut; k.FollowWorldAligned = true; k.Roll = 12f; });
        AddElementKey(timeline, follow, 9d, k => k.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End);
        DirectorReplayElementTrack shake = AddTrack(timeline, DirectorReplayElementTrackType.CameraShake);
        AddElementKey(timeline, shake, 1d, k => k.Roll = 6f);
        timeline.Visuals.Camera.AvoidOcclusion = true;
        timeline.Visuals.Composition.VerticalGuide = true;
        (gradeTrack, titleTrack, cameraTrack) = (grade.Id, title.Id, camera.Id);
        return DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Name = "Preset save",
            DurationSeconds = 20d,
            ViewportTimeline = timeline
        });
    }

    private static void CheckPresetTimeline(DirectorReplayViewportTimeline timeline, Guid gradeTrack, Guid titleTrack, Guid cameraTrack)
    {
        DirectorReplayElementKeyframe Key(DirectorReplayElementTrackType type, double at)
            => timeline.ElementKeyframes.Single(k => k.TrackType == type && Math.Abs(k.TimeSeconds - at) < 1e-6);

        Check.True(timeline.Tracks.Any(t => t.Id == gradeTrack && t.TrackType == DirectorReplayElementTrackType.ColorGrade));
        Check.True(timeline.Tracks.Any(t => t.Id == titleTrack && t.TrackType == DirectorReplayElementTrackType.Title));
        DirectorReplayElementKeyframe grade = Key(DirectorReplayElementTrackType.ColorGrade, 2d);
        Check.Equal(gradeTrack, grade.TrackId);
        Check.NearlyEqual(0.5d, grade.GradeSepia, 1e-6);
        Check.NearlyEqual(-0.25d, grade.GradeContrast, 1e-6);
        Check.NearlyEqual(0.4d, grade.GradeVignette, 1e-6);
        Check.NearlyEqual(1.5d, grade.GradeBloom, 1e-6);
        Check.NearlyEqual(0.3d, grade.GradeGlitch, 1e-6);
        Check.NearlyEqual(0.75d, grade.GradeFade, 1e-6);
        Check.True(Key(DirectorReplayElementTrackType.ColorGrade, 8d).EndsElement);

        DirectorReplayElementKeyframe title = Key(DirectorReplayElementTrackType.Title, 3d);
        Check.Equal(titleTrack, title.TrackId);
        Check.Equal("Chapter one", title.Text);
        Check.Equal("The long road", title.SubText);
        Check.NearlyEqual(4.5d, title.TitleDurationSeconds, 1e-6);
        Check.Equal(DirectorReplayTitlePosition.Top, title.TitlePosition);

        DirectorReplayElementKeyframe rewind = Key(DirectorReplayElementTrackType.Freeze, 4d);
        Check.Equal(DirectorReplayTimeEffect.Rewind, rewind.TimeEffect);
        Check.NearlyEqual(2.5d, rewind.TimeEffectSeconds, 1e-6);
        Check.NearlyEqual(0.75d, rewind.TimeEffectRate, 1e-6);
        Check.True(rewind.FreezeCameraContinues);

        DirectorReplayElementKeyframe shoulder = Key(DirectorReplayElementTrackType.TrackEntity, 5d);
        Check.Equal(DirectorReplayTrackMode.OverShoulder, shoulder.TrackMode);
        Check.Equal("player:b", shoulder.SecondaryEntityActorKey);
        Check.Equal(42L, shoulder.SecondaryEntityId);
        Check.NearlyEqual(0.25d, shoulder.TargetBlend, 1e-6);
        Check.True(shoulder.TrackFrameGroup);
        Check.True(shoulder.TrackAutoFrame);
        Check.NearlyEqual(0.6d, shoulder.TrackFrameSize, 1e-6);
        Check.NearlyEqual(0.5d, shoulder.TrackSmoothTimeSeconds, 1e-6);

        DirectorReplayElementKeyframe follow = Key(DirectorReplayElementTrackType.FollowEntity, 6d);
        Check.Equal(DirectorReplayElementInterpolation.EaseInOut, follow.FollowBlend);
        Check.True(follow.FollowWorldAligned);
        Check.NearlyEqual(12d, follow.Roll, 1e-6);
        Check.NearlyEqual(6d, Key(DirectorReplayElementTrackType.CameraShake, 1d).Roll, 1e-6);

        Check.True(timeline.Clips.Single(c => c.TrackId == cameraTrack && Math.Abs(c.TimelineStartSeconds - 6d) < 1e-6).EndsShot);
        Check.False(timeline.Clips.Single(c => c.TrackId == cameraTrack && Math.Abs(c.TimelineStartSeconds - 1d) < 1e-6).EndsShot);
        Check.True(timeline.Visuals.Camera.AvoidOcclusion);
        Check.True(timeline.Visuals.Composition.VerticalGuide);
    }

    /// <summary>Save, reload and save again: every new track type and field survives each format.</summary>
    private static void NewTrackTypesSurviveSaveAndLoad()
    {
        DirectorReplayDocument replay = PresetReplay(out Guid grade, out Guid title, out Guid camera);
        CheckPresetTimeline(replay.ViewportTimeline, grade, title, camera);
        Check.Equal(23, replay.SchemaVersion);

        string root = Path.Combine(Path.GetTempPath(), "vsdirector-preset-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Replay document.
            string replayPath = Path.Combine(root, "take.vsdreplay.gz");
            DirectorReplayIO.Save(replayPath, replay);
            DirectorReplayDocument loaded = DirectorReplayIO.Load(replayPath);
            Check.Equal(DirectorReplayDocument.CurrentSchemaVersion, loaded.SchemaVersion);
            CheckPresetTimeline(loaded.ViewportTimeline, grade, title, camera);
            string resavedPath = Path.Combine(root, "resaved.vsdreplay.gz");
            DirectorReplayIO.Save(resavedPath, loaded);
            CheckPresetTimeline(DirectorReplayIO.Load(resavedPath).ViewportTimeline, grade, title, camera);

            // Paged playback cache: built on first open, read back on the second.
            CheckPresetTimeline(DirectorReplayIO.LoadPagedAsync(replayPath).GetAwaiter().GetResult().ViewportTimeline,
                grade, title, camera);
            DirectorReplayDocument cached = DirectorReplayIO.LoadPagedAsync(replayPath).GetAwaiter().GetResult();
            Check.True(cached.Pages is not null);
            CheckPresetTimeline(cached.ViewportTimeline, grade, title, camera);

            // Standalone timeline edit file.
            string sidecarPath = replayPath + ".timeline.json";
            DirectorReplayTimelineEditIO.Save(sidecarPath, DirectorReplayTimelineEditIO.Create(replay));
            Check.True(File.ReadAllText(sidecarPath).Contains("\"schemaVersion\": 8", StringComparison.Ordinal));
            DirectorReplayTimelineEditDocument edits = Check.NotNull(DirectorReplayTimelineEditIO.Load(sidecarPath));
            DirectorReplayDocument target = DirectorReplayMath.Normalize(new DirectorReplayDocument
            {
                Id = replay.Id, Name = "Target", DurationSeconds = 20d
            });
            DirectorReplayTimelineEditIO.Apply(target, edits);
            CheckPresetTimeline(target.ViewportTimeline, grade, title, camera);
            DirectorReplayTimelineEditIO.Save(sidecarPath, DirectorReplayTimelineEditIO.Create(target));
            DirectorReplayDocument again = DirectorReplayMath.Normalize(new DirectorReplayDocument
            {
                Id = replay.Id, Name = "Again", DurationSeconds = 20d
            });
            DirectorReplayTimelineEditIO.Apply(again, Check.NotNull(DirectorReplayTimelineEditIO.Load(sidecarPath)));
            CheckPresetTimeline(again.ViewportTimeline, grade, title, camera);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DirectorReplayViewportTimeline LegacyTimeline()
    {
        // What 1.0.4-pre.5 writes: follow and shake keyframes carry the capture camera's
        // roll, which that version ignored; orbit roll was already used.
        var timeline = new DirectorReplayViewportTimeline();
        DirectorReplayElementTrack follow = AddTrack(timeline, DirectorReplayElementTrackType.FollowEntity);
        AddElementKey(timeline, follow, 1d, k => { k.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.Start; k.Roll = 15f; });
        AddElementKey(timeline, follow, 4d, k => { k.FollowEntityRole = DirectorReplayFollowEntityKeyframeRole.End; k.Roll = 15f; });
        DirectorReplayElementTrack shake = AddTrack(timeline, DirectorReplayElementTrackType.CameraShake);
        AddElementKey(timeline, shake, 1d, k => k.Roll = 10f);
        DirectorReplayElementTrack orbit = AddTrack(timeline, DirectorReplayElementTrackType.CameraOrbit);
        AddElementKey(timeline, orbit, 1d, k => k.Roll = 5f);
        DirectorReplayElementTrack track = AddTrack(timeline, DirectorReplayElementTrackType.TrackEntity);
        AddElementKey(timeline, track, 2d, k => k.Roll = 7f);
        DirectorReplayElementTrack freeze = AddTrack(timeline, DirectorReplayElementTrackType.Freeze);
        AddElementKey(timeline, freeze, 3d, k => { k.Frozen = true; k.InterpolationToNext = DirectorReplayElementInterpolation.Hold; });
        return timeline;
    }

    private static void CheckLegacyPlayback(DirectorReplayViewportTimeline timeline)
    {
        DirectorReplayElementKeyframe Single(DirectorReplayElementTrackType type)
            => timeline.ElementKeyframes.First(k => k.TrackType == type);
        Check.True(timeline.ElementKeyframes.Where(k => k.TrackType == DirectorReplayElementTrackType.FollowEntity).All(k => k.Roll == 0f));
        Check.NearlyEqual(0d, Single(DirectorReplayElementTrackType.CameraShake).Roll, 1e-6);
        Check.NearlyEqual(5d, Single(DirectorReplayElementTrackType.CameraOrbit).Roll, 1e-6);
        Check.NearlyEqual(7d, Single(DirectorReplayElementTrackType.TrackEntity).Roll, 1e-6);
        foreach (DirectorReplayElementKeyframe keyframe in timeline.ElementKeyframes)
        {
            Check.False(keyframe.EndsElement);
            Check.Equal(DirectorReplayElementInterpolation.Hold, keyframe.FollowBlend);
            Check.False(keyframe.FollowWorldAligned);
            Check.Equal(DirectorReplayTrackMode.Attached, keyframe.TrackMode);
            Check.Equal(DirectorReplayTimeEffect.Freeze, keyframe.TimeEffect);
            Check.False(keyframe.FreezeCameraContinues);
        }
        Check.True(timeline.Clips.All(clip => !clip.EndsShot));
        Check.False(timeline.Visuals.Camera.AvoidOcclusion);
        Check.False(timeline.Visuals.Composition.VerticalGuide);

        // A plain freeze still stops content and camera together, as before.
        DirectorReplayElementFreezeState freeze = DirectorReplayElementTimeline.SampleFreeze(timeline, 5d);
        Check.True(freeze.Frozen);
        Check.True(freeze.CameraTimeSeconds is null);
        Check.Equal(DirectorReplayTimeEffect.Freeze, freeze.Effect);
    }

    /// <summary>Older files load with defaults that keep their playback unchanged.</summary>
    private static void OlderFilesKeepTheirPlayback()
    {
        DirectorReplayDocument legacy = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            SchemaVersion = 22,
            Name = "Legacy",
            DurationSeconds = 20d,
            ViewportTimeline = LegacyTimeline()
        });
        Check.Equal(23, legacy.SchemaVersion);
        CheckLegacyPlayback(legacy.ViewportTimeline);

        // Current files keep follow/shake roll (it is now applied).
        DirectorReplayDocument current = DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            Name = "Current",
            DurationSeconds = 20d,
            ViewportTimeline = LegacyTimeline()
        });
        Check.NearlyEqual(10d, current.ViewportTimeline.ElementKeyframes
            .First(k => k.TrackType == DirectorReplayElementTrackType.CameraShake).Roll, 1e-6);

        string root = Path.Combine(Path.GetTempPath(), "vsdirector-preset-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sidecarPath = Path.Combine(root, "take.vsdreplay.gz.timeline.json");
            DirectorReplayTimelineEditDocument legacyEdits = new()
            {
                SchemaVersion = 7,
                ReplayId = legacy.Id,
                ViewportTimeline = LegacyTimeline()
            };
            DirectorReplayTimelineEditIO.Save(sidecarPath, legacyEdits);
            DirectorReplayTimelineEditDocument loaded = Check.NotNull(DirectorReplayTimelineEditIO.Load(sidecarPath));
            Check.Equal(8, loaded.SchemaVersion);
            DirectorReplayTimelineEditIO.Apply(legacy, loaded);
            CheckLegacyPlayback(legacy.ViewportTimeline);

            // Apply without Load (an in-memory schema 7 document) migrates too.
            legacyEdits.ViewportTimeline = LegacyTimeline();
            DirectorReplayTimelineEditIO.Apply(legacy, legacyEdits);
            CheckLegacyPlayback(legacy.ViewportTimeline);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A reader rejects files from a newer schema instead of opening them and silently
    /// dropping what it does not know when it saves.
    /// </summary>
    private static void NewerFilesAreRejected()
    {
        Check.Throws<NotSupportedException>(() => DirectorReplayMath.Normalize(new DirectorReplayDocument
        {
            SchemaVersion = DirectorReplayDocument.CurrentSchemaVersion + 1
        }));
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-preset-newer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sidecarPath = Path.Combine(root, "take.vsdreplay.gz.timeline.json");
            DirectorReplayTimelineEditIO.Save(sidecarPath, new DirectorReplayTimelineEditDocument
            {
                SchemaVersion = DirectorReplayTimelineEditDocument.CurrentSchemaVersion + 1
            });
            Check.Throws<NotSupportedException>(() => DirectorReplayTimelineEditIO.Load(sidecarPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
