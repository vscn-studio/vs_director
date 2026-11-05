using System.Text.Json;

namespace VSDirector.Data;

public sealed class CameraPathEditHistory
{
    private readonly int capacity;
    private readonly List<CameraPath> undo = new();
    private readonly List<CameraPath> redo = new();
    private string? lastMergeKey;

    public CameraPathEditHistory(int capacity = 64)
    {
        this.capacity = Math.Max(1, capacity);
    }

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public bool RecordChange(CameraPath before, CameraPath after, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (mergeKey is not null
            && string.Equals(lastMergeKey, mergeKey, StringComparison.Ordinal)
            && undo.Count > 0)
        {
            redo.Clear();
            return true;
        }
        if (Equivalent(before, after))
        {
            return false;
        }

        Push(undo, before);
        redo.Clear();
        lastMergeKey = mergeKey;
        return true;
    }

    public bool TryUndo(CameraPath current, out CameraPath restored)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (undo.Count == 0)
        {
            restored = current.DeepClone();
            return false;
        }

        Push(redo, current);
        restored = Pop(undo);
        lastMergeKey = null;
        return true;
    }

    public bool TryRedo(CameraPath current, out CameraPath restored)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (redo.Count == 0)
        {
            restored = current.DeepClone();
            return false;
        }

        Push(undo, current);
        restored = Pop(redo);
        lastMergeKey = null;
        return true;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        lastMergeKey = null;
    }

    private void Push(List<CameraPath> stack, CameraPath path)
    {
        if (stack.Count >= capacity)
        {
            stack.RemoveAt(0);
        }
        stack.Add(path.DeepClone());
    }

    private static CameraPath Pop(List<CameraPath> stack)
    {
        int index = stack.Count - 1;
        CameraPath result = stack[index].DeepClone();
        stack.RemoveAt(index);
        return result;
    }

    private static bool Equivalent(CameraPath left, CameraPath right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
