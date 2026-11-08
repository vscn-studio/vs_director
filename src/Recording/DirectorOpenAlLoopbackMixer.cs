using System.Collections;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VSDirector.Rendering;
using VSDirector.Runtime;

namespace VSDirector.Recording;

internal readonly record struct DirectorOpenAlDiagnostics(
    int MirroredSources,
    int PlayingPositionalSources,
    float LeftPeak,
    float RightPeak,
    float ListenerX,
    float ListenerY,
    float ListenerZ);

internal sealed class DirectorOpenAlLoopbackMixer : IDisposable
{
    private const int FramesPerChunk = 480;
    // Keep the game-audio mirror below the common OpenAL source limit so
    // spatial voice streams can always allocate their own sources.
    private const int MaximumGameAudioSources = 96;
    private const int MaximumDirectVoiceStreams = 32;
    private const int PlayingState = 4114;
    private const int PausedState = 4115;
    private const int StoppedState = 4116;

    private static readonly FieldInfo? LoadedSoundsField = typeof(LoadedSoundNative)
        .GetField("loadedSounds", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo? LoadedSoundsLockField = typeof(LoadedSoundNative)
        .GetField("loadedSoundsLock", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo? SampleField = typeof(LoadedSoundNative)
        .GetField("sample", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? SourceStateField = typeof(LoadedSoundNative)
        .GetField("sourceState", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PitchOffsetField = typeof(LoadedSoundNative)
        .GetField("pitchOffset", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PlaybackPositionField = typeof(LoadedSoundNative)
        .GetField("playbackPosition", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly DirectorObsSharedTransport transport;
    private readonly Func<DirectorOffscreenCameraPose?> poseProvider;
    private readonly bool captureGameAudio;
    private readonly Dictionary<LoadedSoundNative, MirrorSound> mirrors =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LoadedSoundNative, float> initialPlaybackPositions =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentQueue<DirectorLiveSoundEvent> directSoundEvents = new();
    private readonly List<DirectSound> directSounds = new();
    private readonly ConcurrentQueue<DirectorVoicePcmFrame> directVoiceEvents = new();
    private readonly Dictionary<string, DirectVoiceStream> directVoiceStreams = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim started = new(false);
    private readonly CancellationTokenSource cancellation = new();
    private Thread? thread;
    private string startError = string.Empty;
    private string failure = string.Empty;
    private bool disposed;
    private volatile int mirroredSources;
    private volatile int playingPositionalSources;
    private volatile float leftPeak;
    private volatile float rightPeak;
    private volatile float listenerX;
    private volatile float listenerY;
    private volatile float listenerZ;
    private volatile float playerX;
    private volatile float playerY;
    private volatile float playerZ;

    internal bool IsRunning => thread is { IsAlive: true } && failure.Length == 0;

    internal string FailureMessage => failure;

    internal DirectorOpenAlDiagnostics Diagnostics => new(
        mirroredSources,
        playingPositionalSources,
        leftPeak,
        rightPeak,
        listenerX,
        listenerY,
        listenerZ);

    internal DirectorOpenAlLoopbackMixer(
        DirectorObsSharedTransport transport,
        Func<DirectorOffscreenCameraPose?> poseProvider,
        bool captureGameAudio)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.poseProvider = poseProvider ?? throw new ArgumentNullException(nameof(poseProvider));
        this.captureGameAudio = captureGameAudio;
    }

    internal bool Start(out string error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (captureGameAudio
            && (LoadedSoundsField is null
                || LoadedSoundsLockField is null
                || SampleField is null
                || SourceStateField is null))
        {
            error = "Vintage Story 1.22.3 sound-source bindings are unavailable.";
            return false;
        }
        if (thread is not null)
        {
            error = startError;
            return startError.Length == 0;
        }

        foreach (LoadedSoundNative sound in captureGameAudio
            ? SnapshotLoadedSounds()
            : Array.Empty<LoadedSoundNative>())
        {
            try
            {
                initialPlaybackPositions[sound] = sound.PlaybackPosition;
            }
            catch
            {
                initialPlaybackPositions[sound] = 0f;
            }
        }

        thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "VSDirector OpenAL Loopback"
        };
        thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(5)))
        {
            error = "Timed out while initializing OpenAL Soft loopback.";
            return false;
        }

        error = startError;
        return error.Length == 0;
    }

    internal void UpdatePlayerPosition(Vec3f position)
    {
        playerX = position.X;
        playerY = position.Y;
        playerZ = position.Z;
    }

    internal void CaptureSoundEvent(
        ClientMain game,
        AssetLocation? location,
        double x,
        double y,
        double z,
        float volume,
        float pitch,
        float range,
        EnumSoundType soundType)
    {
        if (!captureGameAudio
            || disposed
            || location is null
            || x == 0d && y == 0d && z == 0d
            || directSoundEvents.Count >= 256
            || poseProvider() is not DirectorOffscreenCameraPose camera)
        {
            return;
        }

        double directorY = camera.Y + camera.Dimension * 32768d;
        double directorDistanceSquared = SquareDistance(camera.X, directorY, camera.Z, x, y, z);
        double playerDistanceSquared = DirectorFollowSoundIsolation.DistanceSquaredFromActiveListener(
            game.EntityPlayer.Pos,
            x,
            y,
            z);
        double rangeSquared = range * range;
        if (directorDistanceSquared > rangeSquared || playerDistanceSquared <= rangeSquared)
        {
            return;
        }

        try
        {
            AssetLocation resolved = game.ResolveSoundPath(location).Clone().WithPathAppendixOnce(".ogg");
            if (!ScreenManager.soundAudioData.TryGetValue(resolved, out AudioData? data)
                || data is not AudioMetaData sample)
            {
                return;
            }
            if (sample.Loaded == 0)
            {
                sample.Load();
            }
            if (sample.Pcm is not { Length: > 0 })
            {
                return;
            }

            directSoundEvents.Enqueue(new DirectorLiveSoundEvent(
                sample,
                (float)x,
                (float)y,
                (float)z,
                Math.Clamp(volume * GlobalVolume(soundType), 0f, 1f),
                Math.Clamp(pitch, 0.1f, 3f),
                Math.Max(1f, range)));
        }
        catch
        {
        }
    }

    internal void CaptureVoiceFrame(DirectorVoicePcmFrame frame)
    {
        if (disposed || frame.Samples is not { Length: > 0 } || directVoiceEvents.Count >= 256)
        {
            return;
        }
        directVoiceEvents.Enqueue(frame);
    }

    private void Worker()
    {
        DirectorOpenAlNative? openAl = null;
        try
        {
            openAl = new DirectorOpenAlNative();
            openAl.InitializeLoopback(DirectorObsSharedTransport.AudioSampleRate);
            startError = string.Empty;
            started.Set();
            RunMixer(openAl, cancellation.Token);
        }
        catch (Exception exception)
        {
            string message = exception.GetBaseException().Message;
            if (!started.IsSet)
            {
                startError = message;
                started.Set();
            }
            else if (!cancellation.IsCancellationRequested)
            {
                failure = message;
            }
        }
        finally
        {
            foreach (MirrorSound mirror in mirrors.Values)
            {
                mirror.Dispose(openAl);
            }
            mirrors.Clear();
            foreach (DirectSound sound in directSounds)
            {
                if (openAl is not null)
                {
                    sound.Dispose(openAl);
                }
            }
            directSounds.Clear();
            directVoiceStreams.Clear();
            openAl?.Dispose();
            started.Set();
        }
    }

    private void RunMixer(DirectorOpenAlNative openAl, CancellationToken token)
    {
        short[] samples = new short[FramesPerChunk * DirectorObsSharedTransport.AudioChannels];
        long nextTimestamp = Stopwatch.GetTimestamp();
        long chunkTicks = Stopwatch.Frequency * FramesPerChunk
            / DirectorObsSharedTransport.AudioSampleRate;
        while (!token.IsCancellationRequested)
        {
            if (captureGameAudio)
            {
                SyncSounds(openAl);
                SyncDirectSounds(openAl);
            }
            long now = Stopwatch.GetTimestamp();
            SyncDirectVoiceSounds(now);
            UpdateListener(openAl);
            Array.Clear(samples);
            openAl.RenderSamples(samples, FramesPerChunk);
            // OpenAL Soft loopback exposes its stereo render pair in the
            // opposite order from OBS's SPEAKERS_STEREO interleaved input.
            SwapStereoChannels(samples, FramesPerChunk);
            MixDirectVoiceSounds(samples, FramesPerChunk, now);
            UpdateAudioLevels(samples);
            transport.PublishAudio(samples, FramesPerChunk);

            nextTimestamp += chunkTicks;
            long remaining = nextTimestamp - Stopwatch.GetTimestamp();
            if (remaining > Stopwatch.Frequency / 1000L)
            {
                Thread.Sleep((int)Math.Max(1L, remaining * 1000L / Stopwatch.Frequency));
            }
            else if (remaining < -chunkTicks * 4L)
            {
                int dropped = (int)Math.Min(int.MaxValue, -remaining / Math.Max(1L, chunkTicks));
                transport.ReportDroppedAudioFrames(dropped * FramesPerChunk);
                nextTimestamp = Stopwatch.GetTimestamp();
            }
        }
    }

    private void SyncSounds(DirectorOpenAlNative openAl)
    {
        LoadedSoundNative[] active = SnapshotLoadedSounds();
        HashSet<LoadedSoundNative> retained = new(active, ReferenceEqualityComparer.Instance);
        foreach (LoadedSoundNative stale in mirrors.Keys.Where(sound => !retained.Contains(sound)).ToArray())
        {
            mirrors.Remove(stale, out MirrorSound? mirror);
            mirror?.Dispose(openAl);
        }

        foreach (LoadedSoundNative sound in active)
        {
            if (sound.IsDisposed
                || sound.Params is not SoundParams parameters)
            {
                continue;
            }

            int state = SourceState(sound);
            if (state is not (PlayingState or PausedState))
            {
                initialPlaybackPositions.Remove(sound);
                if (mirrors.Remove(sound, out MirrorSound? stopped))
                {
                    stopped.Dispose(openAl);
                }
                continue;
            }

            if (DirectorSoundSpatialPolicy.AnchorRelativeSoundToWorld(
                    parameters.RelativePosition,
                    parameters.SoundType))
            {
                initialPlaybackPositions.Remove(sound);
                if (mirrors.Remove(sound, out MirrorSound? excluded))
                {
                    excluded.Dispose(openAl);
                }
                continue;
            }

            if (SampleField?.GetValue(sound) is not AudioMetaData sample
                || sample.Pcm is not { Length: > 0 })
            {
                continue;
            }

            if (!mirrors.TryGetValue(sound, out MirrorSound? mirror))
            {
                if (mirrors.Count + directSounds.Count >= MaximumGameAudioSources)
                {
                    continue;
                }
                mirror = MirrorSound.TryCreate(openAl, sound, parameters, sample);
                if (mirror is null)
                {
                    continue;
                }
                if (initialPlaybackPositions.Remove(sound, out float initialPlayback)
                    && initialPlayback > 0f)
                {
                    openAl.SetSourceSeconds(mirror.Source, initialPlayback);
                }
                mirrors[sound] = mirror;
            }

            mirror.Synchronize(
                openAl,
                sound,
                parameters,
                new Vec3f(playerX, playerY, playerZ));
        }

        mirroredSources = mirrors.Count + directSounds.Count;
        playingPositionalSources = mirrors.Values.Count(mirror => mirror.IsPlayingPositional)
            + directSounds.Count;
    }

    private void SyncDirectSounds(DirectorOpenAlNative openAl)
    {
        long now = Stopwatch.GetTimestamp();
        for (int index = directSounds.Count - 1; index >= 0; index--)
        {
            if (directSounds[index].EndTimestamp > now)
            {
                continue;
            }
            directSounds[index].Dispose(openAl);
            directSounds.RemoveAt(index);
        }

        while (directSoundEvents.TryDequeue(out DirectorLiveSoundEvent soundEvent))
        {
            if (mirrors.Count + directSounds.Count >= MaximumGameAudioSources)
            {
                continue;
            }
            DirectSound? sound = DirectSound.TryCreate(openAl, soundEvent, now);
            if (sound is not null)
            {
                directSounds.Add(sound);
            }
        }
    }

    private void SyncDirectVoiceSounds(long now)
    {
        while (directVoiceEvents.TryDequeue(out DirectorVoicePcmFrame? frame))
        {
            if (!directVoiceStreams.TryGetValue(frame.SourceId, out DirectVoiceStream? stream))
            {
                if (directVoiceStreams.Count >= MaximumDirectVoiceStreams
                    || !DirectVoiceStream.TryCreate(frame, now, out DirectVoiceStream? created)
                    || created is null)
                {
                    continue;
                }

                stream = created;
                directVoiceStreams[frame.SourceId] = created;
            }

            stream.Enqueue(frame, now);
        }
    }

    private void MixDirectVoiceSounds(short[] samples, int frameCount, long now)
    {
        DirectorOffscreenCameraPose? pose = poseProvider();
        foreach (KeyValuePair<string, DirectVoiceStream> entry in directVoiceStreams.ToArray())
        {
            DirectVoiceStream stream = entry.Value;
            if (pose is DirectorOffscreenCameraPose listener)
            {
                stream.Mix(samples, frameCount, listener, now);
            }

            if (!stream.IsIdle(now))
            {
                continue;
            }

            directVoiceStreams.Remove(entry.Key);
        }
    }

    private void UpdateListener(DirectorOpenAlNative openAl)
    {
        DirectorOffscreenCameraPose? pose = poseProvider();
        if (pose is not DirectorOffscreenCameraPose camera)
        {
            return;
        }

        float yaw = camera.Yaw * GameMath.DEG2RAD;
        float pitch = camera.Pitch * GameMath.DEG2RAD;
        float roll = camera.Roll * GameMath.DEG2RAD;
        Vec3f forward = EntityPos.GetViewVector(pitch, yaw);
        Vec3f up = new(
            GameMath.Sin(pitch) * GameMath.Sin(yaw),
            GameMath.Cos(pitch),
            GameMath.Sin(pitch) * GameMath.Cos(yaw));
        Vec3f right = new(
            forward.Y * up.Z - forward.Z * up.Y,
            forward.Z * up.X - forward.X * up.Z,
            forward.X * up.Y - forward.Y * up.X);
        float cosRoll = GameMath.Cos(roll);
        float sinRoll = GameMath.Sin(roll);
        up.Set(
            up.X * cosRoll + right.X * sinRoll,
            up.Y * cosRoll + right.Y * sinRoll,
            up.Z * cosRoll + right.Z * sinRoll);
        listenerX = (float)camera.X;
        listenerY = (float)(camera.Y + camera.Dimension * 32768d);
        listenerZ = (float)camera.Z;
        openAl.SetListener(
            listenerX,
            listenerY,
            listenerZ,
            forward.X,
            forward.Y,
            forward.Z,
            up.X,
            up.Y,
            up.Z);
    }

    private void UpdateAudioLevels(short[] samples)
    {
        int peakLeft = 0;
        int peakRight = 0;
        for (int index = 0; index + 1 < samples.Length; index += 2)
        {
            peakLeft = Math.Max(peakLeft, Math.Abs((int)samples[index]));
            peakRight = Math.Max(peakRight, Math.Abs((int)samples[index + 1]));
        }
        leftPeak = peakLeft / 32768f;
        rightPeak = peakRight / 32768f;
    }

    private static void SwapStereoChannels(short[] samples, int frameCount)
    {
        int sampleCount = Math.Min(samples.Length / 2, Math.Max(0, frameCount));
        for (int frame = 0; frame < sampleCount; frame++)
        {
            int offset = frame * 2;
            (samples[offset], samples[offset + 1]) = (samples[offset + 1], samples[offset]);
        }
    }

    private static LoadedSoundNative[] SnapshotLoadedSounds()
    {
        if (LoadedSoundsField?.GetValue(null) is not IEnumerable loaded
            || LoadedSoundsLockField?.GetValue(null) is not object gate)
        {
            return Array.Empty<LoadedSoundNative>();
        }

        lock (gate)
        {
            return loaded.Cast<object>().OfType<LoadedSoundNative>().ToArray();
        }
    }

    private static int SourceState(LoadedSoundNative sound)
        => SourceStateField?.GetValue(sound) is object value
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : StoppedState;

    private static float PitchOffset(LoadedSoundNative sound)
        => PitchOffsetField?.GetValue(sound) is float value ? value : 0f;

    private static float PlaybackPosition(LoadedSoundNative sound)
        => PlaybackPositionField?.GetValue(sound) is float value ? value : 0f;

    private static float GlobalVolume(EnumSoundType type)
        => type switch
        {
            EnumSoundType.Music or EnumSoundType.MusicGlitchunaffected
                => (float)ClientSettings.MusicLevel / 100f,
            EnumSoundType.Weather => (float)ClientSettings.WeatherSoundLevel / 100f,
            EnumSoundType.Ambient or EnumSoundType.AmbientGlitchunaffected
                => (float)ClientSettings.AmbientSoundLevel / 100f,
            EnumSoundType.Entity => (float)ClientSettings.EntitySoundLevel / 100f,
            _ => (float)ClientSettings.SoundLevel / 100f
        };

    private static double SquareDistance(
        double ax,
        double ay,
        double az,
        double bx,
        double by,
        double bz)
    {
        double dx = ax - bx;
        double dy = ay - by;
        double dz = az - bz;
        return dx * dx + dy * dy + dz * dz;
    }

    private static (byte[] Pcm, int Channels) PreparePcm(
        byte[] pcm,
        int channels,
        int bitsPerSample,
        bool positional)
    {
        if (!positional || channels != 2)
        {
            return (pcm, channels);
        }

        if (bitsPerSample == 16)
        {
            byte[] mono = new byte[pcm.Length / 2];
            for (int source = 0, target = 0; source + 3 < pcm.Length; source += 4, target += 2)
            {
                short left = (short)(pcm[source] | pcm[source + 1] << 8);
                short right = (short)(pcm[source + 2] | pcm[source + 3] << 8);
                short mixed = (short)(((int)left + right) / 2);
                mono[target] = (byte)mixed;
                mono[target + 1] = (byte)(mixed >> 8);
            }
            return (mono, 1);
        }

        if (bitsPerSample == 8)
        {
            byte[] mono = new byte[pcm.Length / 2];
            for (int source = 0, target = 0; source + 1 < pcm.Length; source += 2, target++)
            {
                mono[target] = (byte)((pcm[source] + pcm[source + 1]) / 2);
            }
            return (mono, 1);
        }

        return (pcm, channels);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        cancellation.Cancel();
        if (thread is { IsAlive: true } active && !active.Join(TimeSpan.FromSeconds(3)))
        {
            failure = "OpenAL loopback worker did not stop within three seconds.";
        }
        cancellation.Dispose();
        started.Dispose();
    }

    private sealed class MirrorSound
    {
        private int state;
        private float pitch = float.NaN;
        private float volume = float.NaN;
        private bool looping;
        private bool relative;
        private float x = float.NaN;
        private float y = float.NaN;
        private float z = float.NaN;

        internal bool IsPlayingPositional => state == PlayingState && !relative;

        internal uint Source { get; private set; }

        internal uint Buffer { get; private set; }

        internal static MirrorSound? TryCreate(
            DirectorOpenAlNative openAl,
            LoadedSoundNative original,
            SoundParams parameters,
            AudioMetaData sample)
        {
            try
            {
                (byte[] pcm, int channels) = PreparePcm(sample, parameters);
                uint buffer = openAl.CreateBuffer(pcm, channels, sample.BitsPerSample, sample.Rate);
                uint source = openAl.CreateSource(buffer, parameters);
                MirrorSound mirror = new()
                {
                    Source = source,
                    Buffer = buffer,
                    state = StoppedState
                };
                float playback = PlaybackPosition(original);
                if (playback > 0f)
                {
                    openAl.SetSourceSeconds(source, playback);
                }
                return mirror;
            }
            catch
            {
                return null;
            }
        }

        private static (byte[] Pcm, int Channels) PreparePcm(
            AudioMetaData sample,
            SoundParams parameters)
            => DirectorOpenAlLoopbackMixer.PreparePcm(
                sample.Pcm,
                sample.Channels,
                sample.BitsPerSample,
                !parameters.RelativePosition
                    || !DirectorSoundSpatialPolicy.KeepListenerRelative(parameters.SoundType));

        internal void Synchronize(
            DirectorOpenAlNative openAl,
            LoadedSoundNative original,
            SoundParams parameters,
            Vec3f playerPosition)
        {
            float nextPitch = Math.Clamp(parameters.Pitch + PitchOffset(original), 0.1f, 3f);
            float nextVolume = Math.Clamp(parameters.Volume * GlobalVolume(parameters.SoundType), 0f, 1f);
            bool nextRelative = parameters.RelativePosition
                && DirectorSoundSpatialPolicy.KeepListenerRelative(parameters.SoundType);
            float nextX = parameters.Position?.X ?? (nextRelative ? 0f : playerPosition.X);
            float nextY = parameters.Position?.Y ?? (nextRelative ? 0f : playerPosition.Y);
            float nextZ = parameters.Position?.Z ?? (nextRelative ? 0f : playerPosition.Z);
            if (nextPitch != pitch)
            {
                openAl.SetSourcePitch(Source, nextPitch);
                pitch = nextPitch;
            }
            if (nextVolume != volume)
            {
                openAl.SetSourceGain(Source, nextVolume);
                volume = nextVolume;
            }
            if (parameters.ShouldLoop != looping)
            {
                openAl.SetSourceLooping(Source, parameters.ShouldLoop);
                looping = parameters.ShouldLoop;
            }
            if (nextRelative != relative)
            {
                openAl.SetSourceRelative(Source, nextRelative);
                relative = nextRelative;
            }
            if (nextX != x || nextY != y || nextZ != z)
            {
                openAl.SetSourcePosition(Source, nextX, nextY, nextZ);
                x = nextX;
                y = nextY;
                z = nextZ;
            }

            int nextState = SourceState(original);
            if (nextState == state)
            {
                return;
            }
            if (nextState == PlayingState)
            {
                openAl.PlaySource(Source);
            }
            else if (nextState == PausedState)
            {
                openAl.PauseSource(Source);
            }
            else
            {
                openAl.StopSource(Source);
            }
            state = nextState;
        }

        internal void Dispose(DirectorOpenAlNative? openAl)
        {
            if (openAl is null)
            {
                return;
            }
            openAl.DeleteSource(Source);
            openAl.DeleteBuffer(Buffer);
            Source = 0;
            Buffer = 0;
        }
    }

    private readonly record struct DirectorLiveSoundEvent(
        AudioMetaData Sample,
        float X,
        float Y,
        float Z,
        float Volume,
        float Pitch,
        float Range);

    private sealed class DirectVoiceStream
    {
        // The game drains submitted API frames on its tick, not on the audio
        // thread. Keep enough PCM ahead of OpenAL to absorb that scheduling
        // jitter before beginning playback.
        private const int StartupBuffers = 6;
        private const int MaximumQueuedBuffers = 48;
        private static readonly long StartupTimeoutTicks = Stopwatch.Frequency * 3L / 20L;
        private readonly Queue<short[]> queuedBuffers = new();
        private short[]? activeBuffer;
        private int activeOffset;
        private DirectorVoiceSpatialization spatialization;
        private float volume;
        private long lastFrameTimestamp;
        private bool started;

        internal static bool TryCreate(
            DirectorVoicePcmFrame frame,
            long now,
            out DirectVoiceStream? stream)
        {
            stream = null;
            try
            {
                stream = new DirectVoiceStream
                {
                    lastFrameTimestamp = now,
                    spatialization = frame.Spatialization,
                    volume = frame.Volume
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void Enqueue(DirectorVoicePcmFrame frame, long now)
        {
            spatialization = frame.Spatialization;
            volume = frame.Volume;
            lastFrameTimestamp = now;
            if (queuedBuffers.Count >= MaximumQueuedBuffers)
            {
                return;
            }

            queuedBuffers.Enqueue(frame.Samples);
        }

        internal void Mix(
            short[] destination,
            int frameCount,
            DirectorOffscreenCameraPose listener,
            long now)
        {
            if (!started && queuedBuffers.Count < StartupBuffers
                && now - lastFrameTimestamp < StartupTimeoutTicks)
            {
                return;
            }

            started = true;
            _ = TryResolveStereoGain(listener, out float leftGain, out float rightGain);

            int destinationFrames = Math.Min(frameCount, destination.Length / DirectorObsSharedTransport.AudioChannels);
            for (int frame = 0; frame < destinationFrames; frame++)
            {
                if (!TryReadSample(out short sample))
                {
                    break;
                }

                int offset = frame * DirectorObsSharedTransport.AudioChannels;
                destination[offset] = MixSample(destination[offset], sample, leftGain);
                destination[offset + 1] = MixSample(destination[offset + 1], sample, rightGain);
            }
        }

        internal bool IsIdle(long now)
            => activeBuffer is null
                && queuedBuffers.Count == 0
                && now - lastFrameTimestamp >= Stopwatch.Frequency / 2L;

        private bool TryReadSample(out short sample)
        {
            while (activeBuffer is null || activeOffset >= activeBuffer.Length)
            {
                if (!queuedBuffers.TryDequeue(out short[]? next))
                {
                    activeBuffer = null;
                    activeOffset = 0;
                    sample = 0;
                    return false;
                }
                activeBuffer = next;
                activeOffset = 0;
            }

            sample = activeBuffer[activeOffset++];
            return true;
        }

        private bool TryResolveStereoGain(
            DirectorOffscreenCameraPose listener,
            out float leftGain,
            out float rightGain)
        {
            if (!spatialization.IsSpatialized)
            {
                leftGain = volume;
                rightGain = volume;
                return volume > 0f;
            }

            if (spatialization.Position.Dimension != listener.Dimension)
            {
                leftGain = 0f;
                rightGain = 0f;
                return false;
            }

            float dx = (float)(spatialization.Position.X - listener.X);
            float dy = (float)(spatialization.Position.Y - listener.Y);
            float dz = (float)(spatialization.Position.Z - listener.Z);
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!float.IsFinite(distance) || distance > spatialization.MaxDistance)
            {
                leftGain = 0f;
                rightGain = 0f;
                return false;
            }

            float gain = volume * spatialization.ResolveGain(distance);
            if (gain <= 0f)
            {
                leftGain = 0f;
                rightGain = 0f;
                return false;
            }

            float horizontalLength = MathF.Sqrt(dx * dx + dz * dz);
            if (horizontalLength <= 0.0001f)
            {
                leftGain = gain;
                rightGain = gain;
                return true;
            }

            float yaw = listener.Yaw * GameMath.DEG2RAD;
            Vec3f forward = EntityPos.GetViewVector(0f, yaw);
            float rightX = forward.Z;
            float rightZ = -forward.X;
            float pan = Math.Clamp((dx * rightX + dz * rightZ) / horizontalLength, -1f, 1f);
            leftGain = gain * MathF.Sqrt(0.5f * (1f - pan));
            rightGain = gain * MathF.Sqrt(0.5f * (1f + pan));
            return true;
        }

        private static short MixSample(short current, short incoming, float gain)
            => (short)Math.Clamp(
                current + (int)MathF.Round(incoming * gain),
                short.MinValue,
                short.MaxValue);
    }

    private sealed class DirectSound
    {
        internal uint Source { get; private set; }

        internal uint Buffer { get; private set; }

        internal long EndTimestamp { get; private init; }

        internal static DirectSound? TryCreate(
            DirectorOpenAlNative openAl,
            DirectorLiveSoundEvent soundEvent,
            long now)
        {
            try
            {
                AudioMetaData sample = soundEvent.Sample;
                (byte[] pcm, int channels) = PreparePcm(
                    sample.Pcm,
                    sample.Channels,
                    sample.BitsPerSample,
                    positional: true);
                uint buffer = openAl.CreateBuffer(pcm, channels, sample.BitsPerSample, sample.Rate);
                uint source = openAl.CreateSource(buffer, soundEvent.Range, 3f);
                openAl.SetSourceRelative(source, false);
                openAl.SetSourcePosition(source, soundEvent.X, soundEvent.Y, soundEvent.Z);
                openAl.SetSourceGain(source, soundEvent.Volume);
                openAl.SetSourcePitch(source, soundEvent.Pitch);
                openAl.PlaySource(source);
                double bytesPerFrame = channels * Math.Max(1, sample.BitsPerSample / 8);
                double seconds = pcm.Length / bytesPerFrame / Math.Max(1, sample.Rate)
                    / soundEvent.Pitch;
                return new DirectSound
                {
                    Source = source,
                    Buffer = buffer,
                    EndTimestamp = now + (long)((seconds + 0.5d) * Stopwatch.Frequency)
                };
            }
            catch
            {
                return null;
            }
        }

        internal void Dispose(DirectorOpenAlNative openAl)
        {
            openAl.DeleteSource(Source);
            openAl.DeleteBuffer(Buffer);
            Source = 0;
            Buffer = 0;
        }
    }
}

internal sealed class DirectorOpenAlNative : IDisposable
{
    private const int AlcFrequency = 0x1007;
    private const int AlcFormatChannelsSoft = 0x1990;
    private const int AlcStereoSoft = 0x1501;
    private const int AlcFormatTypeSoft = 0x1991;
    private const int AlcShortSoft = 0x1402;
    private const int AlBuffer = 0x1009;
    private const int AlPitch = 0x1003;
    private const int AlPosition = 0x1004;
    private const int AlGain = 0x100A;
    private const int AlLooping = 0x1007;
    private const int AlSourceState = 0x1010;
    private const int AlBuffersProcessed = 0x1016;
    private const int AlSourceRelative = 0x202;
    private const int AlSecOffset = 0x1024;
    private const int AlRolloffFactor = 0x1021;
    private const int AlReferenceDistance = 0x1020;
    private const int AlMaxDistance = 0x1023;
    private const int AlListenerPosition = 0x1004;
    private const int AlListenerOrientation = 0x100F;
    private const int AlExponentDistanceClamped = 0xD006;

    private readonly IntPtr library;
    private readonly AlcGetProcAddress alcGetProcAddress;
    private readonly AlcGetCurrentContext alcGetCurrentContext;
    private readonly AlGetError alGetError;
    private readonly AlcCreateContext alcCreateContext;
    private readonly AlcDestroyContext alcDestroyContext;
    private readonly AlcCloseDevice alcCloseDevice;
    private readonly AlGenObject alGenBuffers;
    private readonly AlDeleteObject alDeleteBuffers;
    private readonly AlGenObject alGenSources;
    private readonly AlDeleteObject alDeleteSources;
    private readonly AlBufferData alBufferData;
    private readonly AlSourceI alSourceI;
    private readonly AlGetSourceI alGetSourceI;
    private readonly AlSourceF alSourceF;
    private readonly AlSource3F alSource3F;
    private readonly AlSourceQueueBuffers alSourceQueueBuffers;
    private readonly AlSourceUnqueueBuffers alSourceUnqueueBuffers;
    private readonly AlSourceAction alSourcePlay;
    private readonly AlSourceAction alSourcePause;
    private readonly AlSourceAction alSourceStop;
    private readonly AlListener3F alListener3F;
    private readonly AlListenerFV alListenerFV;
    private readonly AlDistanceModel alDistanceModel;
    private AlcLoopbackOpenDeviceSoft? alcLoopbackOpenDevice;
    private AlcRenderSamplesSoft? alcRenderSamples;
    private AlcSetThreadContext? alcSetThreadContext;
    private IntPtr device;
    private IntPtr context;
    private bool disposed;

    internal DirectorOpenAlNative()
    {
        library = LoadLibrary();
        alcGetProcAddress = Load<AlcGetProcAddress>("alcGetProcAddress");
        alcGetCurrentContext = Load<AlcGetCurrentContext>("alcGetCurrentContext");
        alGetError = Load<AlGetError>("alGetError");
        alcCreateContext = Load<AlcCreateContext>("alcCreateContext");
        alcDestroyContext = Load<AlcDestroyContext>("alcDestroyContext");
        alcCloseDevice = Load<AlcCloseDevice>("alcCloseDevice");
        alGenBuffers = Load<AlGenObject>("alGenBuffers");
        alDeleteBuffers = Load<AlDeleteObject>("alDeleteBuffers");
        alGenSources = Load<AlGenObject>("alGenSources");
        alDeleteSources = Load<AlDeleteObject>("alDeleteSources");
        alBufferData = Load<AlBufferData>("alBufferData");
        alSourceI = Load<AlSourceI>("alSourcei");
        alGetSourceI = Load<AlGetSourceI>("alGetSourcei");
        alSourceF = Load<AlSourceF>("alSourcef");
        alSource3F = Load<AlSource3F>("alSource3f");
        alSourceQueueBuffers = Load<AlSourceQueueBuffers>("alSourceQueueBuffers");
        alSourceUnqueueBuffers = Load<AlSourceUnqueueBuffers>("alSourceUnqueueBuffers");
        alSourcePlay = Load<AlSourceAction>("alSourcePlay");
        alSourcePause = Load<AlSourceAction>("alSourcePause");
        alSourceStop = Load<AlSourceAction>("alSourceStop");
        alListener3F = Load<AlListener3F>("alListener3f");
        alListenerFV = Load<AlListenerFV>("alListenerfv");
        alDistanceModel = Load<AlDistanceModel>("alDistanceModel");
    }

    internal void InitializeLoopback(int sampleRate)
    {
        IntPtr openPointer = alcGetProcAddress(IntPtr.Zero, "alcLoopbackOpenDeviceSOFT");
        if (openPointer == IntPtr.Zero)
        {
            throw new NotSupportedException("OpenAL Soft does not expose ALC_SOFT_loopback.");
        }
        alcLoopbackOpenDevice = Marshal.GetDelegateForFunctionPointer<AlcLoopbackOpenDeviceSoft>(openPointer);
        device = alcLoopbackOpenDevice(null);
        if (device == IntPtr.Zero)
        {
            throw new InvalidOperationException("OpenAL Soft loopback device could not be opened.");
        }

        int[] attributes =
        {
            AlcFrequency, sampleRate,
            AlcFormatChannelsSoft, AlcStereoSoft,
            AlcFormatTypeSoft, AlcShortSoft,
            0
        };
        context = alcCreateContext(device, attributes);
        if (context == IntPtr.Zero)
        {
            throw new InvalidOperationException("OpenAL Soft loopback context could not be created.");
        }

        IntPtr threadContextPointer = alcGetProcAddress(device, "alcSetThreadContext");
        if (threadContextPointer == IntPtr.Zero)
        {
            throw new NotSupportedException(
                "OpenAL Soft does not expose ALC_EXT_thread_local_context; "
                + "the director mixer will not replace the game's audio context.");
        }
        alcSetThreadContext = Marshal.GetDelegateForFunctionPointer<AlcSetThreadContext>(
            threadContextPointer);
        if (!alcSetThreadContext(context))
        {
            throw new InvalidOperationException(
                "OpenAL Soft loopback context could not be activated on the director audio thread.");
        }

        IntPtr renderPointer = alcGetProcAddress(device, "alcRenderSamplesSOFT");
        if (renderPointer == IntPtr.Zero)
        {
            throw new NotSupportedException("OpenAL Soft does not expose alcRenderSamplesSOFT.");
        }
        alcRenderSamples = Marshal.GetDelegateForFunctionPointer<AlcRenderSamplesSoft>(renderPointer);
        alDistanceModel(AlExponentDistanceClamped);
    }

    internal bool HasCurrentContext => alcGetCurrentContext() != IntPtr.Zero;
    internal void CheckError()
    {
        int error = alGetError();
        if (error != 0) throw new InvalidOperationException($"Replay voice OpenAL error: 0x{error:X}.");
    }

    internal uint CreateBuffer(byte[] pcm, int channels, int bits, int rate)
    {
        int format = (channels, bits) switch
        {
            (1, 8) => 0x1100,
            (1, 16) => 0x1101,
            (2, 8) => 0x1102,
            (2, 16) => 0x1103,
            _ => throw new NotSupportedException($"Unsupported OpenAL PCM format: {channels}ch/{bits}bit.")
        };
        alGenBuffers(1, out uint buffer);
        GCHandle pinned = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            alBufferData(buffer, format, pinned.AddrOfPinnedObject(), pcm.Length, rate);
        }
        finally
        {
            pinned.Free();
        }
        return buffer;
    }

    internal uint CreateSource(uint buffer, SoundParams parameters)
        => CreateSource(buffer, parameters.Range, parameters.ReferenceDistance);

    internal uint CreateSource(uint buffer, float range, float referenceDistance)
    {
        float rolloff = (float)-(Math.Log(0.01d) / Math.Log(Math.Max(1.01f, range)));
        float reference = referenceDistance != 3f
            ? referenceDistance
            : (float)Math.Max(3d, Math.Sqrt(Math.Max(1f, range)) - 2d);
        return CreateSource(buffer, range, reference, rolloff);
    }

    internal uint CreateSource(uint buffer, float maxDistance, float referenceDistance, float rolloffFactor)
    {
        alGenSources(1, out uint source);
        alSourceI(source, AlBuffer, checked((int)buffer));
        ConfigureStreamingSource(source, maxDistance, referenceDistance, rolloffFactor);
        return source;
    }

    internal uint CreateStreamingSource(float maxDistance, float referenceDistance, float rolloffFactor)
    {
        alGenSources(1, out uint source);
        ConfigureStreamingSource(source, maxDistance, referenceDistance, rolloffFactor);
        return source;
    }

    internal void ConfigureStreamingSource(
        uint source,
        float maxDistance,
        float referenceDistance,
        float rolloffFactor)
    {
        float max = Math.Max(0.1f, maxDistance);
        float reference = Math.Clamp(referenceDistance, 0.1f, max);
        alSourceF(source, AlRolloffFactor, Math.Clamp(rolloffFactor, 0f, 32f));
        alSourceF(source, AlReferenceDistance, reference);
        alSourceF(source, AlMaxDistance, max);
    }

    internal void SetListener(
        float x,
        float y,
        float z,
        float forwardX,
        float forwardY,
        float forwardZ,
        float upX,
        float upY,
        float upZ)
    {
        alListener3F(AlListenerPosition, x, y, z);
        float[] orientation = { forwardX, forwardY, forwardZ, upX, upY, upZ };
        alListenerFV(AlListenerOrientation, orientation);
    }

    internal void RenderSamples(short[] samples, int frameCount)
    {
        GCHandle pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            alcRenderSamples!(device, pinned.AddrOfPinnedObject(), frameCount);
        }
        finally
        {
            pinned.Free();
        }
    }

    internal void SetSourcePitch(uint source, float value) => alSourceF(source, AlPitch, value);
    internal void SetSourceGain(uint source, float value) => alSourceF(source, AlGain, value);
    internal void SetSourceLooping(uint source, bool value) => alSourceI(source, AlLooping, value ? 1 : 0);
    internal void SetSourceRelative(uint source, bool value) => alSourceI(source, AlSourceRelative, value ? 1 : 0);
    internal void SetSourcePosition(uint source, float x, float y, float z) => alSource3F(source, AlPosition, x, y, z);
    internal void SetSourceSeconds(uint source, float value) => alSourceF(source, AlSecOffset, value);
    internal int GetSourceState(uint source)
    {
        alGetSourceI(source, AlSourceState, out int value);
        return value;
    }
    internal int GetProcessedBufferCount(uint source)
    {
        alGetSourceI(source, AlBuffersProcessed, out int value);
        return Math.Max(0, value);
    }
    internal void QueueSourceBuffer(uint source, uint buffer) => alSourceQueueBuffers(source, 1, ref buffer);
    internal uint UnqueueSourceBuffer(uint source)
    {
        alSourceUnqueueBuffers(source, 1, out uint buffer);
        return buffer;
    }
    internal void PlaySource(uint source) => alSourcePlay(source);
    internal void PauseSource(uint source) => alSourcePause(source);
    internal void StopSource(uint source) => alSourceStop(source);
    internal void DeleteSource(uint source) { if (source != 0) alDeleteSources(1, ref source); }
    internal void DeleteBuffer(uint buffer) { if (buffer != 0) alDeleteBuffers(1, ref buffer); }

    private T Load<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static IntPtr LoadLibrary()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Lib", "openal32.dll"),
                "openal32.dll"
            }
            : new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Lib", "libopenal.so.1"),
                "libopenal.so.1",
                "libopenal.so"
            };
        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }
        throw new DllNotFoundException("OpenAL Soft library was not found.");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (context != IntPtr.Zero)
        {
            if (alcSetThreadContext is not null)
            {
                _ = alcSetThreadContext(IntPtr.Zero);
            }
            alcDestroyContext(context);
            context = IntPtr.Zero;
        }
        if (device != IntPtr.Zero)
        {
            _ = alcCloseDevice(device);
            device = IntPtr.Zero;
        }
        NativeLibrary.Free(library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AlcGetProcAddress(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AlcGetCurrentContext();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AlGetError();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AlcCreateContext(IntPtr device, int[] attributes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool AlcSetThreadContext(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlcDestroyContext(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool AlcCloseDevice(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AlcLoopbackOpenDeviceSoft([MarshalAs(UnmanagedType.LPStr)] string? name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlcRenderSamplesSoft(IntPtr device, IntPtr buffer, int samples);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlGenObject(int count, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlDeleteObject(int count, ref uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlBufferData(uint buffer, int format, IntPtr data, int size, int rate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlSourceI(uint source, int parameter, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlGetSourceI(uint source, int parameter, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlSourceF(uint source, int parameter, float value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlSource3F(uint source, int parameter, float x, float y, float z);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlSourceQueueBuffers(uint source, int count, ref uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlSourceUnqueueBuffers(uint source, int count, out uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlSourceAction(uint source);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlListener3F(int parameter, float x, float y, float z);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlListenerFV(int parameter, float[] values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AlDistanceModel(int model);
}
