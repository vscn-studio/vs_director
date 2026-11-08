namespace VSDirector.Data;

public readonly record struct DirectorReplayActorPosition(
    double X,
    double Y,
    double Z,
    int Dimension);

public static class DirectorReplayMath
{
    private const double TimeEpsilon = 0.000001d;
    private const int ChunkBlockCount = 32 * 32 * 32;

    internal static IEqualityComparer<DirectorReplayParticleEvent> ParticlePayloadComparer { get; } =
        new ReplayParticlePayloadComparer();

    public static DirectorReplayDocument Normalize(DirectorReplayDocument? source, CancellationToken cancellationToken = default)
    {
        if (source is not null && source.SchemaVersion > DirectorReplayDocument.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Replay schema {source.SchemaVersion} is newer than supported schema {DirectorReplayDocument.CurrentSchemaVersion}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int sourceSchemaVersion = source?.SchemaVersion
            ?? DirectorReplayDocument.CurrentSchemaVersion;
        DirectorReplayDocument result = new()
        {
            SchemaVersion = DirectorReplayDocument.CurrentSchemaVersion,
            Id = source?.Id is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(source?.Name) ? "Replay" : source.Name.Trim(),
            WorldId = source?.WorldId?.Trim() ?? string.Empty,
            ServerRecording = source?.ServerRecording?.Copy(),
            ServerReplay = source?.ServerReplay?.Copy(),
            SessionMerge = source?.SessionMerge?.Copy(),
            CreatedUtc = source?.CreatedUtc is { } created && created != default
                ? created.ToUniversalTime()
                : now,
            SampleRate = NormalizeSampleRate(source?.SampleRate ?? 30d),
            ParticleCaptureVersion = Math.Clamp(
                source?.ParticleCaptureVersion ?? 0,
                0,
                DirectorReplayDocument.CurrentParticleCaptureVersion),
            WorldSnapshot = NormalizeWorldSnapshot(source?.WorldSnapshot, cancellationToken)
        };
        result.IncludesWorldSnapshot = result.WorldSnapshot.Chunks.Count > 0;
        result.CaptureScope = source?.CaptureScope?.StartsWith("server-authoritative", StringComparison.Ordinal) == true
            ? source.CaptureScope
            : result.IncludesWorldSnapshot
                ? DirectorReplayDocument.ClientWorldCaptureScope
                : DirectorReplayDocument.ClientActorCaptureScope;

        HashSet<string> keys = new(StringComparer.Ordinal);
        if (source?.Actors is not null)
        {
            foreach (DirectorReplayActorTrack? actor in WithCancellation(source.Actors, cancellationToken))
            {
                if (actor is null)
                {
                    continue;
                }

                DirectorReplayActorTrack normalized = NormalizeActor(
                    actor,
                    keys,
                    result.Actors.Count,
                    repairLegacyThirdPersonPose: source.SchemaVersion < 16, cancellationToken);
                if (normalized.Samples.Count > 0)
                {
                    result.Actors.Add(normalized);
                }
            }
        }

        if (source?.BlockEvents is not null)
        {
            result.BlockEvents = WithCancellation(source.BlockEvents, cancellationToken)
                .Where(item => item is not null)
                .Select(NormalizeBlockEvent)
                .OrderBy(item => item.TimeSeconds)
                .ToList();
        }

        if (source?.ParticleEvents is not null)
        {
            result.ParticleEvents = WithCancellation(source.ParticleEvents, cancellationToken)
                .Where(item => item is not null)
                .Select(NormalizeParticleEvent)
                .OrderBy(item => item.TimeSeconds)
                .ToList();
            RemoveDuplicateParticles(result.ParticleEvents);
        }

        if (source?.SoundEvents is not null)
        {
            bool inferLegacyRelativeSounds = source.SchemaVersion < 17;
            result.SoundEvents = WithCancellation(source.SoundEvents, cancellationToken)
                .Where(item => item is not null)
                .Select(item => NormalizeSoundEvent(item, inferLegacyRelativeSounds))
                .OrderBy(item => item.TimeSeconds)
                .ToList();
        }

        if (source?.VoiceFrames is not null)
        {
            result.VoiceFrames = WithCancellation(source.VoiceFrames, cancellationToken)
                .Where(item => item is not null)
                .Select(NormalizeVoiceFrame)
                .Where(item => item.Pcm16.Length > 0)
                .OrderBy(item => item.TimeSeconds)
                .ToList();
        }

        if (source?.ClockSamples is not null)
        {
            result.ClockSamples = WithCancellation(source.ClockSamples, cancellationToken)
                .Where(item => item is not null)
                .Select(item => new DirectorReplayClockSample
                {
                    TimeSeconds = NormalizeNonNegative(item.TimeSeconds),
                    TotalHours = NormalizeFinite(item.TotalHours),
                    CalendarSpeedMul = Math.Clamp(NormalizeFinite(item.CalendarSpeedMul), 0f, 1000f),
                    SeasonOverride = item.SeasonOverride is float season && float.IsFinite(season)
                        ? Math.Clamp(season, 0f, 1f)
                        : null
                })
                .GroupBy(item => item.TimeSeconds)
                .Select(group => group.Last())
                .OrderBy(item => item.TimeSeconds)
                .ToList();
        }

        double actorDuration = result.Actors.Count == 0
            ? 0d
            : result.Actors.Max(actor => actor.Samples[^1].TimeSeconds);
        double eventDuration = new[]
        {
            result.BlockEvents.LastOrDefault()?.TimeSeconds ?? 0d,
            result.ParticleEvents.LastOrDefault()?.TimeSeconds ?? 0d,
            result.SoundEvents.LastOrDefault()?.TimeSeconds ?? 0d,
            result.VoiceFrames.LastOrDefault()?.TimeSeconds ?? 0d,
            result.ClockSamples.LastOrDefault()?.TimeSeconds ?? 0d
        }.Max();
        result.DurationSeconds = Math.Max(
            Math.Max(actorDuration, eventDuration),
            NormalizeNonNegative(source?.DurationSeconds ?? 0d));
        result.Markers = DirectorReplayTimeline.NormalizeMarkers(source?.Markers, result.DurationSeconds);
        result.SpeedKeyframes = DirectorReplayTimeline.NormalizeSpeedKeyframes(
            source?.SpeedKeyframes,
            result.DurationSeconds);
        DirectorReplayViewportTimeline sourceViewportTimeline = source?.ViewportTimeline?.DeepClone()
            ?? new DirectorReplayViewportTimeline();
        if (sourceSchemaVersion < 19)
        {
            DirectorReplayViewportTimeline.MigrateLegacyTwentyTickTimebase(sourceViewportTimeline);
        }
        result.ViewportTimeline = DirectorReplayViewportTimeline.Normalize(
            sourceViewportTimeline,
            result.DurationSeconds);
        DirectorReplayViewportTimeline.MigrateLegacySpeedKeyframes(
            result.ViewportTimeline,
            result.SpeedKeyframes);
        DirectorReplayVoiceTrackPolicy.EnsureTracks(
            result.ViewportTimeline,
            result.VoiceFrames);
        result.IncludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
            source?.IncludedPreloadColumns);
        result.ExcludedPreloadColumns = DirectorReplayPreloadPlanner.NormalizeOverrideColumns(
            source?.ExcludedPreloadColumns);
        foreach (DirectorReplayActorTrack actor in result.Actors)
        {
            if (actor.Lifetimes.Count == 0)
            {
                actor.Lifetimes.Add(new DirectorReplayActorLifetime
                {
                    StartSeconds = actor.Samples[0].TimeSeconds,
                    EndSeconds = actor.Samples[^1].TimeSeconds
                });
            }
            else
            {
                foreach (DirectorReplayActorLifetime lifetime in actor.Lifetimes)
                {
                    lifetime.EndSeconds = Math.Min(result.DurationSeconds, lifetime.EndSeconds);
                }
            }
        }

        return result;
    }

