using VSDirector.Data;

internal static class ServerRecordingChecks
{
    public static void Run()
    {
        var oldConfig = DirectorServerJson.Deserialize<DirectorServerRecordingConfig>(System.Text.Encoding.UTF8.GetBytes("{}"));
        if (oldConfig.ConfirmationAutoStartSeconds != 30) throw new Exception("Existing configuration must default to a 30-second confirmation delay.");
        oldConfig.ConfirmationAutoStartSeconds = 0; oldConfig.Normalize();
        if (DirectorServerJson.Deserialize<DirectorServerRecordingConfig>(DirectorServerJson.Serialize(oldConfig.Copy())).ConfirmationAutoStartSeconds != 0)
            throw new Exception("Manual-only confirmation must survive normalization, copying and serialization.");
        string root = Path.Combine(Path.GetTempPath(), "director-coordination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document = new DirectorReplayDocument
            {
                Id = Guid.NewGuid(), WorldId = "world", Name = "Native coordinated replay", DurationSeconds = 10,
                ServerRecording = new()
                {
                    SessionId = Guid.NewGuid(), PlayerUid = "player",
                    ClockAnchors = [new(0, 12_000_000, 20_000), new(3, 15_000_000, 5_000)]
                }
            };
            var clone = document.DeepClone();
            clone.ServerRecording!.ClockAnchors.Clear();
            if (document.ServerRecording.ClockAnchors.Count != 2) throw new Exception("Clock anchors are aliased.");
            string path = Path.Combine(root, "native.vsdreplay.gz");
            DirectorReplayIO.Save(path, document);
            var loaded = DirectorReplayIO.Load(path);
            if (loaded.SessionMerge is not null || loaded.ServerRecording?.SessionId != document.ServerRecording.SessionId
                || loaded.ServerRecording.PlayerUid != "player"
                || !loaded.ServerRecording.ClockAnchors.SequenceEqual(document.ServerRecording.ClockAnchors))
                throw new Exception("Native recording linkage did not round trip independently of ServerRecording import provenance.");
            document.ServerRecording = null;
            DirectorReplayIO.Save(path, document);
            if (DirectorReplayIO.Load(path).ServerRecording is not null) throw new Exception("Unlinked native replay changed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
