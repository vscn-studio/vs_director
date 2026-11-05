using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VSDirector.Data;
using VSDirector.Recording;
using VSDirector.Runtime;

namespace VSDirector.Replay;

internal readonly record struct DirectorReplayAudioFrame(
    double ReplaySeconds,
    CameraPreset Pose);

internal readonly record struct DirectorReplayAudioExportResult(
    bool Success,
    bool HasAudio,
    long SampleFrames,
    int MissingAssets,
    string Error);

internal sealed class DirectorReplayAudioExportPlan
{
    internal DirectorReplayAudioExportPlan(
        IReadOnlyList<DirectorReplayAudioEvent> events,
        IReadOnlyDictionary<string, DirectorReplayAudioAsset> assets,
        int missingAssets,
        DirectorReplayAudioVisuals audio,
        IReadOnlyList<DirectorReplayVoiceFrame> voiceFrames,
        DirectorReplayViewportTimeline voiceTimeline)
    {
        Events = events;
        Assets = assets;
        MissingAssets = missingAssets;
        Audio = audio;
        VoiceFrames = voiceFrames;
        VoiceTimeline = voiceTimeline;
    }

    internal IReadOnlyList<DirectorReplayAudioEvent> Events { get; }

    internal IReadOnlyDictionary<string, DirectorReplayAudioAsset> Assets { get; }

    internal int MissingAssets { get; }

    internal DirectorReplayAudioVisuals Audio { get; }

    internal IReadOnlyList<DirectorReplayVoiceFrame> VoiceFrames { get; }

    internal DirectorReplayViewportTimeline VoiceTimeline { get; }

    internal bool HasPlayableEvents
        => (Audio.Enabled
                && Audio.MasterVolume > 0f
                && Events.Any(sound => sound.Action == DirectorReplaySoundAction.Play
                    && Assets.ContainsKey(sound.Location)))
            || VoiceFrames.Any(frame => DirectorReplayVoiceTrackPolicy.IsEnabled(
                VoiceTimeline,
                frame));
}

internal enum DirectorReplayAudioBus
{
    Music,
    Ambient,
    Weather,
    Entity,
    Block,
    Ui,
    General
}

internal readonly record struct DirectorReplayAudioEvent(
    double TimeSeconds,
    string Location,
    long InstanceId,
    DirectorReplaySoundAction Action,
    double X,
    double Y,
    double Z,
    bool RelativePosition,
    bool ShouldLoop,
    float PlaybackPosition,
    float Volume,
    float Pitch,
    float PitchOffset,
    float Range,
    float ReferenceDistance,
    int SoundType,
    float CategoryVolume,
    float LowPass,
    float ReverbSeconds,
    DirectorReplayAudioBus Bus);

internal sealed class DirectorReplayAudioAsset
{
    internal DirectorReplayAudioAsset(float[] samples, int channels, int sampleRate)
    {
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    internal float[] Samples { get; }

    internal int Channels { get; }

    internal int SampleRate { get; }

    internal int FrameCount => Samples.Length / Channels;
}

internal static class DirectorReplayAudioExporter
{
    private const double TimeEpsilon = 0.000001d;
    internal static DirectorReplayDocument CreateVoiceSource(DirectorReplayDocument source) => new()
    {
        VoiceFrames = source.Pages?.OpenVoiceView() ?? source.VoiceFrames,
        ViewportTimeline = source.ViewportTimeline.DeepClone()
    };

