using System;
using System.Runtime.InteropServices;

namespace VolumeGuard.Audio
{
    /// <summary>
    /// Ponderação A (IEC 61672) aproximada com 6 seções de 1ª ordem: 4 passa-altas
    /// (20,6 Hz x2, 107,7 Hz, 737,9 Hz) e 2 passa-baixas (12194 Hz x2), bilinear com pré-distorção.
    /// Normalizada para 0 dB em 1 kHz.
    /// </summary>
    public sealed class AWeighting
    {
        const int Sections = 6;
        readonly double[] b0 = new double[Sections], b1 = new double[Sections], a1 = new double[Sections];
        readonly double[][] xs, ys;   // estado por canal (arrays simples são mais rápidos que [,])
        readonly double gain;

        public AWeighting(int sampleRate, int channels)
        {
            double fs = sampleRate, k = 2 * fs;
            double[] freqs = { 20.598997, 20.598997, 107.65265, 737.86223, 12194.217, 12194.217 };
            for (int i = 0; i < Sections; i++)
            {
                double f = Math.Min(freqs[i], fs * 0.45);
                double wa = k * Math.Tan(Math.PI * f / fs);
                double d = k + wa;
                bool highPass = i < 4;
                b0[i] = highPass ? k / d : wa / d;
                b1[i] = highPass ? -k / d : wa / d;
                a1[i] = (wa - k) / d;
            }
            // |H| em 1 kHz para normalizar
            double w = 2 * Math.PI * 1000 / fs, cr = Math.Cos(w), ci = -Math.Sin(w), mag = 1;
            for (int i = 0; i < Sections; i++)
            {
                double nr = b0[i] + b1[i] * cr, ni = b1[i] * ci;
                double dr = 1 + a1[i] * cr, di = a1[i] * ci;
                mag *= Math.Sqrt((nr * nr + ni * ni) / (dr * dr + di * di));
            }
            gain = 1 / mag;
            xs = new double[channels][];
            ys = new double[channels][];
            for (int c = 0; c < channels; c++) { xs[c] = new double[Sections]; ys[c] = new double[Sections]; }
        }

        public double Process(int ch, double x)
        {
            double[] px = xs[ch], py = ys[ch];
            for (int s = 0; s < Sections; s++)
            {
                double y = b0[s] * x + b1[s] * px[s] - a1[s] * py[s];
                if (y < 1e-20 && y > -1e-20) y = 0;
                px[s] = x; py[s] = y;
                x = y;
            }
            return x * gain;
        }

        public void Reset()
        {
            for (int c = 0; c < xs.Length; c++) { Array.Clear(xs[c], 0, Sections); Array.Clear(ys[c], 0, Sections); }
        }
    }

    /// <summary>
    /// Captura em loopback do dispositivo de saída (o que realmente vai pro fone, já com o volume de cada app)
    /// e acumula a potência ponderada A por canal.
    /// </summary>
    public sealed class LoopbackMeter : IDisposable
    {
        /// <summary>Sessão própria do medidor, para não se misturar com outros sons deste processo.</summary>
        public static readonly Guid MeterSessionGuid = new Guid("5b0d3c6e-2a7f-4f7e-9c1d-7e2f0a9b4c11");
        const int AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;
        const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

        IAudioClient client;
        IAudioCaptureClient capture;
        MixFormat fmt;
        AWeighting weighting;
        double[] sumSq = new double[8];
        long frames;
        double rawPeak;
        float[] fbuf = new float[0];
        short[] sbuf = new short[0];
        byte[] bbuf = new byte[0];

        public int SampleRate { get { return fmt != null ? fmt.SampleRate : 48000; } }
        public int Channels { get { return fmt != null ? fmt.Channels : 2; } }
        public bool Invalid { get; private set; }

