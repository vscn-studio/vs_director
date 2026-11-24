namespace VSDirector.Data;

public sealed record DirectorOperationStatus(bool Active, bool Export, double Progress, string Phase, string Title, string Detail, string Error);

/// <summary>Main-thread progress history, bounded and sampled without flooding the UI.</summary>
public sealed class DirectorOperationProgress
{
    private readonly List<string> lines = new();
    private double lastEntry = double.NegativeInfinity;
    public IReadOnlyList<string> Lines => lines;
    public DirectorOperationStatus? Current { get; private set; }
    public int Revision { get; private set; }

    public void Clear()
    {
        lines.Clear(); Current = null; lastEntry = double.NegativeInfinity; Revision++;
    }

    public void Update(DirectorOperationStatus status, double seconds)
    {
        bool start = status.Active && (Current?.Active != true || Current.Export != status.Export);
        if (start) { lines.Clear(); lastEntry = double.NegativeInfinity; }
        bool changed = Current?.Phase != status.Phase || Current?.Active != status.Active || Current?.Error != status.Error;
        if (changed || status.Active && seconds - lastEntry >= 1)
        {
            string line = status.Detail + (status.Error.Length > 0 ? "\n" + status.Error : "");
            if (lines.Count == 0 || !lines[^1].EndsWith(line, StringComparison.Ordinal))
            {
                if (lines.Count >= 150) lines.RemoveAt(0);
                lines.Add($"[{TimeSpan.FromSeconds(Math.Max(0, seconds)):hh\\:mm\\:ss}] {line}");
                Revision++;
            }
            lastEntry = seconds;
        }
        Current = status with { Progress = double.IsFinite(status.Progress) ? Math.Clamp(status.Progress, 0, 1) : 0 };
    }
}
