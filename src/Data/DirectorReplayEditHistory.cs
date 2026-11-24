using System.Text.Json;

namespace VSDirector.Data;

public sealed record DirectorReplayEditSnapshot(DirectorReplayTimelineEditDocument Edits, CameraPreset? Camera);

/// <summary>Bounded, isolated edit snapshots. Failed saves leave both stacks intact.</summary>
public sealed class DirectorReplayEditHistory
{
    private readonly List<string> undo = new(), redo = new();
    private Guid replayId;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Select(Guid id)
    {
        if (replayId == id) return;
        replayId = id;
        undo.Clear(); redo.Clear();
    }

    public void Record(DirectorReplayEditSnapshot before, DirectorReplayEditSnapshot after)
    {
        Select(before.Edits.ReplayId);
        if (JsonSerializer.Serialize(before.Edits) == JsonSerializer.Serialize(after.Edits)) return;
        Push(undo, JsonSerializer.Serialize(before));
        redo.Clear();
    }

    public bool Restore(bool forward, DirectorReplayEditSnapshot current, Func<DirectorReplayEditSnapshot, bool> save)
    {
        Select(current.Edits.ReplayId);
        var source = forward ? redo : undo;
        if (source.Count == 0) return false;
        var restored = JsonSerializer.Deserialize<DirectorReplayEditSnapshot>(source[^1])!;
        if (!save(restored)) return false;
        source.RemoveAt(source.Count - 1);
        Push(forward ? undo : redo, JsonSerializer.Serialize(current));
        return true;
    }

    private static void Push(List<string> stack, string snapshot)
    {
        if (stack.Count >= 64) stack.RemoveAt(0);
        stack.Add(snapshot);
    }
}
