
namespace VSDirector.Rendering;

/// <summary>
/// A small late-bound OpenTK facade. VS Director intentionally does not take a
/// second compile-time OpenTK dependency; Vintage Story owns and loads the GL
/// binding assembly that backs these delegates.
/// </summary>
internal sealed class DirectorOpenGlPboApi
{
    private const int PixelPackBuffer = 35051;
    private const int StreamRead = 35041;
    private const int Bgra = 32993;
    private const int UnsignedByte = 5121;
    private const int SyncGpuCommandsComplete = 37143;
    private const int AlreadySignaled = 37146;
    private const int ConditionSatisfied = 37148;
    private const int WaitFailed = 37149;
    private const int MapReadBit = 1;

    private readonly GenBufferDelegate genBuffer;
    private readonly DeleteBufferDelegate deleteBuffer;
    private readonly BindBufferDelegate bindBuffer;
    private readonly BufferDataDelegate bufferData;
    private readonly ReadPixelsDelegate readPixels;
    private readonly FenceSyncDelegate fenceSync;
    private readonly ClientWaitSyncDelegate clientWaitSync;
    private readonly DeleteSyncDelegate deleteSync;
    private readonly MapBufferRangeDelegate mapBufferRange;
    private readonly UnmapBufferDelegate unmapBuffer;
    private readonly FlushDelegate flush;

    private DirectorOpenGlPboApi(Type glType)
    {
        Type bufferTarget = DirectorOpenGlBinding.NativeType(glType, "BufferTarget");
        Type bufferUsageHint = DirectorOpenGlBinding.NativeType(glType, "BufferUsageHint");
        Type pixelFormat = DirectorOpenGlBinding.NativeType(glType, "PixelFormat");
        Type pixelType = DirectorOpenGlBinding.NativeType(glType, "PixelType");
        Type syncCondition = DirectorOpenGlBinding.NativeType(glType, "SyncCondition");
        Type waitSyncFlags = DirectorOpenGlBinding.NativeType(glType, "WaitSyncFlags");
        Type clientWaitSyncFlags = DirectorOpenGlBinding.NativeType(glType, "ClientWaitSyncFlags");
        Type mapBufferAccessMask = DirectorOpenGlBinding.NativeType(glType, "MapBufferAccessMask");

        genBuffer = DirectorOpenGlBinding.Bind<GenBufferDelegate>(glType, "GenBuffer");
        deleteBuffer = DirectorOpenGlBinding.Bind<DeleteBufferDelegate>(glType, "DeleteBuffer", typeof(int));
        bindBuffer = DirectorOpenGlBinding.Bind<BindBufferDelegate>(glType, "BindBuffer", bufferTarget, typeof(int));
        bufferData = DirectorOpenGlBinding.Bind<BufferDataDelegate>(
            glType,
            "BufferData",
            bufferTarget,
            typeof(IntPtr),
            typeof(IntPtr),
            bufferUsageHint);
        readPixels = DirectorOpenGlBinding.Bind<ReadPixelsDelegate>(
            glType,
            "ReadPixels",
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(int),
            pixelFormat,
            pixelType,
            typeof(IntPtr));
        fenceSync = DirectorOpenGlBinding.Bind<FenceSyncDelegate>(
            glType,
            "FenceSync",
            syncCondition,
            waitSyncFlags);
        clientWaitSync = DirectorOpenGlBinding.Bind<ClientWaitSyncDelegate>(
            glType,
            "ClientWaitSync",
            typeof(IntPtr),
            clientWaitSyncFlags,
            typeof(ulong));
        deleteSync = DirectorOpenGlBinding.Bind<DeleteSyncDelegate>(glType, "DeleteSync", typeof(IntPtr));
        mapBufferRange = DirectorOpenGlBinding.Bind<MapBufferRangeDelegate>(
            glType,
            "MapBufferRange",
            bufferTarget,
            typeof(IntPtr),
            typeof(IntPtr),
            mapBufferAccessMask);
        unmapBuffer = DirectorOpenGlBinding.Bind<UnmapBufferDelegate>(glType, "UnmapBuffer", bufferTarget);
        flush = DirectorOpenGlBinding.Bind<FlushDelegate>(glType, "Flush");
    }

