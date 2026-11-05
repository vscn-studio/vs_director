namespace VSDirector.Data;

public static class DirectorReplayExportIntegrity
{
    public static string Validate(long requested, long encoded, long dropped, long readbackFailures, long duplicated)
        => requested != encoded || dropped != 0 || readbackFailures != 0 || duplicated != 0
            ? $"Replay export is incomplete: requested {requested}, encoded {encoded}, dropped {dropped}, GPU failures {readbackFailures}, duplicated {duplicated}."
            : string.Empty;
}
