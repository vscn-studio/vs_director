using System.Buffers.Binary;
using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;
using VSDirector.Data;
using VSDirector.Recording;
using VSDirector.Replay;
using Vintagestory.API.Client;

namespace VSDirector.Voice;

internal enum DirectorNoiseReductionMode
{
    Off,
    NoiseGate,
    VoiceFocus,
    Rnnoise
}

/// <summary>Local mono microphone capture and lightweight monitoring for replay voice tracks.</summary>
internal sealed class DirectorMicrophoneRecorder : IDisposable
{
    private const int SampleRate = 48_000;
    private const int FrameSamples = 960;
    private const int WaveformSamples = 512;
    private static readonly Lazy<OpenCaptureDeviceDelegate> OpenCaptureDeviceFunction = new(() =>
    {
        IntPtr function = ALC.GetProcAddress(ALDevice.Null, "alcCaptureOpenDevice");
        if (function == IntPtr.Zero) throw new EntryPointNotFoundException("OpenAL capture is unavailable.");
        return Marshal.GetDelegateForFunctionPointer<OpenCaptureDeviceDelegate>(function);
    });
    private readonly ICoreClientAPI api;
    private readonly object sync = new();
    private readonly float[] waveform = new float[WaveformSamples];
    private ALCaptureDevice device;
    private string selectedDevice = string.Empty;
    private double rms;
    private double peak;
    private double noiseFloor = 0.006;
    private float previousInput;
    private float previousOutput;
    private FileStream? waveStream;
    private BinaryWriter? waveWriter;
    private DirectorRnnoiseReducer? rnnoiseReducer;
    private int waveDataBytes;
    private int disposed;
    private DirectorOpenAlNative? monitorApi;
    private DirectorReplayVoiceOutput? monitor;
    private readonly DirectorReplayVoiceFrame monitorFrame = new() { Spatialized = false };
    internal float MonitorVolume { get; set; }

    internal DirectorMicrophoneRecorder(ICoreClientAPI api) => this.api = api;

    internal bool IsRecording { get; private set; }
    internal string Error { get; private set; } = string.Empty;
    internal string OutputPath { get; private set; } = string.Empty;
    internal string NoiseReductionError { get; private set; } = string.Empty;
    internal Action<short[]>? FrameCaptured { get; set; }
    internal DirectorNoiseReductionMode NoiseReduction { get; set; } = DirectorNoiseReductionMode.NoiseGate;
    internal double Level => Volatile.Read(ref rms);
    internal double Peak => Volatile.Read(ref peak);
    internal IReadOnlyList<string> Devices => ReadCaptureDevices();