    internal static bool TryCreate(out DirectorOpenGlPboApi? api, out string error)
    {
        try
        {
            Type glType = DirectorOpenGlBinding.ApiType;
            api = new DirectorOpenGlPboApi(glType);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            api = null;
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    internal static string ValidateBindings()
        => TryCreate(out _, out string error) ? string.Empty : error;

    internal int CreatePixelPackBuffer(int byteCount)
    {
        int buffer = genBuffer();
        try
        {
            bindBuffer(PixelPackBuffer, buffer);
            bufferData(PixelPackBuffer, new IntPtr(byteCount), IntPtr.Zero, StreamRead);
            return buffer;
        }
        catch
        {
            if (buffer != 0)
            {
                deleteBuffer(buffer);
            }
            throw;
        }
        finally
        {
            bindBuffer(PixelPackBuffer, 0);
        }
    }

    internal void DeletePixelPackBuffer(int buffer)
    {
        if (buffer != 0)
        {
            deleteBuffer(buffer);
        }
    }

    internal IntPtr SubmitRead(int buffer, int width, int height)
    {
        bindBuffer(PixelPackBuffer, buffer);
        try
        {
            readPixels(0, 0, width, height, Bgra, UnsignedByte, IntPtr.Zero);
            IntPtr sync = fenceSync(SyncGpuCommandsComplete, 0);
            flush();
            return sync;
        }
        finally
        {
            bindBuffer(PixelPackBuffer, 0);
        }
    }

    internal FenceStatus PollFence(IntPtr sync)
    {
        int result = clientWaitSync(sync, 0, 0UL);
        return result switch
        {
            AlreadySignaled or ConditionSatisfied => FenceStatus.Ready,
            WaitFailed => FenceStatus.Failed,
            _ => FenceStatus.Pending
        };
    }

    internal FenceStatus WaitFence(IntPtr sync, int timeoutMilliseconds)
    {
        int result = clientWaitSync(sync, 1, (ulong)Math.Max(0, timeoutMilliseconds) * 1_000_000UL);
        return result switch { AlreadySignaled or ConditionSatisfied => FenceStatus.Ready, WaitFailed => FenceStatus.Failed, _ => FenceStatus.Pending };
    }

    internal void DeleteFence(IntPtr sync)
    {
        if (sync != IntPtr.Zero)
        {
            deleteSync(sync);
        }
    }

    internal IntPtr MapReadOnly(int buffer, int byteCount)
    {
        bindBuffer(PixelPackBuffer, buffer);
        try
        {
            return mapBufferRange(
                PixelPackBuffer,
                IntPtr.Zero,
                new IntPtr(byteCount),
                MapReadBit);
        }
        finally
        {
            bindBuffer(PixelPackBuffer, 0);
        }
    }

    internal bool Unmap(int buffer)
    {
        bindBuffer(PixelPackBuffer, buffer);
        try
        {
            return unmapBuffer(PixelPackBuffer);
        }
        finally
        {
            bindBuffer(PixelPackBuffer, 0);
        }
    }

    internal enum FenceStatus
    {
        Pending,
        Ready,
        Failed
    }

    private delegate int GenBufferDelegate();
    private delegate void DeleteBufferDelegate(int buffer);
    private delegate void BindBufferDelegate(int target, int buffer);
    private delegate void BufferDataDelegate(int target, IntPtr size, IntPtr data, int usage);
    private delegate void ReadPixelsDelegate(
        int x,
        int y,
        int width,
        int height,
        int format,
        int type,
        IntPtr pixels);
    private delegate IntPtr FenceSyncDelegate(int condition, int flags);
    private delegate int ClientWaitSyncDelegate(IntPtr sync, int flags, ulong timeout);
    private delegate void DeleteSyncDelegate(IntPtr sync);
    private delegate IntPtr MapBufferRangeDelegate(int target, IntPtr offset, IntPtr length, int access);
    private delegate bool UnmapBufferDelegate(int target);
    private delegate void FlushDelegate();
}
