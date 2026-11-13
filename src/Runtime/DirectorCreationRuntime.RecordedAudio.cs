using VSDirector.Data;
using VSDirector.Replay;
using Vintagestory.Client.NoObf;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private readonly Dictionary<string, RecordedWorldAudio> creationAudio = new(StringComparer.OrdinalIgnoreCase);

    private sealed class RecordedWorldAudio(DirectorReplayAudioExportPlan plan)
    {
        internal DirectorReplayAudioExportPlan Plan { get; } = plan;
        internal List<(int Index, DirectorReplayAudioFrame Frame)> Frames { get; } = new();
        internal Guid LastClip;
        internal int LastFrame = -2;
        internal double LastSource = double.NegativeInfinity;
    }

    internal bool HasSceneAudioSource => Session.Project is { } project
        && (project.BackgroundReplayId != Guid.Empty || RecordedWorldAt(Session.Time) is not null);

    private void CaptureCreationWorldAudio(CameraPreset camera)
    {
        if (Session.Project is not { } project || !project.Output.SceneAudio
            || RecordedWorldAt(Session.Time) is not { } world
            || !DirectorCreationEvaluator.ClipActive(world.Clip, Session.Time, project.Duration)
            || creationReplay?.ActiveDocument is not { } document) return;

        string source = world.Performance.WorldSource;
        if (!creationAudio.TryGetValue(source, out var audio))
        {
            audio = new(DirectorReplayAudioExporter.Prepare((ClientMain)api.World, document,
                api.World.Player?.PlayerUID ?? ""));
            creationAudio.Add(source, audio);
        }
        double remaining = Math.Max(0, world.Clip.Start + world.Clip.Duration - Session.Time);
        double next = Math.Min(world.Performance.BackgroundStart + world.Performance.Action.DurationSeconds,
            world.SourceTime + world.Clip.Speed * Math.Min(1d / project.Output.Fps, remaining));
        audio.Frames.Add((exportFrame, new(world.SourceTime, camera)
        {
            NextReplaySeconds = next,
            Discontinuity = audio.LastFrame + 1 != exportFrame || audio.LastClip != world.Clip.Id
                || audio.LastSource > world.SourceTime
        }));
        audio.LastFrame = exportFrame;
        audio.LastClip = world.Clip.Id;
        audio.LastSource = world.SourceTime;
    }

    private (DirectorReplayAudioExportPlan Plan, DirectorReplayAudioFrame[] Frames)[] SnapshotCreationWorldAudio(int frameCount)
        => creationAudio.Values.Select(audio =>
        {
            // Each source occupies its original project frames. Blank spans
            // are silent, so moving a take never pulls its sounds to time zero.
            var frames = Enumerable.Repeat(new DirectorReplayAudioFrame(0, new CameraPreset())
                { Muted = true }, frameCount).ToArray();
            foreach (var sample in audio.Frames)
                if (sample.Index >= 0 && frameCount > sample.Index) frames[sample.Index] = sample.Frame;
            return (audio.Plan, frames);
        }).ToArray();
}
