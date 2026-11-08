namespace VSDirector.Data;

public static class DirectorReplayHandoffPolicy
{
    public static bool MustWaitForReplaySave(bool recordReplayWithVideo, bool isLiveWorkflow)
        => recordReplayWithVideo && !isLiveWorkflow;
}
