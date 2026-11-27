using System.Reflection;
using System.Runtime.InteropServices;

namespace VSDirector.Voice;

/// <summary>
/// Optional RNNoise processing backed by the native runtime distributed with
/// SimpleVoiceChat. The native API and sample alignment follow its 48 kHz,
/// 10 ms processing contract.
/// </summary>
internal sealed class DirectorRnnoiseReducer : IDisposable
{
    private const int SamplesPerBlock = 480;
    private const int SamplesPerFrame = SamplesPerBlock * 2;
    private readonly IntPtr library;
    private readonly DestroyDelegate destroy;
    private readonly ProcessFrameDelegate processFrame;
    private readonly float[] input = new float[SamplesPerBlock];
    private readonly float[] output = new float[SamplesPerBlock];
    private readonly float[] delayedInput = new float[SamplesPerBlock];
    private IntPtr state;
    private float previousMix = 1f;

    private DirectorRnnoiseReducer(IntPtr library)
    {
        this.library = library;
        CreateDelegate create = Bind<CreateDelegate>(library, "rnnoise_create");
        destroy = Bind<DestroyDelegate>(library, "rnnoise_destroy");
        processFrame = Bind<ProcessFrameDelegate>(library, "rnnoise_process_frame");
        state = create(IntPtr.Zero);
        if (state == IntPtr.Zero)
        {
            throw new InvalidOperationException("RNNoise could not create a processing state.");
        }
    }

    internal static bool TryCreate(string modDataDirectory, out DirectorRnnoiseReducer? reducer, out string error)
    {
        reducer = null;
        error = string.Empty;
        string? source = ResolveRuntimePath();
        if (source is null)
        {
            error = "RNNoise is unavailable for this operating system or architecture.";
            return false;
        }

        string directory = Path.Combine(modDataDirectory, "VSDirector", "Native");
        string target = Path.Combine(directory, Path.GetFileName(source));
        try
        {
            Directory.CreateDirectory(directory);
            if (!File.Exists(target) || new FileInfo(target).Length != GetEmbeddedLength(source))
            {
                using Stream input = OpenEmbeddedRuntime(source);
                using FileStream output = File.Create(target);
                input.CopyTo(output);
            }

            if (!NativeLibrary.TryLoad(target, out IntPtr library))
            {
                error = "The RNNoise native library could not be loaded.";
                return false;
            }

            try
            {
                reducer = new DirectorRnnoiseReducer(library);
                return true;
            }
            catch
            {
                NativeLibrary.Free(library);
                throw;
            }
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    internal void Process(short[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        Process(samples.AsSpan());
    }

    private unsafe void Process(Span<short> samples)
    {
        if (state == IntPtr.Zero) throw new ObjectDisposedException(nameof(DirectorRnnoiseReducer));
        if (samples.Length != SamplesPerFrame) throw new ArgumentException("Expected a 20 ms PCM frame.", nameof(samples));

        for (int block = 0; block < 2; block++)
        {
            int offset = block * SamplesPerBlock;
            for (int index = 0; index < SamplesPerBlock; index++)
                input[index] = samples[offset + index];

            float speechProbability;
            fixed (float* outputPointer = output)
            fixed (float* inputPointer = input)
                speechProbability = Math.Clamp(processFrame(state, outputPointer, inputPointer), 0f, 1f);

            float speech = Math.Clamp((speechProbability - 0.2f) / 0.6f, 0f, 1f);
            float mix = 1f - 0.18f * speech;
            for (int index = 0; index < SamplesPerBlock; index++)
            {
                float fade = Math.Min(1f, (index + 1) / 48f);
                float wet = previousMix + (mix - previousMix) * fade;
                float aligned = output[index] * wet + delayedInput[index] * (1f - wet);
                delayedInput[index] = input[index];
                samples[offset + index] = (short)Math.Clamp(
                    (int)MathF.Round(aligned), short.MinValue, short.MaxValue);
            }
            previousMix = mix;
        }

    }

    private static string? ResolveRuntimePath()
    {
        string runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 when OperatingSystem.IsWindows() => "win-x64",
            Architecture.X86 when OperatingSystem.IsWindows() => "win-x86",
            Architecture.X64 when OperatingSystem.IsLinux() => "linux-x64",
            Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
            _ => string.Empty
        };
        if (runtime.Length == 0) return null;
        string file = OperatingSystem.IsWindows() ? "rnnoise.dll" : "librnnoise.so";
        return $"VSDirector.Native.{runtime}.{file}";
    }

    private static Stream OpenEmbeddedRuntime(string resource)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"Embedded RNNoise runtime was not found: {resource}");

    private static long GetEmbeddedLength(string resource)
    {
        using Stream stream = OpenEmbeddedRuntime(resource);
        return stream.Length;
    }

    private static T Bind<T>(IntPtr handle, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

    public void Dispose()
    {
        if (state != IntPtr.Zero)
        {
            destroy(state);
            state = IntPtr.Zero;
        }
        NativeLibrary.Free(library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateDelegate(IntPtr model);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate float ProcessFrameDelegate(IntPtr state, float* output, float* input);
}
