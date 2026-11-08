namespace VSDirector.Recording;

internal static class DirectorRecordingCallbackGuard
{
    public static void InvokeSuccess(
        Action success,
        Action<Exception> failure,
        Action<Exception> log)
    {
        try
        {
            success();
        }
        catch (Exception exception)
        {
            TryLog(log, exception);
            InvokeFailure(failure, exception, log);
        }
    }

    public static void InvokeFailure(
        Action<Exception> failure,
        Exception exception,
        Action<Exception> log)
    {
        try
        {
            failure(exception);
        }
        catch (Exception callbackException)
        {
            TryLog(log, callbackException);
        }
    }

    private static void TryLog(Action<Exception> log, Exception exception)
    {
        try
        {
            log(exception);
        }
        catch
        {
        }
    }
}