    internal static IEnumerable<T> WithCancellation<T>(IEnumerable<T> items, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var item in items) { token.ThrowIfCancellationRequested(); yield return item; }
    }

    private static DirectorReplayVoiceFrame NormalizeVoiceFrame(DirectorReplayVoiceFrame source)
    {
        byte[] pcm = source.Pcm16 is { Length: > 1 }
            ? (byte[])source.Pcm16.Clone()
            : Array.Empty<byte>();
        if (pcm.Length % 2 != 0)
        {
            Array.Resize(ref pcm, pcm.Length - 1);
        }
        if (pcm.Length > DirectorReplayVoiceFrame.SampleRate * sizeof(short))
        {
            Array.Resize(ref pcm, DirectorReplayVoiceFrame.SampleRate * sizeof(short));
        }
        float maxDistance = Math.Clamp(NormalizeFinite(source.MaxDistance), 0.1f, 4096f);
        float referenceDistance = Math.Clamp(NormalizeFinite(source.ReferenceDistance), 0.1f, maxDistance);
        return new DirectorReplayVoiceFrame
        {
            TimeSeconds = NormalizeNonNegative(source.TimeSeconds),
            SourceId = source.SourceId?.Trim() ?? string.Empty,
            SpeakerId = source.SpeakerId?.Trim() ?? string.Empty,
            SpeakerName = source.SpeakerName?.Trim() ?? string.Empty,
            X = NormalizeFinite(source.X),
            Y = NormalizeFinite(source.Y),
            Z = NormalizeFinite(source.Z),
            Dimension = Math.Clamp(source.Dimension, -1024, 1024),
            Volume = Math.Clamp(NormalizeFinite(source.Volume), 0f, 4f),
            MaxDistance = maxDistance,
            ReferenceDistance = referenceDistance,
            RolloffFactor = Math.Clamp(NormalizeFinite(source.RolloffFactor), 0f, 32f),
            Spatialized = source.Spatialized,
            Pcm16 = pcm
        };
    }

    private static void RemoveDuplicateParticles(
        DirectorReplayList<DirectorReplayParticleEvent> particles)
    {
        if (particles.Count < 2)
        {
            return;
        }

        HashSet<DirectorReplayParticleEvent> seen = new(ParticlePayloadComparer);
        double groupTime = double.NaN;
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < particles.Count; readIndex++)
        {
            DirectorReplayParticleEvent current = particles[readIndex];
            if (!double.IsFinite(groupTime)
                || current.TimeSeconds - groupTime > TimeEpsilon)
            {
                groupTime = current.TimeSeconds;
                seen.Clear();
            }

            if (!seen.Add(current))
            {
                continue;
            }

            particles[writeIndex++] = current;
        }

        if (writeIndex < particles.Count)
        {
            particles.RemoveRange(writeIndex, particles.Count - writeIndex);
        }
    }

    private sealed class ReplayParticlePayloadComparer : IEqualityComparer<DirectorReplayParticleEvent>
    {
        public bool Equals(DirectorReplayParticleEvent? left, DirectorReplayParticleEvent? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left is null || right is null)
            {
                return false;
            }

            if (left.Kind != right.Kind)
            {
                return false;
            }
            if (left.IsWeatherParticle != right.IsWeatherParticle)
            {
                return false;
            }
            if (left.Kind == DirectorReplayParticleKind.Provider)
            {
                return string.Equals(left.ProviderType, right.ProviderType, StringComparison.Ordinal)
                    && left.ProviderData.AsSpan().SequenceEqual(right.ProviderData);
            }

            return left.Quantity.Equals(right.Quantity)
                && left.Color == right.Color
                && left.MinX.Equals(right.MinX)
                && left.MinY.Equals(right.MinY)
                && left.MinZ.Equals(right.MinZ)
                && left.MaxX.Equals(right.MaxX)
                && left.MaxY.Equals(right.MaxY)
                && left.MaxZ.Equals(right.MaxZ)
                && left.MinVelocityX.Equals(right.MinVelocityX)
                && left.MinVelocityY.Equals(right.MinVelocityY)
                && left.MinVelocityZ.Equals(right.MinVelocityZ)
                && left.MaxVelocityX.Equals(right.MaxVelocityX)
                && left.MaxVelocityY.Equals(right.MaxVelocityY)
                && left.MaxVelocityZ.Equals(right.MaxVelocityZ)
                && left.LifeLength.Equals(right.LifeLength)
                && left.GravityEffect.Equals(right.GravityEffect)
                && left.Scale.Equals(right.Scale)
                && left.ParticleModel == right.ParticleModel;
        }

        public int GetHashCode(DirectorReplayParticleEvent particle)
        {
            unchecked
            {
                int hash = (int)particle.Kind * 397;
                hash = (hash * 31) ^ (particle.IsWeatherParticle ? 1 : 0);
                if (particle.Kind == DirectorReplayParticleKind.Provider)
                {
                    hash = (hash * 31)
                        ^ StringComparer.Ordinal.GetHashCode(particle.ProviderType ?? string.Empty);
                    foreach (byte value in particle.ProviderData ?? Array.Empty<byte>())
                    {
                        hash = (hash * 31) ^ value;
                    }
                    return hash;
                }

                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.Quantity));
                hash = AddHash(hash, particle.Color);
                hash = AddHash(hash, BitConverter.DoubleToInt64Bits(particle.MinX));
                hash = AddHash(hash, BitConverter.DoubleToInt64Bits(particle.MinY));
                hash = AddHash(hash, BitConverter.DoubleToInt64Bits(particle.MinZ));
                hash = AddHash(hash, BitConverter.DoubleToInt64Bits(particle.MaxX));
                hash = AddHash(hash, BitConverter.DoubleToInt64Bits(particle.MaxY));
                hash = AddHash(hash, BitConverter.DoubleToInt64Bits(particle.MaxZ));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.MinVelocityX));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.MinVelocityY));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.MinVelocityZ));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.MaxVelocityX));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.MaxVelocityY));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.MaxVelocityZ));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.LifeLength));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.GravityEffect));
                hash = AddHash(hash, BitConverter.SingleToInt32Bits(particle.Scale));
                hash = AddHash(hash, particle.ParticleModel);
                return hash;
            }
        }

        private static int AddHash(int hash, int value) => (hash * 31) ^ value;

        private static int AddHash(int hash, long value)
            => AddHash(AddHash(hash, (int)value), (int)(value >> 32));
    }
    public static bool TrySample(
        DirectorReplayActorTrack? actor,
        double timeSeconds,
        out DirectorReplayActorSample sample)
    {
        sample = new DirectorReplayActorSample();
        return TrySample(actor, timeSeconds, sample);
    }

    public static bool TrySamplePosition(
        DirectorReplayActorTrack? actor,
        double timeSeconds,
        out DirectorReplayActorPosition position)
        => TrySamplePosition(actor, timeSeconds, interpolate: true, out position);

    public static bool TrySamplePosition(
        DirectorReplayActorTrack? actor,
        double timeSeconds,
        bool interpolate,
        out DirectorReplayActorPosition position)
    {
        position = default;
        if (actor?.Samples is not { Count: > 0 } samples)
        {
            return false;
        }

        double time = double.IsFinite(timeSeconds) ? Math.Max(0d, timeSeconds) : 0d;
        if (DirectorReplayTimeSearch.LowerBound(samples, time, static s => s.TimeSeconds) == 0)
        {
            position = PositionOf(samples[0]);
            return true;
        }
        if (DirectorReplayTimeSearch.UpperBound(samples, time, static s => s.TimeSeconds) == samples.Count)
        {
            position = PositionOf(samples[^1]);
            return true;
        }

        int high = Math.Clamp(DirectorReplayTimeSearch.UpperBound(samples, time, static s => s.TimeSeconds), 1, samples.Count - 1);
        int low = high - 1;

        DirectorReplayActorSample left = samples[low];
        DirectorReplayActorSample right = samples[high];
        double duration = right.TimeSeconds - left.TimeSeconds;
        if (!interpolate)
        {
            position = PositionOf(left);
            return true;
        }
        if (left.Dimension != right.Dimension || duration <= TimeEpsilon)
        {
            position = PositionOf(
                time - left.TimeSeconds <= right.TimeSeconds - time ? left : right);
            return true;
        }

        double amount = Math.Clamp((time - left.TimeSeconds) / duration, 0d, 1d);
        position = new DirectorReplayActorPosition(
            Lerp(left.X, right.X, amount),
            Lerp(left.Y, right.Y, amount),
            Lerp(left.Z, right.Z, amount),
            left.Dimension);
        return true;
    }

    public static bool TrySample(
        DirectorReplayActorTrack? actor,
        double timeSeconds,
        DirectorReplayActorSample sample)
        => TrySample(actor, timeSeconds, sample, interpolate: true);

    public static bool TrySample(
        DirectorReplayActorTrack? actor,
        double timeSeconds,
        DirectorReplayActorSample sample,
        bool interpolate)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (actor?.Samples is not { Count: > 0 } samples)
        {
            return false;
        }

        double time = double.IsFinite(timeSeconds) ? Math.Max(0d, timeSeconds) : 0d;
        if (DirectorReplayTimeSearch.LowerBound(samples, time, static s => s.TimeSeconds) == 0)
        {
            CopySample(samples[0], sample);
            if (!sample.HasMotion && samples.Count > 1)
            {
                ApplyDerivedMotion(sample, samples[0], samples[1]);
            }
            return true;
        }

        if (DirectorReplayTimeSearch.UpperBound(samples, time, static s => s.TimeSeconds) == samples.Count)
        {
            CopySample(samples[^1], sample);
            if (!sample.HasMotion && samples.Count > 1)
            {
                ApplyDerivedMotion(sample, samples[^2], samples[^1]);
            }
            return true;
        }

        int high = Math.Clamp(DirectorReplayTimeSearch.UpperBound(samples, time, static s => s.TimeSeconds), 1, samples.Count - 1);
        int low = high - 1;

        DirectorReplayActorSample left = samples[low];
        DirectorReplayActorSample right = samples[high];
        if (!interpolate)
        {
            CopySample(left, sample);
            if (!sample.HasMotion)
            {
                ApplyDerivedMotion(sample, left, right);
            }
            return true;
        }
        if (left.Dimension != right.Dimension
            || right.TimeSeconds - left.TimeSeconds <= TimeEpsilon)
        {
            CopySample(time - left.TimeSeconds <= right.TimeSeconds - time ? left : right, sample);
            return true;
        }

        double duration = right.TimeSeconds - left.TimeSeconds;
        double amount = Math.Clamp((time - left.TimeSeconds) / duration, 0d, 1d);
        sample.TimeSeconds = time;
        sample.X = Lerp(left.X, right.X, amount);
        sample.Y = Lerp(left.Y, right.Y, amount);
        sample.Z = Lerp(left.Z, right.Z, amount);
        sample.Dimension = left.Dimension;
        sample.Yaw = LerpAngleRadians(left.Yaw, right.Yaw, amount);
        bool isPlayerTrack = left.HasPlayerState
            && right.HasPlayerState
            || !string.IsNullOrWhiteSpace(actor.PlayerUid)
            || IsPlayerEntityCode(actor.EntityCode);
        sample.Pitch = isPlayerTrack
            ? LerpPlayerPitchRadians(left.Pitch, right.Pitch, amount)
            : LerpAngleRadians(left.Pitch, right.Pitch, amount);
        sample.Roll = LerpAngleRadians(left.Roll, right.Roll, amount);
        sample.HeadYaw = LerpAngleRadians(left.HeadYaw, right.HeadYaw, amount);
        sample.HeadPitch = LerpAngleRadians(left.HeadPitch, right.HeadPitch, amount);
        sample.BodyYaw = LerpAngleRadians(left.BodyYaw, right.BodyYaw, amount);
        sample.HasMotion = true;
        sample.MotionX = left.HasMotion && right.HasMotion
            ? Lerp(left.MotionX, right.MotionX, amount)
            : (right.X - left.X) / duration;
        sample.MotionY = left.HasMotion && right.HasMotion
            ? Lerp(left.MotionY, right.MotionY, amount)
            : (right.Y - left.Y) / duration;
        sample.MotionZ = left.HasMotion && right.HasMotion
            ? Lerp(left.MotionZ, right.MotionZ, amount)
            : (right.Z - left.Z) / duration;
        InterpolatePlaybackState(left, right, amount, sample);
        CopyInterpolatedAnimations(left, right, amount, sample);
        return true;
    }

    public static float LerpAngleRadians(float from, float to, double amount)
    {
        double delta = NormalizeRadians(to - from);
        return (float)NormalizeRadians(from + delta * Math.Clamp(amount, 0d, 1d));
    }

    private static float LerpPlayerPitchRadians(float from, float to, double amount)
    {
        if (!IsNativePlayerPitch(from) || !IsNativePlayerPitch(to))
        {
            return LerpAngleRadians(from, to, amount);
        }

        double delta = to - from;
        if (delta > Math.PI)
        {
            delta -= Math.PI * 2d;
        }
        else if (delta < -Math.PI)
        {
            delta += Math.PI * 2d;
        }

        double value = from + delta * Math.Clamp(amount, 0d, 1d);
        double normalized = value % (Math.PI * 2d);
        if (normalized < Math.PI / 2d)
        {
            normalized += Math.PI * 2d;
        }
        if (normalized > Math.PI * 3d / 2d)
        {
            normalized -= Math.PI * 2d;
        }
        return (float)normalized;
    }

    private static bool IsNativePlayerPitch(float pitch)
    {
        if (!float.IsFinite(pitch))
        {
            return false;
        }

        double relative = Math.PI - pitch;
        return Math.Abs(relative) <= Math.PI / 2d + 0.01d;
    }

    private static bool IsPlayerEntityCode(string? entityCode)
    {
        string normalized = entityCode?.Trim() ?? string.Empty;
        return normalized.Equals("player", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(":player", StringComparison.OrdinalIgnoreCase);
    }

    public static double NormalizeSampleRate(double sampleRate)
        => double.IsFinite(sampleRate) ? Math.Clamp(sampleRate, 1d, 60d) : 30d;

    public static bool TrySampleClock(
        IReadOnlyList<DirectorReplayClockSample>? samples,
        double timeSeconds,
        out DirectorReplayClockSample sample)
    {
        sample = new DirectorReplayClockSample();
        if (samples is not { Count: > 0 })
        {
            return false;
        }

        double time = NormalizeNonNegative(timeSeconds);
        if (DirectorReplayTimeSearch.LowerBound(samples, time, static s => s.TimeSeconds) == 0)
        {
            sample = samples[0].DeepClone();
            return true;
        }

        if (DirectorReplayTimeSearch.UpperBound(samples, time, static s => s.TimeSeconds) == samples.Count)
        {
            sample = samples[^1].DeepClone();
            return true;
        }

        int high = Math.Clamp(DirectorReplayTimeSearch.UpperBound(samples, time, static s => s.TimeSeconds), 1, samples.Count - 1);
        int low = high - 1;

        DirectorReplayClockSample left = samples[low];
        DirectorReplayClockSample right = samples[high];
        double duration = right.TimeSeconds - left.TimeSeconds;
        double amount = duration <= TimeEpsilon ? 0d : (time - left.TimeSeconds) / duration;
        sample = new DirectorReplayClockSample
        {
            TimeSeconds = time,
            TotalHours = Lerp(left.TotalHours, right.TotalHours, amount),
            CalendarSpeedMul = (float)Lerp(left.CalendarSpeedMul, right.CalendarSpeedMul, amount),
            SeasonOverride = amount < 0.5d ? left.SeasonOverride : right.SeasonOverride
        };
        return true;
    }

    private static DirectorReplayActorTrack NormalizeActor(
        DirectorReplayActorTrack source,
        HashSet<string> usedKeys,
        int actorIndex,
        bool repairLegacyThirdPersonPose, CancellationToken cancellationToken)
    {
        string baseKey = string.IsNullOrWhiteSpace(source.ActorKey)
            ? !string.IsNullOrWhiteSpace(source.PlayerUid)
                ? "player:" + source.PlayerUid.Trim()
                : "entity:" + Math.Max(0L, source.EntityId)
            : source.ActorKey.Trim();
        string key = baseKey;
        for (int suffix = 2; !usedKeys.Add(key); suffix++)
        {
            key = $"{baseKey}#{suffix}";
        }

        DirectorReplayActorTrack result = new()
        {
            ActorKey = key,
            EntityId = Math.Max(0L, source.EntityId),
            PlayerUid = source.PlayerUid?.Trim() ?? string.Empty,
            ForceGhost = source.ForceGhost,
            EntityCode = source.EntityCode?.Trim() ?? string.Empty,
            DisplayName = source.DisplayName?.Trim() ?? string.Empty,
            ReplayEntityId = source.ReplayEntityId < 0
                ? source.ReplayEntityId
                : long.MinValue + actorIndex + 1L,
            SimulationRange = Math.Clamp(source.SimulationRange, 0, 4096),
            EquipmentSlotCapacity = Math.Max(0, source.EquipmentSlotCapacity),
            BackpackSlotCapacity = Math.Max(0, source.BackpackSlotCapacity),
            InitialState = CloneBytes(source.InitialState)
        };
        if (source.Samples is null)
        {
            return result;
        }

        bool isPlayer = !string.IsNullOrWhiteSpace(source.PlayerUid)
            || string.Equals(source.EntityCode, "player", StringComparison.OrdinalIgnoreCase);
        double maximumSampleTime = double.NegativeInfinity;
        foreach (DirectorReplayActorSample? sourceSample in WithCancellation(source.Samples, cancellationToken))
        {
            if (sourceSample is null)
            {
                continue;
            }

            DirectorReplayActorSample normalized = NormalizeSample(sourceSample);
            if (isPlayer)
            {
                normalized.Animations = NormalizePlayerAnimations(normalized.Animations);
                normalized.AnimationStates = normalized.AnimationStates
                    .Select(state =>
                    {
                        state.Code = NormalizePlayerAnimationCode(state.Code);
                        state.StateCode = NormalizePlayerAnimationCode(state.StateCode);
                        return state;
                    })
                    .Where(state => state.Code.Length > 0)
                    .GroupBy(
                        state => state.Code + "\n" + NormalizeAnimationCode(state.StateCode, state.Code),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Last())
                    .OrderBy(state => state.Code, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(state => state.StateCode, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            // Recorded samples arrive chronologically. Avoid scanning every earlier sample
            // on each append (quadratic for long volumes), retaining legacy unordered semantics.
            int existing = normalized.TimeSeconds > maximumSampleTime + TimeEpsilon ? -1
                : result.Samples.FindIndex(candidate => Math.Abs(candidate.TimeSeconds - normalized.TimeSeconds) <= TimeEpsilon);
            if (existing >= 0)
            {
                result.Samples[existing] = normalized;
            }
            else
            {
                result.Samples.Add(normalized);
            }
            maximumSampleTime = Math.Max(maximumSampleTime, normalized.TimeSeconds);
        }

        result.Samples.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        RepairAnimationCaptureGaps(result.Samples);
        if (isPlayer)
        {
            EnsureThirdPersonPoses(result.Samples, repairLegacyThirdPersonPose);
        }
        for (int index = 1; index < result.Samples.Count; index++)
        {
            DirectorReplayActorSample previous = result.Samples[index - 1];
            DirectorReplayActorSample current = result.Samples[index];
            if (!current.HasPlayerState)
            {
                continue;
            }
            if (current.RightHandItem.AsSpan().SequenceEqual(previous.RightHandItem))
            {
                current.RightHandItem = previous.RightHandItem;
            }
            if (current.LeftHandItem.AsSpan().SequenceEqual(previous.LeftHandItem))
            {
                current.LeftHandItem = previous.LeftHandItem;
            }
        }
        if (source.Lifetimes is not null)
        {
            foreach (DirectorReplayActorLifetime? lifetime in source.Lifetimes)
            {
                if (lifetime is null)
                {
                    continue;
                }

                double start = NormalizeNonNegative(lifetime.StartSeconds);
                double end = Math.Max(start, NormalizeNonNegative(lifetime.EndSeconds));
                if (result.Lifetimes.Count > 0 && start <= result.Lifetimes[^1].EndSeconds + TimeEpsilon)
                {
                    DirectorReplayActorLifetime existing = result.Lifetimes[^1];
                    if (end >= existing.EndSeconds - TimeEpsilon)
                    {
                        existing.EndSeconds = Math.Max(existing.EndSeconds, end);
                        existing.EndedByDespawn = lifetime.EndedByDespawn;
                    }
                }
                else
                {
                    result.Lifetimes.Add(new DirectorReplayActorLifetime
                    {
                        StartSeconds = start,
                        EndSeconds = end,
                        EndedByDespawn = lifetime.EndedByDespawn
                    });
                }
            }
        }

        if (source.States is not null)
        {
            foreach (DirectorReplayActorState? state in source.States
                .Where(item => item is not null)
                .OrderBy(item => item!.TimeSeconds))
            {
                byte[] data = CloneBytes(state!.Data);
                if (data.Length == 0)
                {
                    continue;
                }

                double time = NormalizeNonNegative(state.TimeSeconds);
                if (result.States.Count > 0
                    && Math.Abs(result.States[^1].TimeSeconds - time) <= TimeEpsilon)
                {
                    result.States[^1] = new DirectorReplayActorState { TimeSeconds = time, Data = data };
                }
                else
                {
                    result.States.Add(new DirectorReplayActorState { TimeSeconds = time, Data = data });
                }
            }
        }

        return result;
    }

    private static void RepairAnimationCaptureGaps(DirectorReplayList<DirectorReplayActorSample> samples)
    {
        const int maximumGapSamples = 4;
        const double maximumGapSeconds = 0.5d;
        int index = 0;
        while (index < samples.Count)
        {
            if (samples[index].AnimationStates.Count > 0)
            {
                index++;
                continue;
            }

            int gapStart = index;
            while (index < samples.Count && samples[index].AnimationStates.Count == 0)
            {
                index++;
            }
            int gapEnd = index;
            int gapLength = gapEnd - gapStart;
            if (gapLength > maximumGapSamples)
            {
                continue;
            }

            int previousIndex = gapStart - 1;
            int nextIndex = gapEnd;
            if (previousIndex >= 0
                && nextIndex < samples.Count
                && samples[nextIndex].TimeSeconds - samples[previousIndex].TimeSeconds
                    <= maximumGapSeconds)
            {
                DirectorReplayActorSample previous = samples[previousIndex];
                DirectorReplayActorSample next = samples[nextIndex];
                double duration = Math.Max(
                    TimeEpsilon,
                    next.TimeSeconds - previous.TimeSeconds);
                for (int gapIndex = gapStart; gapIndex < gapEnd; gapIndex++)
                {
                    DirectorReplayActorSample sample = samples[gapIndex];
                    double amount = Math.Clamp(
                        (sample.TimeSeconds - previous.TimeSeconds) / duration,
                        0d,
                        1d);
                    CopyInterpolatedAnimations(previous, next, amount, sample);
                }
                continue;
            }

            if (previousIndex < 0
                && nextIndex < samples.Count
                && samples[nextIndex].TimeSeconds - samples[gapStart].TimeSeconds
                    <= maximumGapSeconds)
            {
                for (int gapIndex = gapStart; gapIndex < gapEnd; gapIndex++)
                {
                    CopyAnimationStates(
                        samples[nextIndex].AnimationStates,
                        samples[gapIndex].AnimationStates);
                    samples[gapIndex].Animations.Clear();
                    samples[gapIndex].Animations.AddRange(samples[nextIndex].Animations);
                }
                continue;
            }

            if (nextIndex >= samples.Count
                && previousIndex >= 0
                && samples[^1].TimeSeconds - samples[previousIndex].TimeSeconds
                    <= maximumGapSeconds)
            {
                for (int gapIndex = gapStart; gapIndex < gapEnd; gapIndex++)
                {
                    CopyAnimationStates(
                        samples[previousIndex].AnimationStates,
                        samples[gapIndex].AnimationStates);
                    samples[gapIndex].Animations.Clear();
                    samples[gapIndex].Animations.AddRange(samples[previousIndex].Animations);
                }
            }
        }
    }

    private static void EnsureThirdPersonPoses(
        DirectorReplayList<DirectorReplayActorSample> samples,
        bool repairLegacyThirdPersonPose)
    {
        if (samples.Count == 0)
        {
            return;
        }

        bool hasExplicitPose = samples.Any(sample => sample.HasThirdPersonPose);
        if (repairLegacyThirdPersonPose
            && hasExplicitPose
            && LooksLikeFlattenedFirstPersonPose(samples))
        {
            // Schema 15 could mark an entire first-person track as explicit
            // third person when a single transient frame crossed the old
            // separation threshold. Rebuild those tracks from the recorded
            // camera yaw and controls instead of preserving the flattened
            // body/head pose.
            foreach (DirectorReplayActorSample sample in samples)
            {
                sample.HasThirdPersonPose = false;
            }
            hasExplicitPose = false;
        }

        bool rawPoseAlreadyThirdPerson = repairLegacyThirdPersonPose
            && !hasExplicitPose
            && HasSustainedThirdPersonPose(samples, useExplicitPose: false);
        if (rawPoseAlreadyThirdPerson)
        {
            foreach (DirectorReplayActorSample sample in samples)
            {
                sample.HasThirdPersonPose = true;
                sample.ThirdPersonBodyYaw = sample.BodyYaw;
                sample.ThirdPersonHeadYaw = sample.HeadYaw;
                sample.ThirdPersonHeadPitch = sample.HeadPitch;
            }
            return;
        }

        ThirdPersonPoseState state = default;
        double previousTime = samples[0].TimeSeconds;
        foreach (DirectorReplayActorSample sample in samples)
        {
            if (sample.HasThirdPersonPose)
            {
                state.BodyYaw = sample.ThirdPersonBodyYaw;
                state.HeadYaw = sample.ThirdPersonHeadYaw;
                state.HeadPitch = sample.ThirdPersonHeadPitch;
                state.Initialized = true;
                state.TurnOpposite = false;
                state.RotateBodyNow = false;
                previousTime = sample.TimeSeconds;
                continue;
            }

            if (!state.Initialized)
            {
                state.BodyYaw = sample.HasPlayerState ? sample.WalkYaw : sample.BodyYaw;
                state.HeadYaw = sample.HeadYaw;
                state.HeadPitch = sample.HeadPitch;
                state.Initialized = true;
            }
            else
            {
                double remaining = Math.Clamp(sample.TimeSeconds - previousTime, 0d, 1d);
                while (remaining > TimeEpsilon)
                {
                    float step = (float)Math.Min(remaining, 1d / 60d);
                    AdvanceThirdPersonPose(ref state, sample, step);
                    remaining -= step;
                }
            }

            sample.HasThirdPersonPose = true;
            sample.ThirdPersonBodyYaw = state.BodyYaw;
            sample.ThirdPersonHeadYaw = state.HeadYaw;
            sample.ThirdPersonHeadPitch = state.HeadPitch;
            previousTime = sample.TimeSeconds;
        }
    }

    private static bool LooksLikeFlattenedFirstPersonPose(
        IReadOnlyList<DirectorReplayActorSample> samples)
    {
        if (samples.Count < 3
            || HasSustainedThirdPersonPose(samples, useExplicitPose: true))
        {
            return false;
        }

        float firstYaw = samples[0].Yaw;
        for (int index = 1; index < samples.Count; index++)
        {
            if (Math.Abs(NormalizeRadians(samples[index].Yaw - firstYaw)) >= 0.15f)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasSustainedThirdPersonPose(
        IReadOnlyList<DirectorReplayActorSample> samples,
        bool useExplicitPose)
    {
        const float recordedThirdPersonThreshold = 0.15f;
        if (samples.Count < 3)
        {
            return samples.Any(sample => PoseSeparation(sample, useExplicitPose)
                is { Body: true } or { Head: true });
        }

        PoseSeparationDuration body = MeasurePoseSeparation(
            samples,
            sample => PoseSeparation(sample, useExplicitPose).Body);
        PoseSeparationDuration head = MeasurePoseSeparation(
            samples,
            sample => PoseSeparation(sample, useExplicitPose).Head);
        PoseSeparationDuration both = MeasurePoseSeparation(
            samples,
            sample => PoseSeparation(sample, useExplicitPose) is { Body: true, Head: true });
        double trackDuration = Math.Max(body.TrackSeconds, TimeEpsilon);
        return head.LongestSeconds >= 0.15d
            || body.LongestSeconds >= 0.25d
            || both.LongestSeconds >= 0.08d
            || (head.TotalSeconds >= 0.2d && head.TotalSeconds / trackDuration >= 0.1d)
            || (body.TotalSeconds >= 0.2d && body.TotalSeconds / trackDuration >= 0.1d);

        static (bool Body, bool Head) PoseSeparation(
            DirectorReplayActorSample sample,
            bool explicitPose)
        {
            float bodyYaw = explicitPose
                ? sample.ThirdPersonBodyYaw
                : sample.BodyYaw;
            float headYaw = explicitPose
                ? sample.ThirdPersonHeadYaw
                : sample.HeadYaw;
            return (
                Math.Abs(NormalizeRadians(bodyYaw - sample.Yaw))
                    >= recordedThirdPersonThreshold,
                Math.Abs(headYaw) >= recordedThirdPersonThreshold);
        }
    }

    private static PoseSeparationDuration MeasurePoseSeparation(
        IReadOnlyList<DirectorReplayActorSample> samples,
        Func<DirectorReplayActorSample, bool> separated)
    {
        double trackSeconds = 0d;
        double totalSeconds = 0d;
        double longestSeconds = 0d;
        double currentSeconds = 0d;
        for (int index = 0; index + 1 < samples.Count; index++)
        {
            double interval = Math.Clamp(
                samples[index + 1].TimeSeconds - samples[index].TimeSeconds,
                0d,
                1d);
            trackSeconds += interval;
            if (interval <= TimeEpsilon
                || !separated(samples[index])
                || !separated(samples[index + 1]))
            {
                currentSeconds = 0d;
                continue;
            }

            totalSeconds += interval;
            currentSeconds += interval;
            longestSeconds = Math.Max(longestSeconds, currentSeconds);
        }
        return new PoseSeparationDuration(trackSeconds, totalSeconds, longestSeconds);
    }

    private readonly record struct PoseSeparationDuration(
        double TrackSeconds,
        double TotalSeconds,
        double LongestSeconds);

    private static void AdvanceThirdPersonPose(
        ref ThirdPersonPoseState state,
        DirectorReplayActorSample sample,
        float deltaSeconds)
    {
        const float degreesToRadians = MathF.PI / 180f;
        const float headYawLimit = 43f * degreesToRadians;
        const float headPitchLimit = 69f * degreesToRadians;
        const float bodyFollowThreshold = 69f * degreesToRadians;

        float targetYaw = sample.Yaw;
        float bodyDistance = (float)NormalizeRadians(targetYaw - state.BodyYaw);
        if (Math.Abs(bodyDistance) > 108f * degreesToRadians)
        {
            state.TurnOpposite = true;
        }
        if (state.TurnOpposite)
        {
            if (Math.Abs(bodyDistance) < 81f * degreesToRadians)
            {
                state.TurnOpposite = false;
            }
            else
            {
                bodyDistance = 0f;
            }
        }

        state.HeadYaw += (bodyDistance - state.HeadYaw) * deltaSeconds * 6f;
        state.HeadYaw = Math.Clamp(state.HeadYaw, -headYawLimit, headYawLimit);
        if (Math.Abs(sample.Pitch) > MathF.PI / 2f)
        {
            state.HeadPitch = Math.Clamp(
                (sample.Pitch - MathF.PI) * 0.75f,
                -headPitchLimit,
                headPitchLimit);
        }
        else
        {
            state.HeadPitch = sample.HeadPitch;
        }

        int controls = sample.Controls | sample.ServerControls;
        bool triesToMove = (controls & 0x0f) != 0;
        bool gliding = (controls & 0x200) != 0;
        float threshold = triesToMove ? 0.01f : bodyFollowThreshold;
        if (gliding)
        {
            threshold = 0f;
        }

        float targetDistance = (float)NormalizeRadians(targetYaw - state.BodyYaw);
        if (Math.Abs(targetDistance) > threshold || state.RotateBodyNow)
        {
            float followSpeed = 0.05f + Math.Abs(targetDistance) * 3.5f;
            state.BodyYaw = (float)NormalizeRadians(
                state.BodyYaw + Math.Clamp(
                    targetDistance,
                    -deltaSeconds * followSpeed,
                    deltaSeconds * followSpeed));
            state.RotateBodyNow = Math.Abs(targetDistance) > 0.01f;
        }
    }

    private struct ThirdPersonPoseState
    {
        internal float BodyYaw;

        internal float HeadYaw;

        internal float HeadPitch;

        internal bool Initialized;

        internal bool TurnOpposite;

        internal bool RotateBodyNow;
    }

    public static List<string> NormalizePlayerAnimations(IEnumerable<string>? animations)
    {
        if (animations is null)
        {
            return new List<string>();
        }

        return animations
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(NormalizePlayerAnimationCode)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizePlayerAnimationCode(string? animation)
    {
        string code = animation?.Trim() ?? string.Empty;
        if (code.EndsWith("-ifp", StringComparison.OrdinalIgnoreCase))
        {
            return code[..^4];
        }
        return code.EndsWith("-fp", StringComparison.OrdinalIgnoreCase) ? code[..^3] : code;
    }

    public static bool IsActorVisible(DirectorReplayActorTrack actor, double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(actor);
        double time = NormalizeNonNegative(timeSeconds);
        if (actor.Lifetimes.Count == 0)
        {
            return actor.Samples.Count > 0
                && DirectorReplayTimeSearch.UpperBound(actor.Samples, time + TimeEpsilon, static s => s.TimeSeconds) > 0
                && DirectorReplayTimeSearch.LowerBound(actor.Samples, time - TimeEpsilon, static s => s.TimeSeconds) < actor.Samples.Count;
        }

        foreach (DirectorReplayActorLifetime lifetime in actor.Lifetimes)
        {
            if (time >= lifetime.StartSeconds - TimeEpsilon
                && (lifetime.EndedByDespawn
                    ? time < lifetime.EndSeconds - TimeEpsilon
                    : time <= lifetime.EndSeconds + TimeEpsilon))
            {
                return true;
            }
        }
        return false;
    }

    public static DirectorReplayActorState? FindActorState(
        DirectorReplayActorTrack actor,
        double timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.States.Count == 0)
        {
            return null;
        }

        double time = NormalizeNonNegative(timeSeconds);
        int index = DirectorReplayTimeSearch.UpperBound(actor.States, time + TimeEpsilon, static s => s.TimeSeconds) - 1;
        return index >= 0 ? actor.States[index] : null;
    }

    private static DirectorReplayWorldSnapshot NormalizeWorldSnapshot(DirectorReplayWorldSnapshot? source, CancellationToken cancellationToken)
    {
        DirectorReplayWorldSnapshot result = new()
        {
            Dimension = source?.Dimension ?? 0,
            CenterChunkX = source?.CenterChunkX ?? 0,
            CenterChunkZ = source?.CenterChunkZ ?? 0,
            RadiusChunks = Math.Clamp(source?.RadiusChunks ?? 0, 0, 64),
            MapSizeY = Math.Clamp(source?.MapSizeY ?? 0, 0, 16384)
        };
        HashSet<(int X, int Y, int Z)> chunks = new();
        if (source?.Chunks is not null)
        {
            foreach (DirectorReplayChunkSnapshot? chunk in WithCancellation(source.Chunks, cancellationToken))
            {
                if (chunk is null || !chunks.Add((chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ)))
                {
                    continue;
                }

                result.Chunks.Add(new DirectorReplayChunkSnapshot
                {
                    ChunkHash = chunk.ChunkHash ?? string.Empty,
                    ChunkVersion = Math.Max(0, chunk.ChunkVersion),
                    CapturedAtSeconds = Math.Max(0, NormalizeFinite(chunk.CapturedAtSeconds)),
                    ChunkX = chunk.ChunkX,
                    ChunkY = Math.Clamp(chunk.ChunkY, 0, 1023),
                    ChunkZ = chunk.ChunkZ,
                    SolidRuns = DirectorReplayChunkCodec.Normalize(chunk.SolidRuns, ChunkBlockCount),
                    FluidRuns = DirectorReplayChunkCodec.Normalize(chunk.FluidRuns, ChunkBlockCount)
                });
            }
        }

        if (source?.BlockEntities is not null)
        {
            result.BlockEntities = WithCancellation(source.BlockEntities, cancellationToken)
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.ClassName))
                .Select(item => new DirectorReplayBlockEntitySnapshot
                {
                    X = item!.X,
                    Y = item.Y,
                    Z = item.Z,
                    Dimension = item.Dimension,
                    ClassName = item.ClassName.Trim(),
                    Data = CloneBytes(item.Data)
                })
                .OrderBy(item => item.Dimension)
                .ThenBy(item => item.Y)
                .ThenBy(item => item.Z)
                .ThenBy(item => item.X)
                .ToList();
        }

        result.Chunks.Sort((left, right) =>
        {
            int y = left.ChunkY.CompareTo(right.ChunkY);
            int z = left.ChunkZ.CompareTo(right.ChunkZ);
            return y != 0 ? y : z != 0 ? z : left.ChunkX.CompareTo(right.ChunkX);
        });
        return result;
    }

    private static DirectorReplayBlockEvent NormalizeBlockEvent(DirectorReplayBlockEvent source)
        => new()
        {
            TimeSeconds = NormalizeNonNegative(source.TimeSeconds),
            X = source.X,
            Y = source.Y,
            Z = source.Z,
            Dimension = source.Dimension,
            SolidBlockId = Math.Max(0, source.SolidBlockId),
            FluidBlockId = Math.Max(0, source.FluidBlockId),
            BlockEntityCaptured = source.BlockEntityCaptured,
            BlockEntityClass = source.BlockEntityClass?.Trim() ?? string.Empty,
            BlockEntityData = CloneBytes(source.BlockEntityData)
        };

    private static DirectorReplayParticleEvent NormalizeParticleEvent(DirectorReplayParticleEvent source)
        => new()
        {
            TimeSeconds = NormalizeNonNegative(source.TimeSeconds),
            Kind = Enum.IsDefined(source.Kind) ? source.Kind : DirectorReplayParticleKind.Provider,
            IsWeatherParticle = source.IsWeatherParticle,
            ProviderType = source.ProviderType?.Trim() ?? string.Empty,
            ProviderData = CloneBytes(source.ProviderData),
            Quantity = Math.Max(0f, NormalizeFinite(source.Quantity)),
            Color = source.Color,
            MinX = NormalizeFinite(source.MinX),
            MinY = NormalizeFinite(source.MinY),
            MinZ = NormalizeFinite(source.MinZ),
            MaxX = NormalizeFinite(source.MaxX),
            MaxY = NormalizeFinite(source.MaxY),
            MaxZ = NormalizeFinite(source.MaxZ),
            MinVelocityX = NormalizeFinite(source.MinVelocityX),
            MinVelocityY = NormalizeFinite(source.MinVelocityY),
            MinVelocityZ = NormalizeFinite(source.MinVelocityZ),
            MaxVelocityX = NormalizeFinite(source.MaxVelocityX),
            MaxVelocityY = NormalizeFinite(source.MaxVelocityY),
            MaxVelocityZ = NormalizeFinite(source.MaxVelocityZ),
            LifeLength = Math.Max(0f, NormalizeFinite(source.LifeLength)),
            GravityEffect = NormalizeFinite(source.GravityEffect),
            Scale = Math.Max(0f, NormalizeFinite(source.Scale)),
            ParticleModel = source.ParticleModel
        };

    private static DirectorReplaySoundEvent NormalizeSoundEvent(
        DirectorReplaySoundEvent source,
        bool inferLegacyRelativeSounds)
        => new()
        {
            TimeSeconds = NormalizeNonNegative(source.TimeSeconds),
            Location = source.Location?.Trim() ?? string.Empty,
            InstanceId = Math.Max(0L, source.InstanceId),
            Action = Enum.IsDefined(source.Action) ? source.Action : DirectorReplaySoundAction.Play,
            X = NormalizeFinite(source.X),
            Y = NormalizeFinite(source.Y),
            Z = NormalizeFinite(source.Z),
            RelativePosition = source.RelativePosition
                || inferLegacyRelativeSounds
                    && source.X == 0d
                    && source.Y == 0d
                    && source.Z == 0d,
            ShouldLoop = source.ShouldLoop,
            PlaybackPosition = Math.Max(0f, NormalizeFinite(source.PlaybackPosition)),
            SoundLengthSeconds = Math.Max(0f, NormalizeFinite(source.SoundLengthSeconds)),
            Volume = Math.Clamp(NormalizeFinite(source.Volume), 0f, 1f),
            Pitch = Math.Clamp(NormalizeFinite(source.Pitch), 0.01f, 8f),
            PitchOffset = Math.Clamp(NormalizeFinite(source.PitchOffset), -8f, 8f),
            Range = Math.Clamp(NormalizeFinite(source.Range), 0f, 4096f),
            ReferenceDistance = Math.Clamp(NormalizeFinite(source.ReferenceDistance), 0f, 4096f),
            LowPassFilter = Math.Clamp(NormalizeFinite(source.LowPassFilter), 0f, 1f),
            ReverbDecayTime = Math.Clamp(NormalizeFinite(source.ReverbDecayTime), 0f, 99f),
            SoundType = source.SoundType
        };

    private static DirectorReplayActorSample NormalizeSample(DirectorReplayActorSample source)
    {
        HashSet<string> seenAnimations = new(StringComparer.OrdinalIgnoreCase);
        List<string> animations = new();
        if (source.Animations is not null)
        {
            foreach (string? animation in source.Animations)
            {
                if (!string.IsNullOrWhiteSpace(animation) && seenAnimations.Add(animation.Trim()))
                {
                    animations.Add(animation.Trim());
                }
            }
        }

        List<DirectorReplayAnimationSample> animationStates = new();
        HashSet<string> seenAnimationStates = new(StringComparer.OrdinalIgnoreCase);
        if (source.AnimationStates is not null)
        {
            foreach (DirectorReplayAnimationSample? state in source.AnimationStates)
            {
                string code = state?.Code?.Trim() ?? string.Empty;
                string stateCode = NormalizeAnimationCode(state?.StateCode, code);
                if (code.Length == 0
                    || !seenAnimationStates.Add(code + "\n" + stateCode))
                {
                    continue;
                }

                animationStates.Add(new DirectorReplayAnimationSample
                {
                    Code = code.Length <= 128 ? code : code[..128],
                    StateCode = stateCode,
                    Progress = Math.Clamp(NormalizeFinite(state!.Progress), 0f, 1f),
                    RecordedFrame = state.RecordedFrame is float frame && float.IsFinite(frame) ? Math.Max(0, frame) : null,
                    AnimationSpeed = Math.Clamp(NormalizeFinite(state.AnimationSpeed), -64f, 64f),
                    EasingFactor = Math.Clamp(NormalizeFinite(state.EasingFactor), 0f, 1f),
                    BlendedWeight = Math.Clamp(NormalizeFinite(state.BlendedWeight), 0f, 64f),
                    Iterations = Math.Clamp(state.Iterations, -1_000_000, 1_000_000),
                    IsActive = state.IsActive,
                    IsRunning = state.IsRunning,
                    NowPlaying = state.NowPlaying,
                    ShouldRewind = state.ShouldRewind,
                    ShouldPlayTillEnd = state.ShouldPlayTillEnd,
                    Weight = Math.Clamp(NormalizeFinite(state.Weight), 0f, 1000f),
                    EaseInSpeed = Math.Clamp(NormalizeFinite(state.EaseInSpeed), 0f, 1000f),
                    EaseOutSpeed = Math.Clamp(NormalizeFinite(state.EaseOutSpeed), 0f, 1000f),
                    BlendMode = Math.Clamp(state.BlendMode, 0, 16),
                    ElementWeights = NormalizeElementWeights(state.ElementWeights),
                    ElementBlendModes = NormalizeElementBlendModes(state.ElementBlendModes)
                });
            }
        }
        if (animations.Count == 0 && animationStates.Count > 0)
        {
            animations.AddRange(animationStates.Select(state => state.Code));
        }

        return new DirectorReplayActorSample
        {
            TimeSeconds = NormalizeNonNegative(source.TimeSeconds),
            X = NormalizeFinite(source.X),
            Y = NormalizeFinite(source.Y),
            Z = NormalizeFinite(source.Z),
            Dimension = source.Dimension,
            Yaw = NormalizeFinite(source.Yaw),
            Pitch = NormalizeFinite(source.Pitch),
            Roll = NormalizeFinite(source.Roll),
            HeadYaw = NormalizeFinite(source.HeadYaw),
            HeadPitch = NormalizeFinite(source.HeadPitch),
            BodyYaw = NormalizeFinite(source.BodyYaw),
            HasThirdPersonPose = source.HasThirdPersonPose,
            ThirdPersonBodyYaw = NormalizeFinite(source.ThirdPersonBodyYaw),
            ThirdPersonHeadYaw = NormalizeFinite(source.ThirdPersonHeadYaw),
            ThirdPersonHeadPitch = NormalizeFinite(source.ThirdPersonHeadPitch),
            HasMotion = source.HasMotion,
            MotionX = NormalizeFinite(source.MotionX),
            MotionY = NormalizeFinite(source.MotionY),
            MotionZ = NormalizeFinite(source.MotionZ),
            HasRenderState = source.HasRenderState,
            OnGround = source.OnGround,
            FeetInLiquid = source.FeetInLiquid,
            Swimming = source.Swimming,
            ClimbingOnFace = NormalizeFacingCode(source.ClimbingOnFace),
            LocalEyePosX = NormalizeFinite(source.LocalEyePosX),
            LocalEyePosY = NormalizeFinite(source.LocalEyePosY),
            LocalEyePosZ = NormalizeFinite(source.LocalEyePosZ),
            SelectionBoxY2 = Math.Clamp(NormalizeFinite(source.SelectionBoxY2), 0f, 64f),
            CollisionBoxY2 = Math.Clamp(NormalizeFinite(source.CollisionBoxY2), 0f, 64f),
            HasItemRenderState = source.HasItemRenderState,
            ItemRenderElapsedMilliseconds = Math.Max(0L, source.ItemRenderElapsedMilliseconds),
            ItemTouchGroundAgeMilliseconds = Math.Clamp(
                source.ItemTouchGroundAgeMilliseconds,
                0L,
                86_400_000L),
            ItemScaleRandom = Math.Clamp(NormalizeFinite(source.ItemScaleRandom), -1f, 1f),
            ItemYawRandom = NormalizeFinite(source.ItemYawRandom),
            ItemRenderXAngle = NormalizeFinite(source.ItemRenderXAngle),
            ItemRenderYAngle = NormalizeFinite(source.ItemRenderYAngle),
            ItemRenderZAngle = NormalizeFinite(source.ItemRenderZAngle),
            ItemRenderAccumulator = Math.Clamp(
                NormalizeFinite(source.ItemRenderAccumulator),
                0f,
                60f),
            HasAgentState = source.HasAgentState,
            BodyYawServer = NormalizeFinite(source.BodyYawServer),
            HasPlayerState = source.HasPlayerState,
            HasPlayerPostureState = source.HasPlayerPostureState,
            CanStandUp = source.HasPlayerPostureState ? source.CanStandUp : true,
            Controls = source.Controls,
            ServerControls = source.ServerControls,
            HandUse = source.HandUse,
            ServerHandUse = source.ServerHandUse,
            WalkYaw = NormalizeFinite(source.WalkYaw),
            WalkPitch = NormalizeFinite(source.WalkPitch),
            RightHandItem = CloneBytes(source.RightHandItem),
            LeftHandItem = CloneBytes(source.LeftHandItem),
            EquipmentItems = NormalizeItemList(source.EquipmentItems, 64),
            BackpackItems = NormalizeItemList(source.BackpackItems, 16),
            Animations = animations,
            AnimationStates = animationStates
        };
    }

    private static void CopySample(DirectorReplayActorSample source, DirectorReplayActorSample destination)
    {
        destination.TimeSeconds = source.TimeSeconds;
        destination.X = source.X;
        destination.Y = source.Y;
        destination.Z = source.Z;
        destination.Dimension = source.Dimension;
        destination.Yaw = source.Yaw;
        destination.Pitch = source.Pitch;
        destination.Roll = source.Roll;
        destination.HeadYaw = source.HeadYaw;
        destination.HeadPitch = source.HeadPitch;
        destination.BodyYaw = source.BodyYaw;
        destination.HasThirdPersonPose = source.HasThirdPersonPose;
        destination.ThirdPersonBodyYaw = source.ThirdPersonBodyYaw;
        destination.ThirdPersonHeadYaw = source.ThirdPersonHeadYaw;
        destination.ThirdPersonHeadPitch = source.ThirdPersonHeadPitch;
        destination.HasMotion = source.HasMotion;
        destination.MotionX = source.MotionX;
        destination.MotionY = source.MotionY;
        destination.MotionZ = source.MotionZ;
        CopyPlaybackState(source, destination);
        destination.Animations.Clear();
        destination.Animations.AddRange(source.Animations);
        CopyAnimationStates(source.AnimationStates, destination.AnimationStates);
    }

    private static DirectorReplayActorPosition PositionOf(DirectorReplayActorSample sample)
        => new(sample.X, sample.Y, sample.Z, sample.Dimension);

    private static void ApplyDerivedMotion(
        DirectorReplayActorSample destination,
        DirectorReplayActorSample left,
        DirectorReplayActorSample right)
    {
        double duration = right.TimeSeconds - left.TimeSeconds;
        if (left.Dimension != right.Dimension || duration <= TimeEpsilon)
        {
            return;
        }

        destination.HasMotion = true;
        destination.MotionX = (right.X - left.X) / duration;
        destination.MotionY = (right.Y - left.Y) / duration;
        destination.MotionZ = (right.Z - left.Z) / duration;
    }

    private static void CopyInterpolatedAnimations(
        DirectorReplayActorSample left,
        DirectorReplayActorSample right,
        double amount,
        DirectorReplayActorSample destination)
    {
        if (left.AnimationStates.Count == 0 && right.AnimationStates.Count == 0)
        {
            DirectorReplayActorSample selected = amount < 0.5d ? left : right;
            destination.Animations.Clear();
            destination.Animations.AddRange(selected.Animations);
            destination.AnimationStates.Clear();
            return;
        }

        bool canInterpolate = AnimationSetsMatch(left.AnimationStates, right.AnimationStates);
        int stateIndex = 0;
        if (canInterpolate)
        {
            foreach (DirectorReplayAnimationSample leftState in left.AnimationStates)
            {
                DirectorReplayAnimationSample rightState = FindAnimationState(
                    right.AnimationStates,
                    leftState)!;
                DirectorReplayAnimationSample state = GetAnimationState(
                    destination.AnimationStates,
                    stateIndex++);
                InterpolateAnimationState(leftState, rightState, amount, state);
            }
        }
        else
        {
            HashSet<int> matchedRight = new();
            float fadeOut = (float)(1d - amount);
            float fadeIn = (float)amount;
            foreach (DirectorReplayAnimationSample leftState in left.AnimationStates)
            {
                int rightIndex = FindExactAnimationStateIndex(right.AnimationStates, leftState);
                DirectorReplayAnimationSample state = GetAnimationState(
                    destination.AnimationStates,
                    stateIndex++);
                if (rightIndex >= 0)
                {
                    matchedRight.Add(rightIndex);
                    InterpolateAnimationState(
                        leftState,
                        right.AnimationStates[rightIndex],
                        amount,
                        state);
                }
                else
                {
                    CopyAnimationState(leftState, state);
                    state.EasingFactor *= fadeOut;
                    state.BlendedWeight *= fadeOut;
                }
            }

            for (int rightIndex = 0; rightIndex < right.AnimationStates.Count; rightIndex++)
            {
                if (matchedRight.Contains(rightIndex))
                {
                    continue;
                }
                DirectorReplayAnimationSample state = GetAnimationState(
                    destination.AnimationStates,
                    stateIndex++);
                CopyAnimationState(right.AnimationStates[rightIndex], state);
                state.EasingFactor *= fadeIn;
                state.BlendedWeight *= fadeIn;
            }
        }

        if (destination.AnimationStates.Count > stateIndex)
        {
            destination.AnimationStates.RemoveRange(
                stateIndex,
                destination.AnimationStates.Count - stateIndex);
        }
        destination.Animations.Clear();
        destination.Animations.AddRange(destination.AnimationStates
            .Select(state => state.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static void InterpolateAnimationState(
        DirectorReplayAnimationSample left,
        DirectorReplayAnimationSample right,
        double amount,
        DirectorReplayAnimationSample destination)
    {
        destination.Code = left.Code;
        destination.StateCode = amount < 0.5d ? left.StateCode : right.StateCode;
        destination.Progress = LerpAnimationProgress(left, right, amount);
        destination.RecordedFrame = left.RecordedFrame is float lf && right.RecordedFrame is float rf
            ? left.Iterations == right.Iterations ? (float)Lerp(lf, rf, amount) : amount < 0.5 ? lf : rf
            : null;
        destination.AnimationSpeed = (float)Lerp(left.AnimationSpeed, right.AnimationSpeed, amount);
        destination.EasingFactor = (float)Lerp(left.EasingFactor, right.EasingFactor, amount);
        destination.BlendedWeight = (float)Lerp(left.BlendedWeight, right.BlendedWeight, amount);
        destination.Iterations = amount < 0.5d ? left.Iterations : right.Iterations;
        DirectorReplayAnimationSample selected = amount < 0.5d ? left : right;
        destination.IsActive = selected.IsActive;
        destination.IsRunning = selected.IsRunning;
        destination.NowPlaying = selected.NowPlaying;
        destination.ShouldRewind = selected.ShouldRewind;
        destination.ShouldPlayTillEnd = selected.ShouldPlayTillEnd;
        destination.Weight = (float)Lerp(left.Weight, right.Weight, amount);
        destination.EaseInSpeed = amount < 0.5d ? left.EaseInSpeed : right.EaseInSpeed;
        destination.EaseOutSpeed = amount < 0.5d ? left.EaseOutSpeed : right.EaseOutSpeed;
        destination.BlendMode = amount < 0.5d ? left.BlendMode : right.BlendMode;
        destination.ElementWeights = CopyDictionary(
            selected.ElementWeights,
            destination.ElementWeights);
        destination.ElementBlendModes = CopyDictionary(
            selected.ElementBlendModes,
            destination.ElementBlendModes);
    }

    private static int FindExactAnimationStateIndex(
        IReadOnlyList<DirectorReplayAnimationSample> states,
        DirectorReplayAnimationSample target)
    {
        string targetStateCode = string.IsNullOrWhiteSpace(target.StateCode)
            ? target.Code
            : target.StateCode;
        for (int index = 0; index < states.Count; index++)
        {
            DirectorReplayAnimationSample candidate = states[index];
            string candidateStateCode = string.IsNullOrWhiteSpace(candidate.StateCode)
                ? candidate.Code
                : candidate.StateCode;
            if (string.Equals(candidate.Code, target.Code, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    candidateStateCode,
                    targetStateCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool AnimationSetsMatch(
        IReadOnlyList<DirectorReplayAnimationSample> left,
        IReadOnlyList<DirectorReplayAnimationSample> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return false;
        }

        HashSet<int> matched = new();
        foreach (DirectorReplayAnimationSample state in left)
        {
            int match = FindAnimationStateIndex(right, state, matched);
            if (match < 0)
            {
                return false;
            }
            matched.Add(match);
        }
        return true;
    }

    private static DirectorReplayAnimationSample? FindAnimationState(
        IReadOnlyList<DirectorReplayAnimationSample> states,
        DirectorReplayAnimationSample target)
    {
        int index = FindAnimationStateIndex(states, target, excluded: null);
        return index < 0 ? null : states[index];
    }

    private static int FindAnimationStateIndex(
        IReadOnlyList<DirectorReplayAnimationSample> states,
        DirectorReplayAnimationSample target,
        IReadOnlySet<int>? excluded)
    {
        string targetStateCode = string.IsNullOrWhiteSpace(target.StateCode)
            ? target.Code
            : target.StateCode;
        for (int index = 0; index < states.Count; index++)
        {
            DirectorReplayAnimationSample candidate = states[index];
            string candidateStateCode = string.IsNullOrWhiteSpace(candidate.StateCode)
                ? candidate.Code
                : candidate.StateCode;
            if (excluded?.Contains(index) != true
                && string.Equals(candidate.Code, target.Code, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    candidateStateCode,
                    targetStateCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        int fallback = -1;
        for (int index = 0; index < states.Count; index++)
        {
            if (excluded?.Contains(index) == true
                || !string.Equals(states[index].Code, target.Code, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (fallback >= 0)
            {
                return -1;
            }
            fallback = index;
        }
        return fallback;
    }

    private static float LerpAnimationProgress(
        DirectorReplayAnimationSample left,
        DirectorReplayAnimationSample right,
        double amount)
    {
        double delta = right.Progress - left.Progress;
        bool wraps = false;
        if (right.Iterations > left.Iterations || delta < -0.5d)
        {
            delta += 1d;
            wraps = true;
        }
        else if (right.Iterations < left.Iterations
            || (delta > 0.5d && (left.AnimationSpeed < 0f || right.AnimationSpeed < 0f)))
        {
            delta -= 1d;
            wraps = true;
        }

        double progress = left.Progress + delta * amount;
        if (wraps)
        {
            progress -= Math.Floor(progress);
        }
        return (float)Math.Clamp(progress, 0d, 1d);
    }

    private static void CopyAnimationStates(
        IReadOnlyList<DirectorReplayAnimationSample> source,
        List<DirectorReplayAnimationSample> destination)
    {
        for (int index = 0; index < source.Count; index++)
        {
            CopyAnimationState(
                source[index],
                GetAnimationState(destination, index));
        }
        if (destination.Count > source.Count)
        {
            destination.RemoveRange(source.Count, destination.Count - source.Count);
        }
    }

    private static DirectorReplayAnimationSample GetAnimationState(
        List<DirectorReplayAnimationSample> destination,
        int index)
        => index < destination.Count
            ? destination[index]
            : AddAnimationState(destination);

    private static void CopyAnimationState(
        DirectorReplayAnimationSample source,
        DirectorReplayAnimationSample destination)
    {
        destination.Code = source.Code;
        destination.StateCode = source.StateCode;
        destination.Progress = source.Progress;
        destination.RecordedFrame = source.RecordedFrame;
        destination.AnimationSpeed = source.AnimationSpeed;
        destination.EasingFactor = source.EasingFactor;
        destination.BlendedWeight = source.BlendedWeight;
        destination.Iterations = source.Iterations;
        destination.IsActive = source.IsActive;
        destination.IsRunning = source.IsRunning;
        destination.NowPlaying = source.NowPlaying;
        destination.ShouldRewind = source.ShouldRewind;
        destination.ShouldPlayTillEnd = source.ShouldPlayTillEnd;
        destination.Weight = source.Weight;
        destination.EaseInSpeed = source.EaseInSpeed;
        destination.EaseOutSpeed = source.EaseOutSpeed;
        destination.BlendMode = source.BlendMode;
        destination.ElementWeights = CopyDictionary(
            source.ElementWeights,
            destination.ElementWeights);
        destination.ElementBlendModes = CopyDictionary(
            source.ElementBlendModes,
            destination.ElementBlendModes);
    }

    private static DirectorReplayAnimationSample AddAnimationState(
        List<DirectorReplayAnimationSample> destination)
    {
        DirectorReplayAnimationSample state = new();
        destination.Add(state);
        return state;
    }

    private static Dictionary<string, TValue> CopyDictionary<TValue>(
        IReadOnlyDictionary<string, TValue>? source,
        Dictionary<string, TValue>? destination)
    {
        destination ??= new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        destination.Clear();
        if (source is null)
        {
            return destination;
        }

        foreach ((string key, TValue value) in source)
        {
            destination[key] = value;
        }
        return destination;
    }

    private static Dictionary<string, float> NormalizeElementWeights(
        IReadOnlyDictionary<string, float>? source)
    {
        Dictionary<string, float> result = new(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach ((string key, float value) in source.Take(256))
        {
            string normalized = key?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                continue;
            }
            result[normalized.Length <= 128 ? normalized : normalized[..128]]
                = Math.Clamp(NormalizeFinite(value), 0f, 1000f);
        }
        return result;
    }

    private static Dictionary<string, int> NormalizeElementBlendModes(
        IReadOnlyDictionary<string, int>? source)
    {
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach ((string key, int value) in source.Take(256))
        {
            string normalized = key?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                continue;
            }
            result[normalized.Length <= 128 ? normalized : normalized[..128]] = Math.Clamp(value, 0, 16);
        }
        return result;
    }

    private static void CopyPlayerState(
        DirectorReplayActorSample source,
        DirectorReplayActorSample destination)
        => CopyPlaybackState(source, destination);

    private static void CopyPlaybackState(
        DirectorReplayActorSample source,
        DirectorReplayActorSample destination)
    {
        destination.HasRenderState = source.HasRenderState;
        destination.OnGround = source.OnGround;
        destination.FeetInLiquid = source.FeetInLiquid;
        destination.Swimming = source.Swimming;
        destination.ClimbingOnFace = source.ClimbingOnFace ?? string.Empty;
        destination.LocalEyePosX = source.LocalEyePosX;
        destination.LocalEyePosY = source.LocalEyePosY;
        destination.LocalEyePosZ = source.LocalEyePosZ;
        destination.SelectionBoxY2 = source.SelectionBoxY2;
        destination.CollisionBoxY2 = source.CollisionBoxY2;
        destination.HasItemRenderState = source.HasItemRenderState;
        destination.ItemRenderElapsedMilliseconds = source.ItemRenderElapsedMilliseconds;
        destination.ItemTouchGroundAgeMilliseconds = source.ItemTouchGroundAgeMilliseconds;
        destination.ItemScaleRandom = source.ItemScaleRandom;
        destination.ItemYawRandom = source.ItemYawRandom;
        destination.ItemRenderXAngle = source.ItemRenderXAngle;
        destination.ItemRenderYAngle = source.ItemRenderYAngle;
        destination.ItemRenderZAngle = source.ItemRenderZAngle;
        destination.ItemRenderAccumulator = source.ItemRenderAccumulator;
        destination.HasAgentState = source.HasAgentState;
        destination.BodyYawServer = source.BodyYawServer;
        destination.HasThirdPersonPose = source.HasThirdPersonPose;
        destination.ThirdPersonBodyYaw = source.ThirdPersonBodyYaw;
        destination.ThirdPersonHeadYaw = source.ThirdPersonHeadYaw;
        destination.ThirdPersonHeadPitch = source.ThirdPersonHeadPitch;
        destination.HasPlayerState = source.HasPlayerState;
        destination.HasPlayerPostureState = source.HasPlayerPostureState;
        destination.CanStandUp = source.HasPlayerPostureState ? source.CanStandUp : true;
        destination.Controls = source.Controls;
        destination.ServerControls = source.ServerControls;
        destination.HandUse = source.HandUse;
        destination.ServerHandUse = source.ServerHandUse;
        destination.WalkYaw = source.WalkYaw;
        destination.WalkPitch = source.WalkPitch;
        destination.RightHandItem = source.RightHandItem ?? Array.Empty<byte>();
        destination.LeftHandItem = source.LeftHandItem ?? Array.Empty<byte>();
        CopyItemList(source.EquipmentItems, destination.EquipmentItems);
        CopyItemList(source.BackpackItems, destination.BackpackItems);
    }

    private static void InterpolatePlaybackState(
        DirectorReplayActorSample left,
        DirectorReplayActorSample right,
        double amount,
        DirectorReplayActorSample destination)
    {
        DirectorReplayActorSample selected = amount < 0.5d ? left : right;
        CopyPlaybackState(selected, destination);
        if (left.HasRenderState && right.HasRenderState)
        {
            destination.LocalEyePosX = Lerp(left.LocalEyePosX, right.LocalEyePosX, amount);
            destination.LocalEyePosY = Lerp(left.LocalEyePosY, right.LocalEyePosY, amount);
            destination.LocalEyePosZ = Lerp(left.LocalEyePosZ, right.LocalEyePosZ, amount);
            destination.SelectionBoxY2 = (float)Lerp(left.SelectionBoxY2, right.SelectionBoxY2, amount);
            destination.CollisionBoxY2 = (float)Lerp(left.CollisionBoxY2, right.CollisionBoxY2, amount);
        }
        if (left.HasAgentState && right.HasAgentState)
        {
            destination.BodyYawServer = LerpAngleRadians(left.BodyYawServer, right.BodyYawServer, amount);
        }
        if (left.HasThirdPersonPose && right.HasThirdPersonPose)
        {
            destination.HasThirdPersonPose = true;
            destination.ThirdPersonBodyYaw = LerpAngleRadians(
                left.ThirdPersonBodyYaw,
                right.ThirdPersonBodyYaw,
                amount);
            destination.ThirdPersonHeadYaw = LerpAngleRadians(
                left.ThirdPersonHeadYaw,
                right.ThirdPersonHeadYaw,
                amount);
            destination.ThirdPersonHeadPitch = LerpAngleRadians(
                left.ThirdPersonHeadPitch,
                right.ThirdPersonHeadPitch,
                amount);
        }
        if (left.HasItemRenderState && right.HasItemRenderState)
        {
            destination.HasItemRenderState = true;
            destination.ItemRenderElapsedMilliseconds = (long)Math.Round(Lerp(
                left.ItemRenderElapsedMilliseconds,
                right.ItemRenderElapsedMilliseconds,
                amount));
            destination.ItemTouchGroundAgeMilliseconds = (long)Math.Round(Lerp(
                left.ItemTouchGroundAgeMilliseconds,
                right.ItemTouchGroundAgeMilliseconds,
                amount));
            destination.ItemScaleRandom = (float)Lerp(
                left.ItemScaleRandom,
                right.ItemScaleRandom,
                amount);
            destination.ItemYawRandom = LerpAngleRadians(
                left.ItemYawRandom,
                right.ItemYawRandom,
                amount);
            destination.ItemRenderXAngle = (float)Lerp(
                left.ItemRenderXAngle,
                right.ItemRenderXAngle,
                amount);
            destination.ItemRenderYAngle = (float)Lerp(
                left.ItemRenderYAngle,
                right.ItemRenderYAngle,
                amount);
            destination.ItemRenderZAngle = (float)Lerp(
                left.ItemRenderZAngle,
                right.ItemRenderZAngle,
                amount);
            destination.ItemRenderAccumulator = (float)Lerp(
                left.ItemRenderAccumulator,
                right.ItemRenderAccumulator,
                amount);
        }
        if (left.HasPlayerState && right.HasPlayerState)
        {
            destination.WalkYaw = LerpAngleRadians(left.WalkYaw, right.WalkYaw, amount);
            destination.WalkPitch = LerpAngleRadians(left.WalkPitch, right.WalkPitch, amount);
        }
    }

    private static string NormalizeFacingCode(string? value)
    {
        string code = value?.Trim() ?? string.Empty;
        return code.Length <= 32 ? code : code[..32];
    }

    private static string NormalizeAnimationCode(string? value, string fallback)
    {
        string code = value?.Trim() ?? string.Empty;
        if (code.Length == 0)
        {
            code = fallback;
        }
        return code.Length <= 128 ? code : code[..128];
    }

    private static double Lerp(double from, double to, double amount)
        => from + (to - from) * amount;

    private static List<byte[]> NormalizeItemList(IReadOnlyList<byte[]>? source, int maximum)
    {
        List<byte[]> result = new();
        if (source is null)
        {
            return result;
        }
        foreach (byte[]? item in source.Take(maximum))
        {
            result.Add(CloneBytes(item));
        }
        return result;
    }

    private static void CopyItemList(IReadOnlyList<byte[]>? source, List<byte[]> destination)
    {
        destination.Clear();
        if (source is null)
        {
            return;
        }
        destination.AddRange(source);
    }

    private static double NormalizeRadians(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        double wrapped = (value + Math.PI) % (Math.PI * 2d);
        if (wrapped < 0d)
        {
            wrapped += Math.PI * 2d;
        }

        return wrapped - Math.PI;
    }

    private static double NormalizeNonNegative(double value)
        => double.IsFinite(value) ? Math.Max(0d, value) : 0d;

    private static double NormalizeFinite(double value)
        => double.IsFinite(value) ? value : 0d;

    private static float NormalizeFinite(float value)
        => float.IsFinite(value) ? value : 0f;

    private static byte[] CloneBytes(byte[]? data)
        => data is null || data.Length == 0 ? Array.Empty<byte>() : (byte[])data.Clone();
}