    internal string SelectedDevice
    {
        get => selectedDevice;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(selectedDevice, normalized, StringComparison.Ordinal)) return;
            selectedDevice = normalized;
            if (IsRecording) Stop();
        }
    }

    internal void Update()
    {
        if (!IsRecording || device.Handle == IntPtr.Zero) return;
        try
        {
            int available = ALC.GetInteger(device, AlcGetInteger.CaptureSamples);
            while (available >= FrameSamples)
            {
                short[] samples = new short[FrameSamples];
                ALC.CaptureSamples(device, samples, FrameSamples);
                available -= FrameSamples;
                Process(samples);
                AppendWaveFrame(samples);
                FrameCaptured?.Invoke(samples);
                if (MonitorVolume > 0)
                {
                    try
                    {
                        monitorApi ??= new DirectorOpenAlNative();
                        monitor ??= new DirectorReplayVoiceOutput(monitorApi);
                        monitor.Update(monitorFrame, MonitorVolume, 1);
                        monitor.Enqueue(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
                        monitor.SetPaused(false);
                    }
                    catch (Exception e)
                    {
                        Error = e.GetBaseException().Message;
                        MonitorVolume = 0;
                    }
                }
                else if (monitor is not null) CloseMonitor();
            }
        }
        catch (Exception exception)
        {
            Error = exception.GetBaseException().Message;
            Stop();
        }
    }

    internal bool Start(string fileName = "")
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        StopCaptureDevice();
        rnnoiseReducer?.Dispose();
        rnnoiseReducer = null;
        NoiseReductionError = string.Empty;
        try
        {
            if (NoiseReduction == DirectorNoiseReductionMode.Rnnoise
                && !DirectorRnnoiseReducer.TryCreate(api.GetOrCreateDataPath("ModData"), out rnnoiseReducer, out string denoiserError))
            {
                NoiseReductionError = denoiserError;
            }
            device = OpenCaptureDevice(selectedDevice.Length == 0 ? null : selectedDevice);
            if (device.Handle == IntPtr.Zero)
            {
                Error = "Microphone capture device could not be opened.";
                return false;
            }
            OpenWaveFile(fileName);
            rms = peak = 0;
            noiseFloor = 0.006;
            previousInput = previousOutput = 0;
            ALC.CaptureStart(device);
            Error = string.Empty;
            IsRecording = true;
            return true;
        }
        catch (Exception exception)
        {
            Error = exception.GetBaseException().Message;
            AbortWaveFile();
            StopCaptureDevice();
            return false;
        }
    }

    internal bool Stop()
    {
        CloseMonitor();
        if (!IsRecording)
        {
            StopCaptureDevice();
            rnnoiseReducer?.Dispose();
            rnnoiseReducer = null;
            return false;
        }
        IsRecording = false;
        StopCaptureDevice();
        rnnoiseReducer?.Dispose();
        rnnoiseReducer = null;
        try
        {
            OutputPath = FinalizeWaveFile();
            return true;
        }
        catch (Exception exception)
        {
            Error = exception.GetBaseException().Message;
            rnnoiseReducer?.Dispose();
            rnnoiseReducer = null;
            AbortWaveFile();
            return false;
        }
    }

    internal float[] CopyWaveform()
    {
        lock (sync) return (float[])waveform.Clone();
    }

    private void Process(short[] samples)
    {
        double squares = 0;
        double maximum = 0;
        lock (sync)
        {
            Array.Clear(waveform);
            if (NoiseReduction == DirectorNoiseReductionMode.Rnnoise && rnnoiseReducer is not null)
            {
                rnnoiseReducer.Process(samples);
            }
            for (int index = 0; index < samples.Length; index++)
            {
                float input = samples[index] / 32768f;
                float filtered = input;
                if (NoiseReduction == DirectorNoiseReductionMode.VoiceFocus
                    || (NoiseReduction == DirectorNoiseReductionMode.Rnnoise && rnnoiseReducer is null))
                {
                    filtered = input - previousInput + 0.9896f * previousOutput;
                    previousInput = input;
                    previousOutput = filtered;
                }
                float magnitude = Math.Abs(filtered);
                if (NoiseReduction is DirectorNoiseReductionMode.NoiseGate or DirectorNoiseReductionMode.VoiceFocus
                    || (NoiseReduction == DirectorNoiseReductionMode.Rnnoise && rnnoiseReducer is null))
                {
                    double threshold = noiseFloor * (NoiseReduction == DirectorNoiseReductionMode.NoiseGate ? 1.8 : 2.2);
                    if (magnitude < threshold) filtered *= 0.12f;
                }
                samples[index] = (short)Math.Clamp((int)MathF.Round(filtered * short.MaxValue), short.MinValue, short.MaxValue);
                float normalized = samples[index] / 32768f;
                squares += normalized * normalized;
                maximum = Math.Max(maximum, Math.Abs(normalized));
                int target = index * WaveformSamples / samples.Length;
                waveform[target] = Math.Max(waveform[target], Math.Abs(normalized));
            }
            rms = Math.Sqrt(squares / samples.Length);
            peak = maximum;
            if (rms < noiseFloor * 2.5) noiseFloor = noiseFloor * 0.98 + rms * 0.02;
            else noiseFloor = Math.Max(0.001, noiseFloor * 0.997);
        }
    }

    private void CloseMonitor()
    {
        monitor?.Dispose();
        monitor = null;
        monitorApi?.Dispose();
        monitorApi = null;
    }

    private void OpenWaveFile(string fileName)
    {
        string directory = Path.Combine(api.GetOrCreateDataPath("ModData"), "VSDirector", "Recordings");
        Directory.CreateDirectory(directory);
        string name = fileName.Trim();
        if (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) name = "Voice-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new IOException(Vintagestory.API.Config.Lang.Get("vsdirector:voice-invalid-name"));
        for (int take = 1; ; take++)
        {
            string path = Path.Combine(directory, (take == 1 ? name : $"{name}-{take}") + ".wav");
            try { waveStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
            catch (IOException) when (File.Exists(path)) { continue; }
            OutputPath = path;
            break;
        }
        waveWriter = new BinaryWriter(waveStream);
        waveWriter.Write("RIFF"u8);
        waveWriter.Write(0);
        waveWriter.Write("WAVEfmt "u8);
        waveWriter.Write(16);
        waveWriter.Write((short)1);
        waveWriter.Write((short)1);
        waveWriter.Write(SampleRate);
        waveWriter.Write(SampleRate * sizeof(short));
        waveWriter.Write((short)sizeof(short));
        waveWriter.Write((short)16);
        waveWriter.Write("data"u8);
        waveWriter.Write(0);
        waveDataBytes = 0;
    }

    private void AppendWaveFrame(short[] frame)
    {
        BinaryWriter? writer = waveWriter;
        if (writer is null) return;
        Span<byte> bytes = stackalloc byte[FrameSamples * sizeof(short)];
        for (int index = 0; index < frame.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.Slice(index * 2, sizeof(short)), frame[index]);
        }
        writer.Write(bytes);
        waveDataBytes = checked(waveDataBytes + bytes.Length);
    }

    private string FinalizeWaveFile()
    {
        BinaryWriter? writer = waveWriter;
        FileStream? stream = waveStream;
        if (writer is null || stream is null) throw new InvalidOperationException("The microphone WAV file is not open.");
        writer.Flush();
        stream.Position = 4;
        writer.Write(36 + waveDataBytes);
        stream.Position = 40;
        writer.Write(waveDataBytes);
        writer.Flush();
        string path = OutputPath;
        writer.Dispose();
        waveWriter = null;
        waveStream = null;
        waveDataBytes = 0;
        if (new FileInfo(path).Length <= 44)
        {
            File.Delete(path);
            throw new InvalidOperationException("No microphone audio was captured.");
        }
        return path;
    }

    private void AbortWaveFile()
    {
        bool created = waveStream is not null;
        waveWriter?.Dispose();
        waveWriter = null;
        waveStream = null;
        waveDataBytes = 0;
        if (created && !string.IsNullOrWhiteSpace(OutputPath))
        {
            try { File.Delete(OutputPath); } catch { }
            OutputPath = string.Empty;
        }
    }

    private void StopCaptureDevice()
    {
        if (device.Handle == IntPtr.Zero) return;
        try { ALC.CaptureStop(device); } catch { }
        try { ALC.CaptureCloseDevice(device); } catch { }
        device = default;
    }

    private static unsafe IReadOnlyList<string> ReadCaptureDevices()
    {
        try
        {
            IntPtr pointer = (IntPtr)ALC.GetStringPtr(ALDevice.Null, AlcGetString.CaptureDeviceSpecifier);
            List<string> result = new();
            while (pointer != IntPtr.Zero && Marshal.ReadByte(pointer) != 0)
            {
                int length = 0;
                while (Marshal.ReadByte(pointer, length) != 0) length++;
                result.Add(Marshal.PtrToStringUTF8(pointer, length) ?? string.Empty);
                pointer = IntPtr.Add(pointer, length + 1);
            }
            if (result.Count == 0)
            {
                string? fallback = ALC.GetString(ALDevice.Null, AlcGetString.CaptureDeviceSpecifier);
                if (!string.IsNullOrWhiteSpace(fallback)) result.Add(fallback);
            }
            return result;
        }
        catch { return Array.Empty<string>(); }
    }

    private static ALCaptureDevice OpenCaptureDevice(string? name)
    {
        IntPtr utf8 = name is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            IntPtr handle = OpenCaptureDeviceFunction.Value(utf8, SampleRate, ALFormat.Mono16, FrameSamples * 16);
            return new ALCaptureDevice(handle);
        }
        finally { if (utf8 != IntPtr.Zero) Marshal.FreeCoTaskMem(utf8); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenCaptureDeviceDelegate(IntPtr deviceName, uint frequency, ALFormat format, int bufferSize);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Stop();
        StopCaptureDevice();
    }
}