        public void Start(IMMDevice device)
        {
            client = CoreAudio.Activate<IAudioClient>(device, CoreAudio.IID_IAudioClient);
            IntPtr fmtPtr;
            CoreAudio.Check(client.GetMixFormat(out fmtPtr), "GetMixFormat");
            IntPtr guidPtr = Marshal.AllocHGlobal(16);
            try
            {
                fmt = MixFormat.FromPointer(fmtPtr);
                Marshal.Copy(MeterSessionGuid.ToByteArray(), 0, guidPtr, 16);
                CoreAudio.Check(client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED,
                    CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_NOPERSIST,
                    5000000, 0, fmtPtr, guidPtr), "Initialize(loopback)");
            }
            finally
            {
                Marshal.FreeCoTaskMem(fmtPtr);
                Marshal.FreeHGlobal(guidPtr);
            }
            object o;
            Guid iid = CoreAudio.IID_IAudioCaptureClient;
            CoreAudio.Check(client.GetService(ref iid, out o), "GetService(capture)");
            capture = (IAudioCaptureClient)o;
            if (sumSq.Length < fmt.Channels) sumSq = new double[fmt.Channels];
            weighting = new AWeighting(fmt.SampleRate, fmt.Channels);
            CoreAudio.Check(client.Start(), "Start(loopback)");
        }

        public void Poll()
        {
            if (capture == null || Invalid) return;
            for (int guard = 0; guard < 200; guard++)
            {
                int next;
                int hr = capture.GetNextPacketSize(out next);
                if (hr < 0) { if (hr == AUDCLNT_E_DEVICE_INVALIDATED) Invalid = true; return; }
                if (next == 0) return;
                IntPtr data; int n, flags; long p1, p2;
                hr = capture.GetBuffer(out data, out n, out flags, out p1, out p2);
                if (hr < 0) { if (hr == AUDCLNT_E_DEVICE_INVALIDATED) Invalid = true; return; }
                if (n > 0)
                {
                    if ((flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) == 0) Process(data, n);
                    frames += n;
                }
                capture.ReleaseBuffer(n);
            }
        }

        void Process(IntPtr data, int n)
        {
            int ch = fmt.Channels;
            int count = n * ch;
            if (fmt.IsFloat && fmt.BitsPerSample == 32)
            {
                if (fbuf.Length < count) fbuf = new float[count];
                Marshal.Copy(data, fbuf, 0, count);
                // silêncio digital (vídeo pausado etc.): não vale a pena passar pelo filtro
                bool allZero = true;
                for (int i = 0; i < count; i++) if (fbuf[i] != 0f) { allZero = false; break; }
                if (allZero) { weighting.Reset(); return; }
                for (int i = 0, idx = 0; i < n; i++)
                    for (int c = 0; c < ch; c++, idx++) Accumulate(c, fbuf[idx]);
            }
            else if (fmt.BitsPerSample == 16)
            {
                if (sbuf.Length < count) sbuf = new short[count];
                Marshal.Copy(data, sbuf, 0, count);
                for (int i = 0, idx = 0; i < n; i++)
                    for (int c = 0; c < ch; c++, idx++) Accumulate(c, sbuf[idx] / 32768.0);
            }
            else
            {
                int bytesPer = fmt.BitsPerSample / 8;
                int total = count * bytesPer;
                if (bbuf.Length < total) bbuf = new byte[total];
                Marshal.Copy(data, bbuf, 0, total);
                for (int i = 0, idx = 0; i < n; i++)
                    for (int c = 0; c < ch; c++, idx++)
                    {
                        int off = idx * bytesPer;
                        double v;
                        if (bytesPer == 3) v = ((bbuf[off] << 8 | bbuf[off + 1] << 16 | bbuf[off + 2] << 24) >> 8) / 8388608.0;
                        else v = BitConverter.ToInt32(bbuf, off) / 2147483648.0;
                        Accumulate(c, v);
                    }
            }
        }

        void Accumulate(int c, double x)
        {
            double ax = x < 0 ? -x : x;
            if (ax > rawPeak) rawPeak = ax;
            double y = weighting.Process(c, x);
            sumSq[c] += y * y;
        }

        /// <summary>Retorna a potência média ponderada A do canal mais alto desde a última chamada.</summary>
        public void Take(out double sumSqMax, out long frameCount, out double peak)
        {
            double m = 0;
            for (int c = 0; c < Channels && c < sumSq.Length; c++) { if (sumSq[c] > m) m = sumSq[c]; sumSq[c] = 0; }
            sumSqMax = m;
            frameCount = frames;
            peak = rawPeak;
            frames = 0; rawPeak = 0;
        }

        public void Dispose()
        {
            try { if (client != null) client.Stop(); } catch { }
            CoreAudio.SafeRelease(capture); capture = null;
            CoreAudio.SafeRelease(client); client = null;
        }
    }
}
