using System.Runtime.InteropServices;
using System.Text;
using DeltaHarmonica.Core;

namespace DeltaHarmonica.App.Services;

/// <summary>
/// Streams the built-in harmonica sound to the Windows default output. Opens the
/// device only on the first nonempty preview chord; no assets or packages are needed.
/// </summary>
public sealed class PreviewAudioService : IPreviewAudioSink, IDisposable
{
    private const int BufferSamples = HarmonicaSynthesizer.SampleRate / 50; // 20 ms; 60 ms of scheduling headroom
    private const int BufferCount = 3;
    private const uint HeaderInQueue = 0x10;
    private readonly object _gate = new();
    private readonly HarmonicaSynthesizer _synthesizer = new();
    private readonly IWaveOutApi _api;
    private readonly List<PcmBuffer> _buffers = [];
    private AutoResetEvent? _bufferReady;
    private Thread? _worker;
    private IntPtr _device;
    private Exception? _backgroundError;
    private bool _releaseErrorReported;
    private bool _disposed;

    public PreviewAudioService() : this(new WinMmWaveOutApi()) { }

    // A small native boundary lets lifecycle/error tests run without an audio device.
    internal PreviewAudioService(IWaveOutApi api) => _api = api;

    public void SetChord(IReadOnlyList<MappedNote> notes, IReadOnlyList<char> retriggerKeys)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_backgroundError is not null)
                throw new InvalidOperationException($"内置预览音效已停止：{_backgroundError.Message}", _backgroundError);
            _synthesizer.SetChord(notes, retriggerKeys);
            // Musical note-off keeps the stream and the reed's release envelope alive.
            // Only transport operations (pause/seek/stop) discard queued sound.
            if (notes.Count == 0) { _bufferReady?.Set(); return; }
            try { EnsureOutput(); }
            catch { _synthesizer.ReleaseAll(); throw; }
            _bufferReady!.Set();
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            _synthesizer.ReleaseAll();
            // Reset removes already queued sound too, so pause/seek/stop are immediate.
            if (_device != IntPtr.Zero) Check(_api.Reset(_device), "停止预览音效");
            _bufferReady?.Set();
            // Report even a failure during one long sustained chord, whose next
            // scheduler operation can be completion/pause rather than SetChord.
            if (_backgroundError is not null && !_releaseErrorReported)
            {
                _releaseErrorReported = true;
                throw new InvalidOperationException($"内置预览音效已停止：{_backgroundError.Message}", _backgroundError);
            }
        }
    }

    private void EnsureOutput()
    {
        if (_device != IntPtr.Zero) return;
        try
        {
            _bufferReady = new AutoResetEvent(false);
            var format = new WaveFormatEx
            {
                FormatTag = 1, Channels = 1, SamplesPerSecond = HarmonicaSynthesizer.SampleRate,
                AverageBytesPerSecond = HarmonicaSynthesizer.SampleRate * sizeof(short),
                BlockAlign = sizeof(short), BitsPerSample = 16
            };
            var result = _api.Open(out var device, ref format, _bufferReady.SafeWaitHandle.DangerousGetHandle());
            Check(result, "打开默认音频输出设备");
            _device = device;
            for (var index = 0; index < BufferCount; index++)
            {
                var buffer = new PcmBuffer(BufferSamples);
                _buffers.Add(buffer);
                Check(_api.Prepare(_device, buffer.Header, WaveHeader.Size), "准备音频缓冲区");
                buffer.Prepared = true;
            }
            var worker = new Thread(StreamAudio)
            {
                IsBackground = true, Priority = ThreadPriority.AboveNormal,
                Name = "DeltaHarmonica preview audio"
            };
            worker.Start();
            _worker = worker;
        }
        catch (Exception exception)
        {
            CleanupOutput();
            // A failed driver can retain its handle. That is owned memory, not a
            // successfully initialized output; do not silently reuse it on retry.
            if (_device != IntPtr.Zero) _backgroundError = exception;
            throw;
        }
    }

    private void StreamAudio()
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    foreach (var buffer in _buffers)
                    {
                        // The event only signals a change: inspect every header because
                        // several buffers can complete before this thread is scheduled.
                        if ((Marshal.PtrToStructure<WaveHeader>(buffer.Header).Flags & HeaderInQueue) != 0) continue;
                        _synthesizer.Render(buffer.Samples);
                        Marshal.Copy(buffer.Samples, 0, buffer.Data, buffer.Samples.Length);
                        Check(_api.Write(_device, buffer.Header, WaveHeader.Size), "播放预览音效");
                    }
                }
                _bufferReady!.WaitOne(100);
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _backgroundError = exception;
                _synthesizer.ReleaseAll();
                if (_device != IntPtr.Zero) _api.Reset(_device);
            }
        }
    }

    public void Dispose()
    {
        Thread? worker;
        lock (_gate)
        {
            _disposed = true;
            _synthesizer.ReleaseAll();
            _bufferReady?.Set();
            worker = _worker;
        }
        // Never free a native buffer while the producer thread could still use it.
        worker?.Join();
        lock (_gate)
        {
            _worker = null;
            CleanupOutput();
        }
        GC.SuppressFinalize(this);
    }

    private void CleanupOutput()
    {
        if (_device != IntPtr.Zero) _api.Reset(_device);
        foreach (var buffer in _buffers)
        {
            if (buffer.Prepared && _api.Unprepare(_device, buffer.Header, WaveHeader.Size) == 0)
                buffer.Prepared = false;
        }
        var closed = _device == IntPtr.Zero || _api.Close(_device) == 0;
        if (closed) _device = IntPtr.Zero;
        // If a driver refuses reset/unprepare/close, keep its owned memory alive.
        // A repeated Dispose may retry cleanup; releasing queued memory is unsafe.
        foreach (var buffer in _buffers.Where(buffer => closed || !buffer.Prepared).ToArray())
        {
            buffer.Free();
            _buffers.Remove(buffer);
        }
        if (closed)
        {
            _bufferReady?.Dispose();
            _bufferReady = null;
        }
    }

    private void Check(uint result, string operation)
    {
        if (result != 0)
            throw new InvalidOperationException($"{operation}失败：{_api.DescribeError(result)}（错误码 {result}）。");
    }

    private sealed class PcmBuffer
    {
        internal PcmBuffer(int samples)
        {
            Samples = new short[samples];
            Data = Marshal.AllocHGlobal(samples * sizeof(short));
            try
            {
                Header = Marshal.AllocHGlobal((int)WaveHeader.Size);
                Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = (uint)(samples * sizeof(short)) }, Header, false);
            }
            catch
            {
                if (Header != IntPtr.Zero) Marshal.FreeHGlobal(Header);
                Marshal.FreeHGlobal(Data);
                throw;
            }
        }
        internal short[] Samples { get; }
        internal IntPtr Data { get; }
        internal IntPtr Header { get; }
        internal bool Prepared { get; set; }
        internal void Free() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSecond;
        internal uint AverageBytesPerSecond;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader
    {
        internal IntPtr Data;
        internal uint BufferLength;
        internal uint BytesRecorded;
        internal UIntPtr User;
        internal uint Flags;
        internal uint Loops;
        internal IntPtr Next;
        internal UIntPtr Reserved;
        internal static uint Size => (uint)Marshal.SizeOf<WaveHeader>();
    }

    internal interface IWaveOutApi
    {
        uint Open(out IntPtr device, ref WaveFormatEx format, IntPtr callbackEvent);
        uint Prepare(IntPtr device, IntPtr header, uint headerSize);
        uint Write(IntPtr device, IntPtr header, uint headerSize);
        uint Reset(IntPtr device);
        uint Unprepare(IntPtr device, IntPtr header, uint headerSize);
        uint Close(IntPtr device);
        string DescribeError(uint error);
    }

    private sealed class WinMmWaveOutApi : IWaveOutApi
    {
        public uint Open(out IntPtr device, ref WaveFormatEx format, IntPtr callbackEvent)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("内置预览音效需要 Windows 音频输出。");
            return waveOutOpen(out device, uint.MaxValue, ref format, callbackEvent, UIntPtr.Zero, 0x00050000); // CALLBACK_EVENT
        }
        public uint Prepare(IntPtr device, IntPtr header, uint headerSize) => waveOutPrepareHeader(device, header, headerSize);
        public uint Write(IntPtr device, IntPtr header, uint headerSize) => waveOutWrite(device, header, headerSize);
        public uint Reset(IntPtr device) => waveOutReset(device);
        public uint Unprepare(IntPtr device, IntPtr header, uint headerSize) => waveOutUnprepareHeader(device, header, headerSize);
        public uint Close(IntPtr device) => waveOutClose(device);
        public string DescribeError(uint error)
        {
            var text = new StringBuilder(256);
            return waveOutGetErrorTextW(error, text, (uint)text.Capacity) == 0 ? text.ToString() : "Windows 音频设备错误";
        }

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormatEx format, IntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint headerSize);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint headerSize);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutReset(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint headerSize);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutClose(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint waveOutGetErrorTextW(uint error, StringBuilder text, uint capacity);
    }
}
