namespace VSDirector.Data;

public sealed class DirectorRetryableAsyncSave<TResult>
{
    private readonly Func<Task<TResult>> saveFactory;
    private Task<TResult>? task;

    public DirectorRetryableAsyncSave(Func<Task<TResult>> saveFactory)
    {
        this.saveFactory = saveFactory ?? throw new ArgumentNullException(nameof(saveFactory));
    }

    public bool IsSaving => task is not null;

    public Task? Completion => task;

    public bool HasFailed { get; private set; }

    public string Error { get; private set; } = string.Empty;

    public void Start()
    {
        if (task is not null)
        {
            throw new InvalidOperationException("The save operation is already running.");
        }

        HasFailed = false;
        Error = string.Empty;
        try
        {
            task = saveFactory()
                ?? Task.FromException<TResult>(new InvalidOperationException("The save factory returned no task."));
        }
        catch (Exception exception)
        {
            task = Task.FromException<TResult>(exception);
        }
    }

    public bool TryTakeCompletion(out TResult? result, out Exception? exception)
    {
        result = default;
        exception = null;
        Task<TResult>? active = task;
        if (active is null || !active.IsCompleted)
        {
            return false;
        }

        task = null;
        try
        {
            result = active.GetAwaiter().GetResult();
            HasFailed = false;
            Error = string.Empty;
        }
        catch (Exception failure)
        {
            exception = failure;
            HasFailed = true;
            Error = failure.Message;
        }

        return true;
    }
}
