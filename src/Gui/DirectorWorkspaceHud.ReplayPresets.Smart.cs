using VSDirector.Data;
using VSDirector.Replay;

namespace VSDirector.Gui;

/// <summary>
/// Presets that read the recording (voice, sounds, deaths, block edits, movement) and
/// bake the result into ordinary tracks and markers, so the outcome can be edited.
/// </summary>
internal sealed partial class DirectorWorkspaceHud
{
    private delegate void PresetAdder(string id, string group,
        System.Func<ReplayPresetContext, Guid?> apply, PresetNeeds needs = PresetNeeds.None);

    private readonly record struct SmartShot(double Start, double Length, DirectorReplayActorPathSource Actor,
        Action<DirectorReplayElementKeyframe> Rig);

    private readonly record struct SmartSegment(double Start, double End, string ActorKey);

    private readonly record struct SmartEvent(double Time, double Score, string Kind, string ActorKey);

    private static void AddSmartPresets(PresetAdder add)
    {
        add("smart-auto-director", PresetSmartGroup, c =>
            {
                double end = SmartEnd(c, 60d);
                List<SmartSegment> turns = VoiceTurns(c, c.Start, end);
                List<SmartEvent> hits = HurtEvents(c, c.Start, end);
                Action<DirectorReplayElementKeyframe>[] travel = { RigChase, RigWide, RigShoulder, RigFront, RigDrone, RigSide(c) };
                var shots = new List<SmartShot>();
                int rig = 0;
                double t = c.Start;
                while (t < end - 0.5d)
                {
                    SmartSegment? turn = turns.Where(item => item.End > t + 0.5d && item.Start <= t + 0.25d)
                        .Select(item => (SmartSegment?)item).FirstOrDefault();
                    if (turn is SmartSegment talking && SourceByKey(c, talking.ActorKey) is DirectorReplayActorPathSource speaker)
                    {
                        double length = Math.Clamp(talking.End - t, 1.5d, 6d);
                        shots.Add(new SmartShot(t, length, speaker, RigFaceOn));
                        t += length;
                        continue;
                    }
                    if (c.Player is not DirectorReplayActorPathSource player) break;
                    bool fight = hits.Any(hit => hit.Time >= t - 0.5d && hit.Time <= t + 3d);
                    double shot = fight ? 2.5d : 5d;
                    // Do not run into the next speech turn.
                    double nextTurn = turns.Where(item => item.Start > t + 0.25d).Select(item => item.Start).DefaultIfEmpty(end).Min();
                    shot = Math.Max(1.2d, Math.Min(shot, nextTurn - t));
                    shots.Add(new SmartShot(t, shot, player, fight ? (rig % 2 == 0 ? RigHip : RigLowHero) : travel[rig % travel.Length]));
                    rig++;
                    t += shot;
                }
                return SmartCuts(c, "smart-auto-director", shots, "replay-preset-no-player");
            }, PresetNeeds.Player);
        add("smart-combat", PresetSmartGroup, c =>
            {
                List<SmartEvent> hits = HurtEvents(c, c.Start, SmartEnd(c, 120d));
                if (hits.Count == 0)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-combat"));
                    return null;
                }
                var shake = new List<ShakeStep>();
                var fov = new List<FovStep>();
                Guid? id = null;
                foreach (SmartEvent hit in hits)
                {
                    double at = hit.Time - c.Start;
                    shake.Add(new(at, 0));
                    shake.Add(new(at + 0.04, 1, IEaseOut));
                    shake.Add(new(at + 0.4, 0));
                    fov.Add(new(at, c.BaseFov, IEaseOut));
                    fov.Add(new(at + 0.07, c.BaseFov + 10f, IEaseOut));
                    fov.Add(new(at + 0.5, c.BaseFov, IHold));
                    id = TimeEffectPreset(c, at, at + 0.08, DirectorReplayTimeEffect.Freeze, cameraContinues: true);
                }
                ShakePreset(c, 10f, 1.4f, 9f, 1.8f, 0f, shake.ToArray());
                FovPreset(c, true, fov.ToArray());
                return id;
            }, PresetNeeds.Player);
        add("smart-combat-frame", PresetSmartGroup, c =>
            {
                if (NearestOtherActor(c, true) is not { } enemy)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-creature"));
                    return null;
                }
                return TripodPreset(c, "smart-combat-frame", 20d, k =>
                {
                    k.SecondaryEntityActorKey = enemy.ActorKey; k.SecondaryEntityId = enemy.EntityId;
                    k.TargetBlend = 0.5d; k.TrackAutoFrame = true; k.TrackFrameSize = 0.75d; k.TrackSmoothTimeSeconds = 0.35d;
                    k.BodyPart = "body";
                }, (0d, c.CameraPoint));
            }, PresetNeeds.Player);
        add("smart-kill-cam", PresetSmartGroup, c =>
            {
                List<SmartEvent> deaths = Deaths(c, c.Start, c.Duration);
                if (deaths.Count == 0 || SourceByKey(c, deaths[0].ActorKey) is not DirectorReplayActorPathSource victim)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-death"));
                    return null;
                }
                double at = deaths[0].Time - c.Start;
                DirectorReplayElementTrack track = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, "smart-kill-cam", PlayerLabel(victim));
                AddFollowInterval(c, track, victim, at - 2d, 6d, k =>
                {
                    k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = c.Pose.Yaw;
                    k.FollowDistance = 4d; k.FollowHeight = 1.2d; k.FollowSmoothTimeSeconds = 0.15d; k.FollowFov = 60f;
                    k.FollowAnchor = CameraFollowAnchor.Body;
                }, k => k.FollowOrbitAngleDegrees = NormalizeDegrees(c.Pose.Yaw + 170d), IEase);
                SpeedPreset(c, new(at - 2d, 1, IEaseIn), new(at - 1.2d, 0.3, IHold), new(at + 0.2, 1));
                TimeEffectPreset(c, at + 0.2, at + 3, DirectorReplayTimeEffect.Freeze, cameraContinues: true);
                GradePreset(c, true, new GradeStep(at - 2d), new GradeStep(at, Sepia: 0.5f, Contrast: 0.2f, Vignette: 0.45f),
                    new GradeStep(at + 3, Sepia: 0.5f, Contrast: 0.2f, Vignette: 0.45f), new GradeStep(at + 4));
                c.Hud.owner.TryAddReplayMarkerQuietly(PresetLang("vsdirector:replay-preset-marker-death") + " · " + PrettyActorName(victim),
                    deaths[0].Time);
                return track.Id;
            });
        add("smart-highlights", PresetSmartGroup, c =>
            {
                List<SmartEvent> events = Highlights(c, 0d, c.Duration, minGap: 4d);
                if (events.Count == 0)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-highlights"));
                    return null;
                }
                foreach (SmartEvent item in events.Take(60))
                {
                    c.Hud.owner.TryAddReplayMarkerQuietly(HighlightLabel(c, item), item.Time);
                }
                c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-highlights-added") + " " + Math.Min(60, events.Count));
                return null;
            });
        add("smart-montage", PresetSmartGroup, c =>
            {
                double end = SmartEnd(c, 600d);
                List<SmartEvent> events = Highlights(c, c.Start, end, minGap: 0.5d, includeBlocks: true);
                var steps = new List<RateStep>();
                double boringFrom = c.Start;
                void Close(double until)
                {
                    if (until - boringFrom >= 8d)
                    {
                        steps.Add(new(boringFrom + 1d - c.Start, 1, IEase));
                        steps.Add(new(boringFrom + 1.6d - c.Start, 4, IHold));
                        steps.Add(new(until - 2.5d - c.Start, 4, IEase));
                        steps.Add(new(until - 1.5d - c.Start, 1, IHold));
                    }
                }
                foreach (SmartEvent item in events)
                {
                    Close(item.Time - 1d);
                    boringFrom = Math.Max(boringFrom, item.Time + 2d);
                }
                Close(end);
                if (steps.Count == 0)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-nothing-boring"));
                    return null;
                }
                return SpeedPreset(c, steps.ToArray());
            });
        add("smart-trailer", PresetSmartGroup, c =>
            {
                List<SmartEvent> top = Highlights(c, c.Start + 1d, c.Duration - 1d, minGap: 6d)
                    .OrderByDescending(item => item.Score).Take(8).OrderBy(item => item.Time).ToList();
                if (top.Count == 0)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-highlights"));
                    return null;
                }
                Action<DirectorReplayElementKeyframe>[] rigs = TrailerRigs(c);
                var shots = new List<SmartShot>();
                var rates = new List<RateStep>();
                double previousEnd = c.Start;
                for (int i = 0; i < top.Count; i++)
                {
                    SmartEvent item = top[i];
                    DirectorReplayActorPathSource? actor = SourceByKey(c, item.ActorKey) ?? c.Player;
                    if (actor is not DirectorReplayActorPathSource subject) continue;
                    double shotStart = item.Time - 1d;
                    if (shotStart - previousEnd > 3d)
                    {
                        rates.Add(new(previousEnd + 0.2d - c.Start, 8, IHold));
                        rates.Add(new(shotStart - c.Start, 1, IHold));
                    }
                    shots.Add(new SmartShot(shotStart, 2.2d, subject, rigs[i % rigs.Length]));
                    double at = item.Time - c.Start;
                    FovPreset(c, false, new FovStep(at - 1d, c.BaseFov - 10f, IEaseOut), new FovStep(at - 0.6d, c.BaseFov, IHold));
                    TimeEffectPreset(c, at, at + 0.12d, DirectorReplayTimeEffect.Freeze, cameraContinues: true);
                    previousEnd = shotStart + 2.2d;
                }
                if (rates.Count > 0)
                {
                    rates.Add(new(previousEnd - c.Start, 1, IHold));
                    SpeedPreset(c, rates.ToArray());
                }
                TitlePreset(c, previousEnd - c.Start, PresetLang("vsdirector:replay-preset-trailer-title"), string.Empty, 3d,
                    DirectorReplayTitlePosition.Center);
                GradePreset(c, false, new GradeStep(previousEnd - c.Start, Interpolation: ILinear),
                    new GradeStep(previousEnd + 3d - c.Start, Fade: 1f, Interpolation: IHold));
                return SmartCuts(c, "smart-trailer", shots, "replay-preset-no-highlights", allowGaps: true);
            });
        add("smart-chase", PresetSmartGroup, c =>
            {
                double end = SmartEnd(c, 30d);
                IReadOnlyList<DirectorReplayActorPathSource> players = c.Hud.ReplayPlayerSources().Where(item => item.IsPlayer).ToList();
                if (players.Count == 0 || c.Document is null)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-player"));
                    return null;
                }
                var shots = new List<SmartShot>();
                DirectorReplayActorPathSource? current = null;
                double shotStart = c.Start;
                for (double t = c.Start; t < end; t += 0.5d)
                {
                    DirectorReplayActorPathSource fastest = players
                        .OrderByDescending(player => ActorSpeed(c.Document, player.ActorKey, t))
                        .First();
                    if (current is null)
                    {
                        current = fastest;
                        continue;
                    }
                    if (fastest.ActorKey != current.Value.ActorKey && t - shotStart >= 3d
                        && ActorSpeed(c.Document, fastest.ActorKey, t) > ActorSpeed(c.Document, current.Value.ActorKey, t) + 1d)
                    {
                        shots.Add(new SmartShot(shotStart, t - shotStart, current.Value, RigChase));
                        current = fastest;
                        shotStart = t;
                    }
                }
                if (current is DirectorReplayActorPathSource last && end - shotStart > 0.2d)
                {
                    shots.Add(new SmartShot(shotStart, end - shotStart, last, RigChase));
                }
                return SmartCuts(c, "smart-chase", shots, "replay-preset-no-player");
            });
        add("smart-build-cam", PresetSmartGroup, c =>
            {
                double end = SmartEnd(c, 600d);
                List<DirectorReplayBlockEvent> edits = BlockEdits(c, c.Start, end);
                if (edits.Count < 4)
                {
                    c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:replay-preset-no-building"));
                    return null;
                }
                int dimension = edits.GroupBy(item => item.Dimension).OrderByDescending(group => group.Count()).First().Key;
                edits = edits.Where(item => item.Dimension == dimension).ToList();
                double cx = edits.Average(item => item.X + 0.5d);
                double cy = edits.Average(item => item.Y + 0.5d);
                double cz = edits.Average(item => item.Z + 0.5d);
                // Ignore the far-away few (a stray dig) when sizing the shot.
                List<double> spread = edits.Select(item => Math.Sqrt((item.X - cx) * (item.X - cx) + (item.Z - cz) * (item.Z - cz)))
                    .OrderBy(value => value).ToList();
                double radius = spread[(int)(spread.Count * 0.9d) - (spread.Count >= 10 ? 1 : 0)];
                double first = edits.Min(item => item.TimeSeconds);
                double last = Math.Max(first + 2d, edits.Max(item => item.TimeSeconds));
                double distance = Math.Clamp(radius * 2.4d + 6d, 10d, 120d);
                PresetPoint center = new(cx, cy, cz, dimension);
                if (last - first > 20d)
                {
                    TimelapsePreset(c, "smart-build-cam", first - c.Start, last - first, 15d);
                }
                return OrbitPreset(c, "smart-build-cam", center,
                    new(first - c.Start, c.Pose.Yaw, -30, distance), new(last - c.Start, c.Pose.Yaw + 360, -30, distance));
            });
    }

    // ----- Rigs shared by smart cuts ------------------------------------------------------

    private static void RigFaceOn(DirectorReplayElementKeyframe k)
    {
        k.FollowDistance = 2.6d; k.FollowHeight = 0.15d; k.FollowOrbitAngleDegrees = 165d; k.FollowFov = 55f;
    }

    private static void RigChase(DirectorReplayElementKeyframe k)
    {
        k.FollowDistance = 5d; k.FollowHeight = 1.5d; k.FollowFov = 70f;
    }

    private static void RigWide(DirectorReplayElementKeyframe k)
    {
        k.FollowDistance = 14d; k.FollowHeight = 5d; k.FollowSmoothTimeSeconds = 1d; k.FollowFov = 55f;
    }

    private static void RigShoulder(DirectorReplayElementKeyframe k)
    {
        k.FollowDistance = 2.2d; k.FollowHeight = 0.35d; k.FollowOffsetX = 0.6d;
    }

    private static void RigFront(DirectorReplayElementKeyframe k)
    {
        k.FollowDistance = 6d; k.FollowHeight = 0.6d; k.FollowOrbitAngleDegrees = 180d;
    }

    private static void RigDrone(DirectorReplayElementKeyframe k)
    {
        k.FollowMotionMode = CameraFollowMotionMode.Delayed; k.FollowDelaySeconds = 1d;
        k.FollowDistance = 9d; k.FollowHeight = 6d; k.FollowFov = 60f;
    }

    private static void RigHip(DirectorReplayElementKeyframe k)
    {
        k.FollowDistance = 1.4d; k.FollowHeight = -0.35d; k.FollowOffsetX = 0.5d; k.FollowSmoothTimeSeconds = 0.12d; k.FollowFov = 75f;
    }

    private static void RigLowHero(DirectorReplayElementKeyframe k)
    {
        k.FollowDistance = 3d; k.FollowHeight = -0.9d; k.FollowOffsetX = 0.4d; k.FollowSmoothTimeSeconds = 0.15d;
    }

    private static Action<DirectorReplayElementKeyframe> RigSide(ReplayPresetContext c) => k =>
    {
        k.FollowWorldAligned = true; k.FollowOrbitAngleDegrees = NormalizeDegrees(c.Pose.Yaw + 90d);
        k.FollowDistance = 7d; k.FollowHeight = 0.5d;
    };

    private static double NormalizeDegrees(double value)
    {
        double result = (value + 180d) % 360d;
        if (result < 0d) result += 360d;
        return result - 180d;
    }

    /// <summary>Writes shots as alternating A/B follow rows (absolute replay times).</summary>
    private static Guid? SmartCuts(ReplayPresetContext c, string id, List<SmartShot> shots, string emptyKey,
        bool allowGaps = false)
    {
        shots = shots.Where(shot => shot.Length > 0.2d).OrderBy(shot => shot.Start).ToList();
        if (shots.Count == 0)
        {
            c.Hud.capi.ShowChatMessage(PresetLang("vsdirector:" + emptyKey));
            return null;
        }
        DirectorReplayElementTrack a = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, id, "A");
        DirectorReplayElementTrack b = PresetNewTrack(c, DirectorReplayElementTrackType.FollowEntity, id, "B");
        for (int i = 0; i < shots.Count; i++)
        {
            SmartShot shot = shots[i];
            double length = shot.Length;
            if (!allowGaps && i + 1 < shots.Count)
            {
                // Close tiny gaps so the view does not flick back to the free camera.
                length = Math.Max(length, shots[i + 1].Start - shot.Start);
            }
            AddFollowInterval(c, i % 2 == 0 ? a : b, shot.Actor, shot.Start - c.Start, length, k =>
            {
                k.FollowSmoothTimeSeconds = 0.25d;
                k.FollowFov = 65f;
                shot.Rig(k);
            });
        }
        if (!c.Timeline.ElementKeyframes.Any(item => item.TrackId == b.Id))
        {
            c.Timeline.Tracks.Remove(b);
        }
        return a.Id;
    }

    // ----- Recording analysis ------------------------------------------------------------------

    private static double SmartEnd(ReplayPresetContext c, double seconds) => Math.Min(c.Duration, c.Start + seconds);

    private static DirectorReplayActorPathSource? SourceByKey(ReplayPresetContext c, string actorKey)
    {
        foreach (DirectorReplayActorPathSource source in c.Hud.owner.ReplayActorPathSources)
        {
            if (source.ActorKey == actorKey) return source;
        }
        return null;
    }

    /// <summary>Index of the first item at or after <paramref name="time"/>.</summary>
    private static int FirstAtOrAfter<T>(IReadOnlyList<T> items, Func<T, double> time, double value)
    {
        int low = 0;
        int high = items.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (time(items[mid]) < value) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static DirectorReplayActorTrack? ActorTrack(DirectorReplayDocument document, string actorKey)
        => document.Actors.FirstOrDefault(actor => actor.ActorKey == actorKey);

    private static DirectorReplayActorSample? SampleAt(DirectorReplayActorTrack track, double time)
    {
        IReadOnlyList<DirectorReplayActorSample> samples = track.Samples;
        if (samples.Count == 0) return null;
        int index = FirstAtOrAfter(samples, sample => sample.TimeSeconds, time);
        if (index >= samples.Count) return null;
        if (index > 0 && time - samples[index - 1].TimeSeconds < samples[index].TimeSeconds - time)
        {
            index--;
        }
        return Math.Abs(samples[index].TimeSeconds - time) <= 2d ? samples[index] : null;
    }

    /// <summary>Horizontal speed in blocks per second around <paramref name="time"/>.</summary>
    private static double ActorSpeed(DirectorReplayDocument document, string actorKey, double time)
    {
        if (ActorTrack(document, actorKey) is not DirectorReplayActorTrack track
            || SampleAt(track, time - 0.25d) is not DirectorReplayActorSample before
            || SampleAt(track, time + 0.25d) is not DirectorReplayActorSample after
            || after.TimeSeconds - before.TimeSeconds < 0.05d)
        {
            return 0d;
        }
        double dx = after.X - before.X;
        double dz = after.Z - before.Z;
        double speed = Math.Sqrt(dx * dx + dz * dz) / (after.TimeSeconds - before.TimeSeconds);
        return speed > 60d ? 0d : speed; // teleports
    }

    private static DirectorReplayActorPathSource? NearestPlayerAt(ReplayPresetContext c, string actorKey, double time)
    {
        if (c.Document is null || ActorTrack(c.Document, actorKey) is not DirectorReplayActorTrack origin
            || SampleAt(origin, time) is not DirectorReplayActorSample from)
        {
            return null;
        }
        DirectorReplayActorPathSource? best = null;
        double bestDistance = 48d;
        foreach (DirectorReplayActorPathSource source in c.Hud.owner.ReplayActorPathSources)
        {
            if (!source.IsPlayer || source.ActorKey == actorKey
                || ActorTrack(c.Document, source.ActorKey) is not DirectorReplayActorTrack track
                || SampleAt(track, time) is not DirectorReplayActorSample at)
            {
                continue;
            }
            double distance = Math.Sqrt((at.X - from.X) * (at.X - from.X) + (at.Y - from.Y) * (at.Y - from.Y)
                + (at.Z - from.Z) * (at.Z - from.Z));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = source;
            }
        }
        return best;
    }

    /// <summary>
    /// Who talks when: voice frames are bucketed into quarter seconds, the loudest
    /// speaker above a noise floor wins each bucket, short pauses are bridged and turns
    /// shorter than 1.2 s are merged into their neighbour.
    /// </summary>
    private static List<SmartSegment> VoiceTurns(ReplayPresetContext c, double from, double to)
    {
        var result = new List<SmartSegment>();
        if (c.Document is null || to <= from) return result;
        IReadOnlyList<DirectorReplayVoiceFrame> frames = c.Document.VoiceFrames;
        const double bin = 0.25d;
        int bins = (int)Math.Ceiling((to - from) / bin);
        var loudness = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var actorBySpeaker = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (int index = FirstAtOrAfter(frames, frame => frame.TimeSeconds, from); index < frames.Count; index++)
        {
            DirectorReplayVoiceFrame frame = frames[index];
            if (frame.TimeSeconds >= to) break;
            byte[] pcm = frame.Pcm16;
            int count = pcm.Length / 2;
            if (count == 0) continue;
            string speaker = frame.SpeakerId.Length > 0 ? frame.SpeakerId : frame.SpeakerName;
            if (!actorBySpeaker.TryGetValue(speaker, out string? actorKey))
            {
                actorKey = c.Hud.owner.ReplayActorPathSources
                    .Where(source => source.IsPlayer
                        && ((frame.SpeakerId.Length > 0 && source.PlayerUid == frame.SpeakerId)
                            || (frame.SpeakerName.Length > 0 && source.DisplayName == frame.SpeakerName)))
                    .Select(source => source.ActorKey)
                    .FirstOrDefault();
                actorBySpeaker[speaker] = actorKey;
            }
            if (actorKey is null) continue;
            double sum = 0d;
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                double sample = (short)(pcm[i] | (pcm[i + 1] << 8)) / 32768d;
                sum += sample * sample;
            }
            double rms = Math.Sqrt(sum / count);
            int start = (int)((frame.TimeSeconds - from) / bin);
            int end = (int)((frame.TimeSeconds + count / (double)DirectorReplayVoiceFrame.SampleRate - from) / bin);
            if (!loudness.TryGetValue(actorKey, out double[]? values))
            {
                values = new double[bins];
                loudness[actorKey] = values;
            }
            for (int b = Math.Max(0, start); b <= Math.Min(bins - 1, end); b++)
            {
                values[b] = Math.Max(values[b], rms);
            }
        }
        if (loudness.Count == 0) return result;

        const double noiseFloor = 0.02d;
        var owners = new string?[bins];
        for (int b = 0; b < bins; b++)
        {
            double best = noiseFloor;
            foreach ((string key, double[] values) in loudness)
            {
                if (values[b] > best)
                {
                    best = values[b];
                    owners[b] = key;
                }
            }
        }
        // Bridge pauses up to 1 s inside one speaker's turn.
        string? last = null;
        int silent = 0;
        for (int b = 0; b < bins; b++)
        {
            if (owners[b] is null)
            {
                silent++;
                if (last is not null && silent <= 4) owners[b] = last;
                continue;
            }
            silent = 0;
            last = owners[b];
        }
        int runStart = -1;
        for (int b = 0; b <= bins; b++)
        {
            string? owner = b < bins ? owners[b] : null;
            if (runStart >= 0 && (b == bins || owner != owners[runStart]))
            {
                result.Add(new SmartSegment(from + runStart * bin, from + b * bin, owners[runStart]!));
                runStart = -1;
            }
            if (runStart < 0 && owner is not null) runStart = b;
        }
        // Merge very short turns into the previous one so cuts are not frantic.
        var merged = new List<SmartSegment>();
        foreach (SmartSegment turn in result)
        {
            if (merged.Count > 0 && (turn.End - turn.Start < 1.2d || merged[^1].ActorKey == turn.ActorKey)
                && turn.Start - merged[^1].End < 1.5d)
            {
                merged[^1] = merged[^1] with { End = turn.End };
                continue;
            }
            merged.Add(turn);
        }
        return merged.Where(turn => turn.End - turn.Start >= 0.75d).ToList();
    }

    /// <summary>"hurt" sounds near the player (or anywhere when no player), 0.3 s apart.</summary>
    private static List<SmartEvent> HurtEvents(ReplayPresetContext c, double from, double to)
    {
        var result = new List<SmartEvent>();
        if (c.Document is null) return result;
        IReadOnlyList<DirectorReplaySoundEvent> sounds = c.Document.SoundEvents;
        DirectorReplayActorTrack? player = c.Player is DirectorReplayActorPathSource source ? ActorTrack(c.Document, source.ActorKey) : null;
        double lastTime = double.NegativeInfinity;
        for (int index = FirstAtOrAfter(sounds, sound => sound.TimeSeconds, from); index < sounds.Count; index++)
        {
            DirectorReplaySoundEvent sound = sounds[index];
            if (sound.TimeSeconds >= to) break;
            if (sound.Action != DirectorReplaySoundAction.Play
                || sound.Location.IndexOf("hurt", StringComparison.OrdinalIgnoreCase) < 0
                || sound.TimeSeconds - lastTime < 0.3d)
            {
                continue;
            }
            if (player is not null && !sound.RelativePosition && SampleAt(player, sound.TimeSeconds) is DirectorReplayActorSample at)
            {
                double distance = Math.Sqrt((sound.X - at.X) * (sound.X - at.X) + (sound.Y - at.Y) * (sound.Y - at.Y)
                    + (sound.Z - at.Z) * (sound.Z - at.Z));
                if (distance > 24d) continue;
            }
            result.Add(new SmartEvent(sound.TimeSeconds, 3d, "hit", c.Player?.ActorKey ?? string.Empty));
            lastTime = sound.TimeSeconds;
        }
        return result;
    }

    /// <summary>Moments an actor starts its "die" animation.</summary>
    private static List<SmartEvent> Deaths(ReplayPresetContext c, double from, double to)
    {
        var result = new List<SmartEvent>();
        if (c.Document is null) return result;
        foreach (DirectorReplayActorTrack actor in c.Document.Actors)
        {
            IReadOnlyList<DirectorReplayActorSample> samples = actor.Samples;
            int index = FirstAtOrAfter(samples, sample => sample.TimeSeconds, from);
            bool wasDying = index > 0 && IsDying(samples[index - 1]);
            for (; index < samples.Count; index++)
            {
                DirectorReplayActorSample sample = samples[index];
                if (sample.TimeSeconds >= to) break;
                bool dying = IsDying(sample);
                if (dying && !wasDying)
                {
                    bool isPlayer = actor.PlayerUid.Length > 0;
                    result.Add(new SmartEvent(sample.TimeSeconds, isPlayer ? 10d : 5d, "death", actor.ActorKey));
                }
                wasDying = dying;
            }
        }
        result.Sort((left, right) => left.Time.CompareTo(right.Time));
        return result;
    }

    private static bool IsDying(DirectorReplayActorSample sample)
        => sample.Animations.Any(animation => animation.StartsWith("die", StringComparison.OrdinalIgnoreCase)
            || animation.Equals("dead", StringComparison.OrdinalIgnoreCase));

    private static List<DirectorReplayBlockEvent> BlockEdits(ReplayPresetContext c, double from, double to)
    {
        var result = new List<DirectorReplayBlockEvent>();
        if (c.Document is null) return result;
        IReadOnlyList<DirectorReplayBlockEvent> events = c.Document.BlockEvents;
        for (int index = FirstAtOrAfter(events, item => item.TimeSeconds, from); index < events.Count; index++)
        {
            DirectorReplayBlockEvent item = events[index];
            if (item.TimeSeconds >= to) break;
            result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Ranked moments: deaths, fights, speech starts and bursts of block edits, spaced at
    /// least <paramref name="minGap"/> seconds apart (the higher score wins).
    /// </summary>
    private static List<SmartEvent> Highlights(ReplayPresetContext c, double from, double to, double minGap,
        bool includeBlocks = true)
    {
        var events = new List<SmartEvent>();
        events.AddRange(Deaths(c, from, to));
        List<SmartEvent> hits = HurtEvents(c, from, to);
        // A fight is several hits close together; score by how many.
        for (int i = 0; i < hits.Count; i++)
        {
            int count = hits.Count(hit => hit.Time >= hits[i].Time && hit.Time < hits[i].Time + 5d);
            events.Add(hits[i] with { Score = 1d + count, Kind = "fight" });
        }
        foreach (SmartSegment turn in VoiceTurns(c, from, to))
        {
            events.Add(new SmartEvent(turn.Start, 1.5d + Math.Min(3d, (turn.End - turn.Start) / 4d), "speech", turn.ActorKey));
        }
        if (includeBlocks)
        {
            List<DirectorReplayBlockEvent> edits = BlockEdits(c, from, to);
            int start = 0;
            for (int i = 0; i < edits.Count; i++)
            {
                while (edits[i].TimeSeconds - edits[start].TimeSeconds > 10d) start++;
                int burst = i - start + 1;
                if (burst >= 12 && (i + 1 == edits.Count || edits[i + 1].TimeSeconds - edits[i].TimeSeconds > 2d))
                {
                    events.Add(new SmartEvent(edits[start].TimeSeconds, 1d + Math.Min(4d, burst / 15d), "build", string.Empty));
                    start = i + 1;
                }
            }
        }
        var kept = new List<SmartEvent>();
        foreach (SmartEvent item in events.OrderByDescending(item => item.Score))
        {
            if (kept.All(other => Math.Abs(other.Time - item.Time) >= minGap)) kept.Add(item);
        }
        kept.Sort((left, right) => left.Time.CompareTo(right.Time));
        return kept;
    }

    private static string HighlightLabel(ReplayPresetContext c, SmartEvent item)
    {
        string name = SourceByKey(c, item.ActorKey) is DirectorReplayActorPathSource source ? " · " + PrettyActorName(source) : string.Empty;
        return PresetLang("vsdirector:replay-preset-marker-" + item.Kind) + name;
    }
}
