using System.Runtime.InteropServices;

namespace Glint;

/// <summary>
/// Real frequency bands for Skyline's waveform: bass, low-mid, mid, high-mid and treble, each 0..1.
/// Listens to a copy of what's going to the speakers (WASAPI "loopback"; never the microphone), and runs a
/// 2048-point FFT every 1024 samples, about 47 times a second. Measured at roughly 0.3–0.6% of one CPU core
/// and ~0.15 MB while music plays; it only runs between <see cref="Start"/> and <see cref="Stop"/>.
/// </summary>
internal sealed class AudioBands
{
    public const int Count = 5;
    const int N = 2048, Hop = 1024;
    static readonly double[] Edges = { 30, 150, 400, 1000, 3000, 12000 };

    /// <summary>Latest band levels, 0..1, written by the capture thread and read by the UI each frame.</summary>
    public readonly float[] Bands = new float[Count];

    /// <summary>False if capture couldn't start (no speakers, device busy); the caller then falls back.</summary>
    public bool Working { get; private set; }

    readonly float[] _ring = new float[N];
    readonly double[] _re = new double[N], _im = new double[N], _window = new double[N], _cos = new double[N / 2], _sin = new double[N / 2];
    readonly int[] _rev = new int[N];
    readonly int[] _lo = new int[Count], _hi = new int[Count];
    int _fill, _sinceFft, _rate;

    Thread? _thread;
    volatile bool _run;

    public AudioBands()
    {
        for (int i = 0; i < N; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
        for (int i = 0; i < N / 2; i++) { _cos[i] = Math.Cos(-2 * Math.PI * i / N); _sin[i] = Math.Sin(-2 * Math.PI * i / N); }
        int bits = (int)Math.Log2(N);
        for (int i = 0; i < N; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _rev[i] = r;
        }
    }

    public void Start()
    {
        if (_run) return;
        _run = true;
        Working = true; // optimistic until the capture thread says otherwise
        _thread = new Thread(Pump) { IsBackground = true, Name = "Glint.AudioBands", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(500);
        _thread = null;
        Array.Clear(Bands);
    }

    // ---------- capture ----------

    void Pump()
    {
        var mono = new float[48000];
        var raw = new float[96000 * 2];
        while (_run)
        {
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            try
            {
                (client, capture, int channels, bool isFloat) = Open();
                Working = true;
                client.Start();
                while (_run)
                {
                    Thread.Sleep(10);
                    while (true)
                    {
                        // An error here usually means the default speakers changed; reopen on the new ones.
                        Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint packet));
                        if (packet == 0) break;
                        Marshal.ThrowExceptionForHR(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                        int n = (int)Math.Min(frames, (uint)mono.Length);
                        if ((flags & 2) != 0) Array.Clear(mono, 0, n); // silent packet
                        else Downmix(data, n, channels, isFloat, raw, mono);
                        capture.ReleaseBuffer(frames);
                        Push(mono, n);
                    }
                }
                client.Stop();
            }
            catch
            {
                Working = false;
                Array.Clear(Bands);
                if (_run) Thread.Sleep(2000); // try again shortly, e.g. after plugging in headphones
            }
            finally
            {
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
            }
        }
    }

    (IAudioClient, IAudioCaptureClient, int Channels, bool IsFloat) Open()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* render: the speakers */, 1, out var device));
        Guid iid = typeof(IAudioClient).GUID;
        Marshal.ThrowExceptionForHR(device!.Activate(ref iid, 23, IntPtr.Zero, out object o));
        var client = (IAudioClient)o;

        Marshal.ThrowExceptionForHR(client.GetMixFormat(out IntPtr fmt));
        try
        {
            int tag = (ushort)Marshal.ReadInt16(fmt, 0);
            int channels = (ushort)Marshal.ReadInt16(fmt, 2);
            int rate = Marshal.ReadInt32(fmt, 4);
            int bitsPerSample = (ushort)Marshal.ReadInt16(fmt, 14);
            bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.PtrToStructure<Guid>(fmt + 24) == FloatFormat);
            if (!isFloat && bitsPerSample != 16) throw new NotSupportedException();
            SetRate(rate);
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000 /* LOOPBACK */, 2_000_000 /* 200 ms */, 0, fmt, IntPtr.Zero));

            Guid ciid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out object c));
            return (client, (IAudioCaptureClient)c, channels, isFloat);
        }
        finally { Marshal.FreeCoTaskMem(fmt); }
    }

    static readonly Guid FloatFormat = new("00000003-0000-0010-8000-00aa00389b71");

    static void Downmix(IntPtr data, int frames, int channels, bool isFloat, float[] raw, float[] mono)
    {
        int samples = Math.Min(frames * channels, raw.Length);
        frames = samples / channels;
        if (isFloat) Marshal.Copy(data, raw, 0, samples);
        else
        {
            for (int i = 0; i < samples; i++) raw[i] = Marshal.ReadInt16(data, i * 2) / 32768f;
        }
        for (int f = 0; f < frames; f++)
        {
            float s = 0;
            for (int ch = 0; ch < channels; ch++) s += raw[f * channels + ch];
            mono[f] = s / channels;
        }
    }

    // ---------- analysis ----------

    void SetRate(int rate)
    {
        if (rate == _rate) return;
        _rate = rate;
        double binHz = (double)rate / N;
        for (int k = 0; k < Count; k++)
        {
            _lo[k] = Math.Max(1, (int)(Edges[k] / binHz));
            _hi[k] = Math.Max(_lo[k] + 1, (int)(Edges[k + 1] / binHz));
        }
    }

    void Push(float[] mono, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _ring[_fill] = mono[i];
            _fill = (_fill + 1) % N;
            if (++_sinceFft >= Hop)
            {
                _sinceFft = 0;
                Analyse();
            }
        }
    }

    void Analyse()
    {
        for (int i = 0; i < N; i++)
        {
            _re[_rev[i]] = _ring[(_fill + i) % N] * _window[i];
            _im[_rev[i]] = 0;
        }
        for (int size = 2; size <= N; size <<= 1)
        {
            int half = size >> 1, step = N / size;
            for (int start = 0; start < N; start += size)
                for (int j = 0; j < half; j++)
                {
                    double wr = _cos[j * step], wi = _sin[j * step];
                    int a = start + j, b = a + half;
                    double tr = _re[b] * wr - _im[b] * wi, ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
        }
        for (int k = 0; k < Count; k++)
        {
            double sum = 0;
            for (int bin = _lo[k]; bin < _hi[k]; bin++) sum += _re[bin] * _re[bin] + _im[bin] * _im[bin];
            double rms = Math.Sqrt(sum / (_hi[k] - _lo[k])) / (N / 4.0);
            double db = 20 * Math.Log10(rms + 1e-9);
            Bands[k] = (float)Math.Clamp((db + 60) / 60, 0, 1); // -60 dB .. 0 dB
        }
    }

    // ---------- interop ----------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
