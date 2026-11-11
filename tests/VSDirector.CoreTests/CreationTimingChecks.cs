using System.Text.Json;
using System.Text.Json.Nodes;
using VSDirector.Data;

internal static class CreationTimingChecks
{
    public static void Run()
    {
        void Near(double actual, double expected, string message)
        { if (Math.Abs(actual - expected) > 1e-8) throw new Exception($"{message}: {actual} vs {expected}"); }

        foreach (var display in Enum.GetValues<CreationTimeDisplay>())
        {
            foreach (double seconds in new[] { 0, .001, 1d / 30, .125, 10.123456, 86400 })
                Near(DirectorCreationTiming.FromDisplay(DirectorCreationTiming.ToDisplay(seconds, display), display), seconds, "Display conversion changed source time");
            foreach (double desired in new[] { .0001, .01, .05, .13, .8, 2.1, 16, 1000, 138240000 })
            {
                var ticks = DirectorCreationTiming.Ruler(desired, display);
                if (ticks.MinorFrames < 1 || ticks.MajorFrames % ticks.MinorFrames != 0 || ticks.MajorFrames / 30d < desired - 1e-8)
                    throw new Exception("Ruler produced fractional frames, misaligned major ticks or overlapping labels");
            }
        }
        Near(DirectorCreationTiming.Snap(.049), 1d / 30, "Snap did not use 30 Hz");
        Near(DirectorCreationTiming.Snap(.05), 2d / 30, "Half-frame snap is inconsistent");
        Near(DirectorCreationTiming.Step(1, 1), 31d / 30, "Next frame");
        Near(DirectorCreationTiming.Step(1, -1), 29d / 30, "Previous frame");
        Near(DirectorCreationTiming.Step(1.015, 1), 31d / 30, "Next from a subframe");
        Near(DirectorCreationTiming.Step(1.015, -1), 1, "Previous from a subframe");
        Near(DirectorCreationTiming.Step(0, -1), 0, "Step before start");

        var part = new CreationPart { Name = "Body", To = new(16, 16, 16) };
        var model = new CreationModel { Parts = [part] };
        var native = DirectorCreationShape.ImportAnimation(JsonNode.Parse("""
            {"name":"source","quantityframes":60,"keyframes":[
              {"frame":0,"elements":{"Body":{"rotationY":0}}},
              {"frame":30,"elements":{"Body":{"rotationY":60}}}]}
            """)!.AsObject(), model);
        var actor = new CreationSceneObject { ModelId = model.Id };
        var project = new DirectorCreationProject { Duration = 10.123456, LoopEnd = 10.123456, Models = [model], Objects = [actor], Animations = [native],
            Clips = [new() { ObjectId = actor.Id, AnimationId = native.Id, Start = .125, Duration = 1, Speed = 2, SourceStart = .25 }] };
        // Old projects have no display preference and their seconds must remain exact.
        var legacy = JsonSerializer.SerializeToNode(project, CreationJson.Options)!.AsObject(); legacy.Remove("TimeDisplay");
        project = legacy.Deserialize<DirectorCreationProject>(CreationJson.Options)!; project.Validate();
        if (project.TimeDisplay != CreationTimeDisplay.Frames) throw new Exception("Legacy project did not default to frames");
        string root = Path.Combine(Path.GetTempPath(), "vsdirector-timing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new DirectorCreationSession(new(root)); session.Open(project);
            foreach (int fps in new[] { 24, 30, 60, 120 })
            {
                project.Output.Fps = fps;
                foreach (var display in Enum.GetValues<CreationTimeDisplay>())
                {
                    project.TimeDisplay = display;
                    Near(project.Clone().Duration, 10.123456, "Clone rescaled legacy duration");
                    if (project.Clone().TimeDisplay != display) throw new Exception("History clone lost time display");
                    var loaded = CreationJson.Clone(project); loaded.Validate();
                    Near(loaded.Clips[0].Start, .125, "Display/save snapped existing clip");
                    Near(loaded.Animations[0].Duration, 2, "Export FPS changed imported duration");
                    Near(loaded.Animations[0].Tracks[0].Keys.Single(k => Math.Abs(k.Time - 1) < 1e-8).Time, 1, "Imported frame was reinterpreted");
                    var frame = DirectorCreationEvaluator.Evaluate(loaded, .375);
                    Near(frame.Objects[actor.Id].Pose[part.Id].Rotation.Y, 45, "Clip speed/source time changed with output FPS or display");
                    session.Seek(1); session.Play(true); session.Tick(.25); session.Tick(.25); session.Play(false);
                    Near(session.Time, 1.5, "Playback speed changed with export FPS");
                }
                session.ExternalRecording = true; project.Duration = project.LoopEnd = 10.123456;
                session.AdvancePerformance(11);
                Near(project.Duration, 11 + 1d / 30, "Recording extension used export FPS");
                session.ExternalRecording = false;
                project.Duration = project.LoopEnd = 10.123456;
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