    internal static IReadOnlyList<string> ExportIndependentVoiceTracks(
        DirectorReplayDocument document,
        double startSeconds,
        double endSeconds,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(startSeconds)
            || !double.IsFinite(endSeconds)
            || startSeconds < 0d
            || endSeconds <= startSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeconds));
        }

        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        DirectorReplayViewportTimeline timeline = document.ViewportTimeline.DeepClone();
        IEnumerable<DirectorReplayVoiceFrame> frames = SelectIndependentVoiceFrames(
            document,
            timeline,
            startSeconds,
            endSeconds, cancellationToken);
        // Retain speaker identities only. PCM remains paged even for exports
        // spanning hours; the cancellable enumeration is repeated per speaker.
        Dictionary<string, string> speakers = new(StringComparer.Ordinal);
        foreach (var frame in frames)
        {
            string key = VoiceTrackKey(frame);
            if (!speakers.ContainsKey(key) || speakers[key] == key)
                speakers[key] = string.IsNullOrWhiteSpace(frame.SpeakerName) ? key : frame.SpeakerName.Trim();
        }
        List<string> paths = new();
        foreach (var speaker in speakers.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string speakerName = speaker.Value;
            string fileName = DirectorRecordingFiles.SanitizeFilePart(
                speakerName,
                "speaker") + "-" + DirectorRecordingFiles.SanitizeFilePart(
                    speaker.Key,
                    "source") + ".wav";
            string path = UniquePath(directory, fileName, paths);
            RenderIndependentVoiceWave(
                frames.Where(frame => VoiceTrackKey(frame) == speaker.Key),
                timeline,
                startSeconds,
                endSeconds,
                path,
                cancellationToken);
            paths.Add(path);
        }

        return paths;
    }

    internal static bool HasIndependentVoiceTracks(
        DirectorReplayDocument document,
        double startSeconds,
        double endSeconds)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(startSeconds)
            || !double.IsFinite(endSeconds)
            || startSeconds < 0d
            || endSeconds <= startSeconds)
        {
            return false;
        }

        return SelectIndependentVoiceFrames(
                document,
                document.ViewportTimeline.DeepClone(),
                startSeconds,
                endSeconds)
            .Any();
    }

    internal static DirectorReplayAudioExportPlan Prepare(
        ClientMain game,
        DirectorReplayDocument document,
        string preferredPlayerUid)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(document);

        DirectorReplayAudioVisuals audio = document.ViewportTimeline.Visuals.Audio.DeepClone();
        DirectorReplayAudioEvent[] events = document.SoundEvents
            .OrderBy(sound => sound.TimeSeconds)
            .Select(sound => PrepareEvent(document, sound, preferredPlayerUid, audio))
            .ToArray();
        Dictionary<string, DirectorReplayAudioAsset> assets =
            new(StringComparer.OrdinalIgnoreCase);
        int missingAssets = 0;
        foreach (string location in events
            .Where(sound => sound.Action == DirectorReplaySoundAction.Play)
            .Select(sound => sound.Location)
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryLoadAsset(game, location, out DirectorReplayAudioAsset? asset)
                && asset is not null)
            {
                assets[location] = asset;
            }
            else
            {
                missingAssets++;
            }
        }

        return new DirectorReplayAudioExportPlan(
            events,
            assets,
            missingAssets,
            audio,
            document.Pages?.OpenVoiceView() ?? document.VoiceFrames ?? new List<DirectorReplayVoiceFrame>(),
            document.ViewportTimeline.DeepClone());
    }

    internal static DirectorReplayAudioExportResult RenderAndMux(
        DirectorReplayAudioExportPlan plan,
        IReadOnlyList<DirectorReplayAudioFrame> frames,
        int framesPerSecond,
        string videoPath,
        string ffmpegPath,
        int audioBitrateKbps,
        DateTimeOffset sourceUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0 || !plan.HasPlayableEvents)
        {
            string noAudioError = plan.Events.Count > 0 && plan.MissingAssets > 0
                ? $"No recorded replay sound assets could be decoded ({plan.MissingAssets} missing)."
                : string.Empty;
            return new DirectorReplayAudioExportResult(
                noAudioError.Length == 0,
                false,
                0L,
                plan.MissingAssets,
                noAudioError);
        }

        string wavePath = videoPath + ".replay-audio.wav";
        string temporaryVideoPath = Path.Combine(
            Path.GetDirectoryName(videoPath)!,
            "." + Path.GetFileNameWithoutExtension(videoPath)
                + ".audio-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            long sampleFrames = RenderWave(
                plan,
                frames,
                framesPerSecond,
                wavePath,
                cancellationToken,
                busFilter: null);
            cancellationToken.ThrowIfCancellationRequested();
            DirectorFfmpegResult mux = DirectorFfmpegExporter.RemuxMp4Async(
                    ffmpegPath,
                    videoPath,
                    wavePath,
                    temporaryVideoPath,
                    DirectorTimecode.FromUtc(sourceUtc, framesPerSecond),
                    audioBitrateKbps,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            if (!mux.Success)
            {
                return new DirectorReplayAudioExportResult(
                    false,
                    true,
                    sampleFrames,
                    plan.MissingAssets,
                    mux.Message);
            }

            File.Move(temporaryVideoPath, videoPath, overwrite: true);
            TryDelete(wavePath);
            return new DirectorReplayAudioExportResult(
                true,
                true,
                sampleFrames,
                plan.MissingAssets,
                string.Empty);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryVideoPath);
            TryDelete(wavePath);
            return new DirectorReplayAudioExportResult(
                false,
                false,
                0L,
                plan.MissingAssets,
                "Replay audio export was canceled.");
        }
        catch (Exception exception)
        {
            TryDelete(temporaryVideoPath);
            TryDelete(wavePath);
            return new DirectorReplayAudioExportResult(
                false,
                false,
                0L,
                plan.MissingAssets,
                exception.GetBaseException().Message);
        }
    }

    private static long RenderWave(
        DirectorReplayAudioExportPlan plan,
        IReadOnlyList<DirectorReplayAudioFrame> frames,
        int framesPerSecond,
        string outputPath,
        CancellationToken cancellationToken,
        DirectorReplayAudioBus? busFilter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using FileStream stream = new(
            outputPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        WriteWaveHeader(writer, dataBytes: 0L);

        DirectorReplayAudioMixer mixer = new(plan, busFilter);
        DirectorReplayVoiceMixer voiceMixer = new(plan.VoiceFrames, plan.VoiceTimeline);
        mixer.InitializeAt(DirectorReplayElementTimeline.SampleFreeze(plan.VoiceTimeline, frames[0].ReplaySeconds).ReplayTimeSeconds);
        int maximumSamples = (int)Math.Ceiling(
            DirectorReplayAudioExportPolicy.SampleRate
            / (double)Math.Clamp(framesPerSecond, 1, 240));
        float[] left = new float[maximumSamples];
        float[] right = new float[maximumSamples];
        double lastReplayRate = 1d;
        bool previouslyFrozen = false;
        double previousContentTime = double.NegativeInfinity;
        long totalSampleFrames = 0L;
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectorReplayAudioFrame frame = frames[frameIndex];
            var content = DirectorReplayElementTimeline.SampleFreeze(plan.VoiceTimeline, frame.ReplaySeconds);
            if (previouslyFrozen && !content.Frozen || content.ReplayTimeSeconds < previousContentTime)
            {
                // Unfreeze jumps to the logical timeline. Rebuild audio at
                // that destination rather than squeezing skipped sounds into
                // the preceding video frame.
                mixer = new DirectorReplayAudioMixer(plan, busFilter);
                mixer.InitializeAt(content.ReplayTimeSeconds);
                voiceMixer = new DirectorReplayVoiceMixer(plan.VoiceFrames, plan.VoiceTimeline);
            }
            previouslyFrozen = content.Frozen;
            previousContentTime = content.ReplayTimeSeconds;
            double nextReplaySeconds = frameIndex + 1 < frames.Count
                ? DirectorReplayElementTimeline.SampleFreeze(plan.VoiceTimeline, frames[frameIndex + 1].ReplaySeconds).ReplayTimeSeconds
                : content.ReplayTimeSeconds;
            if (content.Frozen && frameIndex + 1 < frames.Count
                && !DirectorReplayElementTimeline.SampleFreeze(plan.VoiceTimeline, frames[frameIndex + 1].ReplaySeconds).Frozen)
                nextReplaySeconds = content.ReplayTimeSeconds;
            double replayRate;
            if (frameIndex + 1 < frames.Count)
            {
                replayRate = DirectorReplayAudioExportPolicy.ResolveReplayRate(
                    content.ReplayTimeSeconds,
                    nextReplaySeconds,
                    framesPerSecond,
                    lastReplayRate);
                lastReplayRate = replayRate;
            }
            else
            {
                // The final video frame is an inclusive end pose. Hold it for
                // one output frame without advancing sounds past the selected
                // replay range.
                replayRate = 0d;
            }

            int sampleCount = DirectorReplayAudioExportPolicy.SamplesInFrame(
                frameIndex,
                framesPerSecond);
            mixer.MixFrame(
                frame.Pose,
                content.ReplayTimeSeconds,
                nextReplaySeconds,
                replayRate,
                left,
                right,
                sampleCount);
            voiceMixer.MixFrame(
                frame.Pose,
                content.ReplayTimeSeconds,
                nextReplaySeconds,
                replayRate,
                left,
                right,
                sampleCount);
            WriteSamples(writer, left, right, sampleCount);
            totalSampleFrames += sampleCount;
        }

        long dataBytes = checked(totalSampleFrames
            * DirectorReplayAudioExportPolicy.Channels
            * sizeof(short));
        if (dataBytes > uint.MaxValue - 36L)
        {
            throw new NotSupportedException("Replay audio exceeds the WAV 4 GB size limit.");
        }
        stream.Position = 0L;
        WriteWaveHeader(writer, dataBytes);
        writer.Flush();
        return totalSampleFrames;
    }

    private static void RenderIndependentVoiceWave(
        IEnumerable<DirectorReplayVoiceFrame> frames,
        DirectorReplayViewportTimeline timeline,
        double startSeconds,
        double endSeconds,
        string outputPath,
        CancellationToken cancellationToken)
    {
        long totalSamples = checked((long)Math.Ceiling(
            (endSeconds - startSeconds) * DirectorReplayVoiceFrame.SampleRate));
        long dataBytes = checked(totalSamples * sizeof(short));
        if (dataBytes > uint.MaxValue - 36L)
        {
            throw new NotSupportedException("Replay voice track exceeds the WAV 4 GB size limit.");
        }

        using FileStream stream = new(
            outputPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        WriteWaveHeader(writer, dataBytes, channels: 1);
        stream.SetLength(44L + dataBytes);

        short[] output = new short[DirectorReplayVoiceFrame.SampleRate];
        foreach (DirectorReplayVoiceFrame frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            short[] source = DirectorVoicePcm.FromLittleEndianBytes(frame.Pcm16);
            long sourceStart = Math.Max(0L, (long)Math.Ceiling(
                (startSeconds - frame.TimeSeconds) * DirectorReplayVoiceFrame.SampleRate));
            long destinationStart = Math.Max(0L, (long)Math.Ceiling(
                (frame.TimeSeconds - startSeconds) * DirectorReplayVoiceFrame.SampleRate));
            long length = Math.Min(
                Math.Max(0L, source.Length - sourceStart),
                Math.Max(0L, totalSamples - destinationStart));
            long copied = 0L;
            while (copied < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(output.Length, length - copied);
                for (int index = 0; index < count; index++)
                {
                    double time = startSeconds + (destinationStart + copied + index)
                        / (double)DirectorReplayVoiceFrame.SampleRate;
                    if (DirectorReplayVoiceTrackPolicy.TryResolveGain(
                            timeline,
                            frame,
                            time,
                            out float gain))
                    {
                        int sourceIndex = checked((int)(sourceStart + copied + index));
                        int mixed = output[index] + (int)Math.Round(
                            source[sourceIndex] * frame.Volume * gain,
                            MidpointRounding.AwayFromZero);
                        output[index] = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
                    }
                }
                stream.Position = 44L + (destinationStart + copied) * sizeof(short);
                writer.Write(MemoryMarshal.AsBytes(output.AsSpan(0, count)));
                Array.Clear(output, 0, count);
                copied += count;
            }
        }
        writer.Flush();
    }

    private static void WriteWaveHeader(BinaryWriter writer, long dataBytes, int channels = DirectorReplayAudioExportPolicy.Channels)
    {
        writer.Write("RIFF"u8.ToArray());
        writer.Write(checked((uint)(36L + dataBytes)));
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write(checked((ushort)channels));
        writer.Write((uint)DirectorReplayAudioExportPolicy.SampleRate);
        writer.Write((uint)(DirectorReplayAudioExportPolicy.SampleRate
            * channels
            * sizeof(short)));
        writer.Write(checked((ushort)(channels * sizeof(short))));
        writer.Write((ushort)16);
        writer.Write("data"u8.ToArray());
        writer.Write(checked((uint)dataBytes));
    }

    private static bool IsWithinRange(
        DirectorReplayVoiceFrame frame,
        double startSeconds,
        double endSeconds)
    {
        double duration = frame.Pcm16.Length / (double)(sizeof(short) * DirectorReplayVoiceFrame.SampleRate);
        return frame.TimeSeconds < endSeconds && frame.TimeSeconds + duration > startSeconds;
    }

    private static string VoiceTrackKey(DirectorReplayVoiceFrame frame)
    {
        string speakerId = frame.SpeakerId?.Trim() ?? string.Empty;
        return speakerId.Length > 0 ? speakerId : frame.SourceId?.Trim() ?? string.Empty;
    }

    private static IEnumerable<DirectorReplayVoiceFrame> SelectIndependentVoiceFrames(
        DirectorReplayDocument document,
        DirectorReplayViewportTimeline timeline,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken = default)
    {
        foreach (var frame in document.VoiceFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.TimeSeconds >= endSeconds) yield break;
            if (IsWithinRange(frame, startSeconds, endSeconds) && DirectorReplayVoiceTrackPolicy.IsEnabled(timeline, frame)) yield return frame;
        }
    }

    private static string UniquePath(string directory, string fileName, IReadOnlyCollection<string> reservedPaths)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 1; ; index++)
        {
            string name = index == 1 ? fileName : $"{stem}-{index}{extension}";
            string path = Path.Combine(directory, name);
            if (!File.Exists(path) && !reservedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return path;
            }
        }
    }

    private static void WriteSamples(
        BinaryWriter writer,
        float[] left,
        float[] right,
        int sampleCount)
    {
        for (int index = 0; index < sampleCount; index++)
        {
            writer.Write(ToPcm16(left[index]));
            writer.Write(ToPcm16(right[index]));
        }
    }

    private static short ToPcm16(float sample)
        => (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);

    private static DirectorReplayAudioEvent PrepareEvent(
        DirectorReplayDocument document,
        DirectorReplaySoundEvent sound,
        string preferredPlayerUid,
        DirectorReplayAudioVisuals audio)
    {
        EnumSoundType soundType = Enum.IsDefined(typeof(EnumSoundType), sound.SoundType)
            ? (EnumSoundType)sound.SoundType
            : EnumSoundType.Sound;
        double x = sound.X;
        double y = sound.Y;
        double z = sound.Z;
        bool relative = sound.RelativePosition;
        if (DirectorSoundSpatialPolicy.AnchorRelativeSoundToWorld(relative, soundType)
            && DirectorReplayAnchorSelector.TrySelect(
                document,
                sound.TimeSeconds,
                preferredPlayerUid,
                out DirectorReplayAnchorPose anchor))
        {
            x = anchor.X;
            y = DirectorChunkWindow.ToAbsoluteY(anchor.Y, anchor.Dimension);
            z = anchor.Z;
            relative = false;
        }

        DirectorReplayAudioBus bus = ResolveBus(soundType);
        return new DirectorReplayAudioEvent(
            Math.Max(0d, sound.TimeSeconds),
            sound.Location?.Trim() ?? string.Empty,
            sound.InstanceId,
            sound.Action,
            x,
            y,
            z,
            relative,
            sound.ShouldLoop,
            Math.Max(0f, sound.PlaybackPosition),
            Math.Clamp(sound.Volume, 0f, 4f),
            Math.Clamp(sound.Pitch, 0.01f, 16f),
            Math.Clamp(sound.PitchOffset, -15.99f, 15.99f),
            Math.Max(1f, sound.Range),
            Math.Max(0.1f, sound.ReferenceDistance),
            (int)soundType,
            GlobalVolume(soundType) * BusGain(bus, audio) * audio.MasterVolume,
            Math.Clamp(sound.LowPassFilter, 0f, 4f),
            Math.Clamp(sound.ReverbDecayTime, 0f, 120f),
            bus);
    }

    private static DirectorReplayAudioBus ResolveBus(EnumSoundType type)
    {
        string name = type.ToString();
        if (name.Contains("music", StringComparison.OrdinalIgnoreCase)) return DirectorReplayAudioBus.Music;
        if (name.Contains("ambient", StringComparison.OrdinalIgnoreCase)) return DirectorReplayAudioBus.Ambient;
        if (name.Contains("weather", StringComparison.OrdinalIgnoreCase)) return DirectorReplayAudioBus.Weather;
        if (name.Contains("entity", StringComparison.OrdinalIgnoreCase)) return DirectorReplayAudioBus.Entity;
        if (name.Contains("block", StringComparison.OrdinalIgnoreCase)) return DirectorReplayAudioBus.Block;
        if (name.Contains("ui", StringComparison.OrdinalIgnoreCase)) return DirectorReplayAudioBus.Ui;
        return DirectorReplayAudioBus.General;
    }

    private static float BusGain(
        DirectorReplayAudioBus bus,
        DirectorReplayAudioVisuals audio)
        => 1f;

    private static bool TryLoadAsset(
        ClientMain game,
        string location,
        out DirectorReplayAudioAsset? asset)
    {
        asset = null;
        try
        {
            AssetLocation requested = new(location);
            AssetLocation exact = requested.Clone().WithPathAppendixOnce(".ogg");
            AudioData? data = null;
            if (!ScreenManager.soundAudioData.TryGetValue(exact, out data))
            {
                AssetLocation resolved = game.ResolveSoundPath(requested).Clone()
                    .WithPathAppendixOnce(".ogg");
                ScreenManager.soundAudioData.TryGetValue(resolved, out data);
            }
            if (data is not AudioMetaData sample)
            {
                return false;
            }
            if (sample.Pcm is not { Length: > 0 } && !sample.Load())
            {
                return false;
            }
            if (sample.Pcm is not { Length: > 0 }
                || sample.Channels is < 1 or > 2
                || sample.BitsPerSample is not (8 or 16)
                || sample.Rate <= 0)
            {
                return false;
            }

            asset = new DirectorReplayAudioAsset(
                ConvertPcm(sample.Pcm, sample.Channels, sample.BitsPerSample),
                sample.Channels,
                sample.Rate);
            return asset.FrameCount > 0;
        }
        catch
        {
            asset = null;
            return false;
        }
    }

    private static float[] ConvertPcm(byte[] pcm, int channels, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;
        int sampleCount = pcm.Length / bytesPerSample;
        sampleCount -= sampleCount % channels;
        float[] samples = new float[sampleCount];
        if (bitsPerSample == 8)
        {
            for (int index = 0; index < sampleCount; index++)
            {
                samples[index] = (pcm[index] - 128) / 128f;
            }
            return samples;
        }

        for (int index = 0; index < sampleCount; index++)
        {
            int offset = index * 2;
            short value = (short)(pcm[offset] | pcm[offset + 1] << 8);
            samples[index] = value / 32768f;
        }
        return samples;
    }

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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class DirectorReplayAudioMixer
    {
        private readonly DirectorReplayAudioExportPlan plan;
        private readonly DirectorReplayAudioBus? busFilter;
        private readonly Dictionary<long, Voice> identifiedVoices = new();
        private readonly List<Voice> voices = new();
        private int eventCursor;
        private long nextAnonymousId = -1L;

        internal DirectorReplayAudioMixer(
            DirectorReplayAudioExportPlan plan,
            DirectorReplayAudioBus? busFilter = null)
        {
            this.plan = plan;
            this.busFilter = busFilter;
        }

        internal void InitializeAt(double replaySeconds)
        {
            ApplyEventsThrough(replaySeconds);
            foreach (Voice voice in voices.ToArray())
            {
                if (voice.Paused)
                {
                    continue;
                }
                double elapsed = Math.Max(0d, replaySeconds - voice.LastEventSeconds);
                AdvanceVoice(voice, elapsed * EffectivePitch(voice.Event) * voice.Asset.SampleRate);
            }
            RemoveFinishedVoices();
        }

        internal void ApplyEventsThrough(double replaySeconds)
        {
            while (eventCursor < plan.Events.Count
                && plan.Events[eventCursor].TimeSeconds <= replaySeconds + TimeEpsilon)
            {
                ApplyEvent(plan.Events[eventCursor++]);
            }
        }

        internal void MixFrame(
            CameraPreset pose,
            double currentReplaySeconds,
            double nextReplaySeconds,
            double replayRate,
            float[] left,
            float[] right,
            int sampleCount)
        {
            Array.Clear(left, 0, sampleCount);
            Array.Clear(right, 0, sampleCount);
            if (!double.IsFinite(replayRate) || replayRate <= TimeEpsilon)
            {
                ApplyEventsThrough(currentReplaySeconds);
                return;
            }

            int sampleOffset = 0;
            double replaySpan = Math.Max(TimeEpsilon, nextReplaySeconds - currentReplaySeconds);
            while (eventCursor < plan.Events.Count
                && plan.Events[eventCursor].TimeSeconds <= nextReplaySeconds + TimeEpsilon)
            {
                DirectorReplayAudioEvent sound = plan.Events[eventCursor++];
                int eventOffset = sound.TimeSeconds <= currentReplaySeconds + TimeEpsilon
                    ? sampleOffset
                    : (int)Math.Clamp(
                        Math.Round(
                            (sound.TimeSeconds - currentReplaySeconds)
                            / replaySpan
                            * sampleCount),
                        sampleOffset,
                        sampleCount);
                MixRange(pose, replayRate, left, right, sampleOffset, eventOffset);
                ApplyEvent(sound);
                sampleOffset = eventOffset;
            }
            MixRange(pose, replayRate, left, right, sampleOffset, sampleCount);
        }

        private void MixRange(
            CameraPreset pose,
            double replayRate,
            float[] left,
            float[] right,
            int startSample,
            int endSample)
        {
            int sampleCount = Math.Max(0, endSample - startSample);
            if (sampleCount == 0)
            {
                return;
            }
            foreach (Voice voice in voices)
            {
                if (voice.Paused
                    || voice.Finished
                    || busFilter is DirectorReplayAudioBus bus
                        && voice.Event.Bus != bus)
                {
                    continue;
                }
                SpatialGains gains = ResolveGains(voice.Event, pose);
                if (gains.Left <= 0f && gains.Right <= 0f)
                {
                    AdvanceVoice(
                        voice,
                        sampleCount * SourceStep(voice, replayRate));
                    continue;
                }

                double step = SourceStep(voice, replayRate);
                MixVoice(voice, gains, step, left, right, startSample, endSample);
            }
            RemoveFinishedVoices();
        }

        private Voice? ApplyEvent(DirectorReplayAudioEvent sound)
        {
            if (sound.Action == DirectorReplaySoundAction.Play)
            {
                if (!plan.Assets.TryGetValue(sound.Location, out DirectorReplayAudioAsset? asset))
                {
                    return null;
                }
                long key = sound.InstanceId > 0L ? sound.InstanceId : nextAnonymousId--;
                if (sound.InstanceId > 0L && identifiedVoices.Remove(key, out Voice? previous))
                {
                    previous.Finished = true;
                    voices.Remove(previous);
                }
                Voice voice = new(key, asset, sound);
                voices.Add(voice);
                if (sound.InstanceId > 0L)
                {
                    identifiedVoices[key] = voice;
                }
                return voice;
            }

            if (sound.InstanceId <= 0L
                || !identifiedVoices.TryGetValue(sound.InstanceId, out Voice? active))
            {
                return null;
            }
            if (sound.Action == DirectorReplaySoundAction.Stop)
            {
                active.Finished = true;
                identifiedVoices.Remove(sound.InstanceId);
                voices.Remove(active);
                return null;
            }

            if (plan.Assets.TryGetValue(sound.Location, out DirectorReplayAudioAsset? nextAsset))
            {
                active.Asset = nextAsset;
            }
            active.Event = sound;
            active.PositionFrames = sound.PlaybackPosition * active.Asset.SampleRate;
            active.LastEventSeconds = sound.TimeSeconds;
            active.Paused = sound.Action == DirectorReplaySoundAction.Pause;
            active.Finished = false;
            NormalizePosition(active);
            return active;
        }

        private void MixVoice(
            Voice voice,
            SpatialGains gains,
            double step,
            float[] left,
            float[] right,
            int startSample,
            int endSample)
        {
            DirectorReplayAudioAsset asset = voice.Asset;
            for (int index = startSample; index < endSample; index++)
            {
                if (!NormalizePosition(voice))
                {
                    break;
                }
                double position = voice.PositionFrames;
                int firstFrame = (int)Math.Floor(position);
                int secondFrame = firstFrame + 1;
                if (secondFrame >= asset.FrameCount)
                {
                    secondFrame = voice.Event.ShouldLoop ? 0 : firstFrame;
                }
                float fraction = (float)(position - firstFrame);
                if (gains.PreserveStereo && asset.Channels == 2)
                {
                    float sourceLeft = Interpolate(
                        asset.Samples[firstFrame * 2],
                        asset.Samples[secondFrame * 2],
                        fraction);
                    float sourceRight = Interpolate(
                        asset.Samples[firstFrame * 2 + 1],
                        asset.Samples[secondFrame * 2 + 1],
                        fraction);
                    ProcessVoiceSample(
                        voice,
                        sourceLeft,
                        sourceRight,
                        out float processedLeft,
                        out float processedRight);
                    left[index] += processedLeft * gains.Left;
                    right[index] += processedRight * gains.Right;
                }
                else
                {
                    float first = MonoSample(asset, firstFrame);
                    float second = MonoSample(asset, secondFrame);
                    float sample = Interpolate(first, second, fraction);
                    ProcessVoiceSample(
                        voice,
                        sample,
                        sample,
                        out float processedLeft,
                        out float processedRight);
                    left[index] += processedLeft * gains.Left;
                    right[index] += processedRight * gains.Right;
                }
                voice.PositionFrames += step;
            }
        }

        private static float MonoSample(DirectorReplayAudioAsset asset, int frame)
            => asset.Channels == 1
                ? asset.Samples[frame]
                : (asset.Samples[frame * 2] + asset.Samples[frame * 2 + 1]) * 0.5f;

        private static float Interpolate(float first, float second, float amount)
            => first + (second - first) * amount;

        private double SourceStep(Voice voice, double replayRate)
            => voice.Asset.SampleRate
                / (double)DirectorReplayAudioExportPolicy.SampleRate
                * EffectivePitch(voice.Event)
                * (plan.Audio.PreservePitchWithReplaySpeed ? 1d : replayRate);

        private static double EffectivePitch(DirectorReplayAudioEvent sound)
            => Math.Clamp(sound.Pitch + sound.PitchOffset, 0.01f, 32f);

        private static void ProcessVoiceSample(
            Voice voice,
            float sourceLeft,
            float sourceRight,
            out float outputLeft,
            out float outputRight)
        {
            float lowPassAmount = Math.Clamp(voice.Event.LowPass / 4f, 0f, 1f);
            float coefficient = 1f - lowPassAmount * 0.94f;
            voice.FilteredLeft += (sourceLeft - voice.FilteredLeft) * coefficient;
            voice.FilteredRight += (sourceRight - voice.FilteredRight) * coefficient;
            outputLeft = voice.FilteredLeft;
            outputRight = voice.FilteredRight;

            float reverbWet = Math.Clamp(voice.Event.ReverbSeconds / 8f, 0f, 0.65f);
            if (reverbWet <= 0.0001f)
            {
                return;
            }
            voice.EnsureReverbDelay();
            int delayIndex = voice.ReverbDelayIndex;
            float delayedLeft = voice.ReverbDelayLeft![delayIndex];
            float delayedRight = voice.ReverbDelayRight![delayIndex];
            voice.ReverbDelayLeft[delayIndex] = outputLeft + delayedLeft * reverbWet * 0.55f;
            voice.ReverbDelayRight[delayIndex] = outputRight + delayedRight * reverbWet * 0.55f;
            voice.ReverbDelayIndex = (delayIndex + 1) % voice.ReverbDelayLeft.Length;
            outputLeft += delayedLeft * reverbWet;
            outputRight += delayedRight * reverbWet;
        }

        private static void AdvanceVoice(Voice voice, double sourceFrames)
        {
            if (!double.IsFinite(sourceFrames) || sourceFrames <= 0d)
            {
                return;
            }
            voice.PositionFrames += sourceFrames;
            NormalizePosition(voice);
        }

        private static bool NormalizePosition(Voice voice)
        {
            int frameCount = voice.Asset.FrameCount;
            if (frameCount <= 0)
            {
                voice.Finished = true;
                return false;
            }
            if (voice.Event.ShouldLoop)
            {
                voice.PositionFrames %= frameCount;
                if (voice.PositionFrames < 0d)
                {
                    voice.PositionFrames += frameCount;
                }
                return true;
            }
            if (voice.PositionFrames >= frameCount)
            {
                voice.Finished = true;
                return false;
            }
            voice.PositionFrames = Math.Max(0d, voice.PositionFrames);
            return true;
        }

        private void RemoveFinishedVoices()
        {
            for (int index = voices.Count - 1; index >= 0; index--)
            {
                Voice voice = voices[index];
                if (!voice.Finished)
                {
                    continue;
                }
                voices.RemoveAt(index);
                if (voice.Key > 0L)
                {
                    identifiedVoices.Remove(voice.Key);
                }
            }
        }

        private SpatialGains ResolveGains(
            DirectorReplayAudioEvent sound,
            CameraPreset pose)
        {
            float gain = Math.Clamp(sound.Volume * sound.CategoryVolume, 0f, 4f);
            EnumSoundType soundType = (EnumSoundType)sound.SoundType;
            bool preserveStereo = !plan.Audio.SpatialAudio
                || sound.RelativePosition
                    && DirectorSoundSpatialPolicy.KeepListenerRelative(soundType);
            if (preserveStereo)
            {
                return new SpatialGains(gain, gain, true);
            }

            double listenerY = pose.Y + pose.Dimension * DirectorChunkWindow.DimensionHeight;
            double dx = sound.X - pose.X;
            double dy = sound.Y - listenerY;
            double dz = sound.Z - pose.Z;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!double.IsFinite(distance) || distance >= sound.Range)
            {
                return default;
            }
            double reference = Math.Clamp(sound.ReferenceDistance, 0.1f, sound.Range);
            double attenuation = distance <= reference
                ? 1d
                : Math.Pow(
                    Math.Clamp((sound.Range - distance) / Math.Max(0.001d, sound.Range - reference), 0d, 1d),
                    1.35d);
            double pan = 0d;
            if (distance > 0.0001d)
            {
                Vec3f right = CameraRight(pose);
                pan = Math.Clamp(
                    (dx * right.X + dy * right.Y + dz * right.Z) / distance,
                    -1d,
                    1d);
            }
            float left = gain * (float)(attenuation * Math.Sqrt(1d - pan));
            float rightGain = gain * (float)(attenuation * Math.Sqrt(1d + pan));
            return new SpatialGains(left, rightGain, false);
        }

        private static Vec3f CameraRight(CameraPreset pose)
        {
            float yaw = pose.Yaw * GameMath.DEG2RAD;
            float pitch = pose.Pitch * GameMath.DEG2RAD;
            float roll = pose.Roll * GameMath.DEG2RAD;
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
            right.Set(
                right.X * cosRoll - up.X * sinRoll,
                right.Y * cosRoll - up.Y * sinRoll,
                right.Z * cosRoll - up.Z * sinRoll);
            return right;
        }

        private sealed class Voice
        {
            internal Voice(
                long key,
                DirectorReplayAudioAsset asset,
                DirectorReplayAudioEvent sound)
            {
                Key = key;
                Asset = asset;
                Event = sound;
                PositionFrames = sound.PlaybackPosition * asset.SampleRate;
                LastEventSeconds = sound.TimeSeconds;
                NormalizePosition(this);
            }

            internal long Key { get; }

            internal DirectorReplayAudioAsset Asset { get; set; }

            internal DirectorReplayAudioEvent Event { get; set; }

            internal double PositionFrames { get; set; }

            internal double LastEventSeconds { get; set; }

            internal bool Paused { get; set; }

            internal bool Finished { get; set; }

            internal float FilteredLeft { get; set; }

            internal float FilteredRight { get; set; }

            internal float[]? ReverbDelayLeft { get; private set; }

            internal float[]? ReverbDelayRight { get; private set; }

            internal int ReverbDelayIndex { get; set; }

            internal void EnsureReverbDelay()
            {
                ReverbDelayLeft ??= new float[3840];
                ReverbDelayRight ??= new float[3840];
            }
        }

        private readonly record struct SpatialGains(float Left, float Right, bool PreserveStereo);
    }

    private sealed class DirectorReplayVoiceMixer
    {
        private readonly IReadOnlyList<DirectorReplayVoiceFrame> frames;
        private readonly DirectorReplayViewportTimeline timeline;
        private readonly List<ActiveVoice> active = new();
        private int cursor;

        internal DirectorReplayVoiceMixer(
            IReadOnlyList<DirectorReplayVoiceFrame> frames,
            DirectorReplayViewportTimeline timeline)
        {
            this.frames = frames;
            this.timeline = timeline;
        }

        internal void MixFrame(
            CameraPreset pose,
            double currentSeconds,
            double nextSeconds,
            double replayRate,
            float[] left,
            float[] right,
            int sampleCount)
        {
            if (sampleCount <= 0 || !double.IsFinite(replayRate) || replayRate <= 0d)
            {
                return;
            }
            while (cursor < frames.Count && frames[cursor].TimeSeconds <= nextSeconds + 0.000001d)
            {
                DirectorReplayVoiceFrame frame = frames[cursor++];
                double frameEnd = frame.TimeSeconds + frame.Pcm16.Length / (double)(sizeof(short) * DirectorReplayVoiceFrame.SampleRate);
                if (frameEnd < currentSeconds) continue;
                short[] samples = DirectorVoicePcm.FromLittleEndianBytes(frame.Pcm16);
                if (samples.Length > 0)
                {
                    active.Add(new ActiveVoice(frame, samples));
                }
            }

            double span = Math.Max(0.000001d, nextSeconds - currentSeconds);
            for (int index = active.Count - 1; index >= 0; index--)
            {
                ActiveVoice voice = active[index];
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    double time = currentSeconds + (sample + 0.5d) / sampleCount * span;
                    double sourcePosition = (time - voice.Frame.TimeSeconds)
                        * DirectorReplayVoiceFrame.SampleRate;
                    if (sourcePosition < 0d || sourcePosition >= voice.Samples.Length)
                    {
                        continue;
                    }
                    int sourceIndex = Math.Min(voice.Samples.Length - 1, (int)sourcePosition);
                    float sampleValue = voice.Samples[sourceIndex] / (float)short.MaxValue;
                    if (!DirectorReplayVoiceTrackPolicy.TryResolveGain(
                            timeline,
                            voice.Frame,
                            time,
                            out float trackGain))
                    {
                        continue;
                    }
                    (float gainLeft, float gainRight) = SpatialGain(voice.Frame, pose, trackGain);
                    left[sample] += sampleValue * gainLeft;
                    right[sample] += sampleValue * gainRight;
                }
                double end = voice.Frame.TimeSeconds
                    + voice.Samples.Length / (double)DirectorReplayVoiceFrame.SampleRate;
                if (end < currentSeconds - span)
                {
                    active.RemoveAt(index);
                }
            }
        }

        private static (float Left, float Right) SpatialGain(
            DirectorReplayVoiceFrame frame,
            CameraPreset pose,
            float trackGain)
        {
            if (!frame.Spatialized)
            {
                float directGain = frame.Volume * trackGain * 0.70710677f;
                return (directGain, directGain);
            }
            double listenerY = pose.Y + pose.Dimension * DirectorChunkWindow.DimensionHeight;
            double dx = frame.X - pose.X;
            double dy = frame.Y - listenerY;
            double dz = frame.Z - pose.Z;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!double.IsFinite(distance) || frame.Dimension != pose.Dimension)
            {
                return default;
            }
            double referenceDistance = Math.Clamp(frame.ReferenceDistance, 0.1f, Math.Max(0.1f, frame.MaxDistance));
            double maxDistance = Math.Max(referenceDistance, frame.MaxDistance);
            double attenuation = Math.Pow(
                Math.Clamp(distance, referenceDistance, maxDistance) / referenceDistance,
                -Math.Clamp(frame.RolloffFactor, 0f, 32f));
            float yaw = pose.Yaw * GameMath.DEG2RAD;
            float rightX = GameMath.Cos(yaw);
            float rightZ = -GameMath.Sin(yaw);
            double pan = Math.Clamp((dx * rightX + dz * rightZ) / Math.Max(1d, distance), -1d, 1d);
            float gain = frame.Volume * trackGain * (float)attenuation;
            return (gain * (float)Math.Sqrt(1d - pan), gain * (float)Math.Sqrt(1d + pan));
        }

        private sealed record ActiveVoice(DirectorReplayVoiceFrame Frame, short[] Samples);
    }
}
