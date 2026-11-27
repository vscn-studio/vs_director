using System.Buffers.Binary;
using VSDirector.Data;

internal static class ReplayAudioFileChecks
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vsdirector-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string monoPath = Path.Combine(directory, "mono.wav");
            string stereoPath = Path.Combine(directory, "stereo.wav");
            WriteWave(monoPath, 48000, Enumerable.Repeat((short)1200, 1920).ToArray());
            short[] stereoSamples = Enumerable.Repeat(new short[] { 10000, 6000 }, 480)
                .SelectMany(frame => frame)
                .ToArray();
            WriteWave(stereoPath, 24000, stereoSamples, channels: 2);

            Guid monoTrackId = Guid.NewGuid();
            Guid stereoTrackId = Guid.NewGuid();
            Guid audioGroupId = Guid.NewGuid();
            DirectorReplayViewportTimeline timeline = new();
            timeline.Tracks.Add(new DirectorReplayElementTrack
            {
                Id = audioGroupId,
                TrackType = DirectorReplayElementTrackType.PresetGroup,
                Name = "Audio"
            });
            timeline.Tracks.Add(new DirectorReplayElementTrack
            {
                Id = monoTrackId,
                TrackType = DirectorReplayElementTrackType.VoiceAudio,
                SpeakerId = "first",
                Name = "Audio track",
                ParentGroupId = audioGroupId
            });
            Guid volumeTrackId = Guid.NewGuid();
            timeline.Tracks.Add(new DirectorReplayElementTrack
            {
                Id = volumeTrackId,
                TrackType = DirectorReplayElementTrackType.VoiceAudio,
                SpeakerId = "first:volume",
                Name = "Volume",
                ParentGroupId = audioGroupId
            });
            timeline.Tracks.Add(new DirectorReplayElementTrack
            {
                Id = stereoTrackId,
                TrackType = DirectorReplayElementTrackType.VoiceAudio,
                SpeakerId = "second",
                Name = "Stereo"
            });
            timeline.Tracks.Add(new DirectorReplayElementTrack
            {
                TrackType = DirectorReplayElementTrackType.VoiceAudio,
                Enabled = false,
                SpeakerId = "disabled"
            });
            DirectorReplayElementKeyframe monoKeyframe = new()
            {
                TrackId = monoTrackId,
                TrackType = DirectorReplayElementTrackType.VoiceAudio,
                TimeSeconds = 1.25,
                AudioAsset = monoPath,
                AudioTrimStartSeconds = 0.01,
                AudioTrimEndSeconds = 0.03
            };
            timeline.ElementKeyframes.Add(monoKeyframe);
            timeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
            {
                TrackId = volumeTrackId,
                TrackType = DirectorReplayElementTrackType.VoiceAudio,
                TimeSeconds = 1.25,
                Volume = 0.5f
            });
            timeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
            {
                TrackId = stereoTrackId,
                TrackType = DirectorReplayElementTrackType.VoiceAudio,
                TimeSeconds = 2d,
                AudioAsset = stereoPath
            });

            IReadOnlyList<DirectorReplayVoiceFrame> frames = DirectorReplayAudioFileFrames.Create(timeline);
            Check.Equal(2, frames.Count);
            Check.NearlyEqual(1.25, frames[0].TimeSeconds);
            Check.NearlyEqual(2d, frames[1].TimeSeconds);
            Check.Equal(DirectorReplayVoiceFrame.SampleRate, (int)(frames[0].Pcm16.Length / 2 * 50));
            Check.Equal(1920, frames[0].Pcm16.Length);
            Check.Equal(1920, frames[1].Pcm16.Length);
            Check.Equal((short)8000, BinaryPrimitives.ReadInt16LittleEndian(frames[1].Pcm16));
            Check.Equal((short)8000, BinaryPrimitives.ReadInt16LittleEndian(frames[1].Pcm16.AsSpan(2)));
            Check.Equal("first:" + monoKeyframe.Id.ToString("N"), frames[0].SourceId);
            Dictionary<string, float[]> waveform = DirectorVoiceWaveform.Read(
                frames, new HashSet<string> { "first" }, 1.25, 1.29, 4);
            Check.Equal(4, waveform["first"].Length);
            Check.NearlyEqual(1200d / short.MaxValue, waveform["first"][0], 0.0001);
            Check.NearlyEqual(1200d / short.MaxValue, waveform["first"][1], 0.0001);
            Check.True(DirectorReplayVoiceTrackPolicy.TryResolveGain(timeline, frames[0], 1.25, out float gain));
            Check.NearlyEqual(.5, gain);

            monoKeyframe.AudioClipGain = .25f;
            var secondClip = timeline.ElementKeyframes.Single(k => k.TrackId == stereoTrackId);
            secondClip.TimeSeconds = monoKeyframe.TimeSeconds;
            Check.True(DirectorReplayVoiceTrackPolicy.MoveFileClips(timeline, monoTrackId, stereoTrackId));
            Check.False(DirectorReplayVoiceTrackPolicy.MoveFileClips(timeline, stereoTrackId, volumeTrackId));
            var merged = DirectorReplayAudioFileFrames.Create(timeline);
            Check.Equal(2, merged.Count);
            Check.True(merged.All(frame => frame.SpeakerId == "second" && frame.TimeSeconds == 1.25));
            var moved = merged.Single(frame => frame.SourceId.EndsWith(monoKeyframe.Id.ToString("N")));
            Check.Equal(.25f, moved.Volume);
            Check.Equal(1f, merged.Single(frame => frame != moved).Volume);
            Check.Equal(1920, moved.Pcm16.Length);
            Check.Equal(.01, monoKeyframe.AudioTrimStartSeconds);
            Check.Equal(monoPath, monoKeyframe.AudioAsset);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteWave(string path, int sampleRate, short[] interleavedSamples, ushort channels = 1)
    {
        byte[] sampleBytes = new byte[interleavedSamples.Length * sizeof(short)];
        for (int index = 0; index < interleavedSamples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(sampleBytes.AsSpan(index * sizeof(short)), interleavedSamples[index]);

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + sampleBytes.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(short));
        writer.Write((ushort)(channels * sizeof(short)));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(sampleBytes.Length);
        writer.Write(sampleBytes);
    }
}
