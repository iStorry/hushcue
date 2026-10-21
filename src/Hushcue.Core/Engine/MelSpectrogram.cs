namespace Hushcue.Core.Engine;

/// <summary>
/// Whisper's log-mel spectrogram, a port of faster-whisper's FeatureExtractor: 400-point STFT with a Hann window and
/// a 160-sample hop, Slaney mel filters, log10, clamped to 8 below the file's loudest value and rescaled.
/// Values are float32 at the same points numpy's are. The FFT runs in double; the magnitude and the filter sums follow
/// numpy's float32 arithmetic as closely as measured, so results match numpy to within one float32 step.
/// </summary>
public sealed class MelSpectrogram
{
    public const int SampleRate = 16000;
    public const int NFft = 400;
    public const int HopLength = 160;
    public const int ChunkLength = 30;
    public const int NSamples = ChunkLength * SampleRate;
    public const int FramesPerWindow = NSamples / HopLength; // 3000
    public const double TimePerFrame = (double)HopLength / SampleRate;

    private const int Bins = NFft / 2 + 1; // 201
    private const int N1 = 16, N2 = 25; // NFft = N1 * N2, for a two-step Cooley-Tukey FFT

    private static readonly float[] Window = HannWindow();
    private static readonly (double Re, double Im)[] Twiddle = Twiddles();

    private readonly float[] _filters; // NMels x Bins, row-major

    public MelSpectrogram(int nMels)
    {
        NMels = nMels;
        _filters = MelFilters(SampleRate, NFft, nMels);
    }

    public int NMels { get; }

    /// <summary>
    /// Log-mel features of 16 kHz audio: NMels rows of <c>frames</c> values each (row-major), where
    /// frames = samples / 160 + 1. <paramref name="padding"/> zero samples are appended first, as faster-whisper does.
    /// </summary>
    public (float[] Data, int Frames) Compute(ReadOnlySpan<float> audio, int padding = 160)
    {
        var signal = new float[audio.Length + padding];
        audio.CopyTo(signal);
        // centre=True: reflect-pad NFft/2 on both sides (numpy mode="reflect": the edge sample is not repeated)
        const int pad = NFft / 2;
        var padded = new float[signal.Length + 2 * pad];
        signal.CopyTo(padded, pad);
        for (var i = 0; i < pad; i++)
        {
            padded[pad - 1 - i] = signal[Math.Min(i + 1, signal.Length - 1)];
            padded[pad + signal.Length + i] = signal[Math.Max(signal.Length - 2 - i, 0)];
        }
        var stftFrames = 1 + (padded.Length - NFft) / HopLength;
        var frames = stftFrames - 1; // stft[..., :-1]: the last frame is dropped
        var data = new float[NMels * frames];

        Parallel.For(0, frames, () => new FrameBuffers(), (t, _, buffers) =>
        {
            ComputeFrame(padded.AsSpan(t * HopLength, NFft), buffers);
            for (var m = 0; m < NMels; m++)
            {
                // float32 with two interleaved accumulators: of the orders tried, the closest to OpenBLAS's sgemm
                var row = _filters.AsSpan(m * Bins, Bins);
                float even = 0, odd = 0;
                for (var k = 0; k + 1 < Bins; k += 2)
                {
                    even += row[k] * buffers.Power[k];
                    odd += row[k + 1] * buffers.Power[k + 1];
                }
                even += row[Bins - 1] * buffers.Power[Bins - 1];
                var mel = Math.Max(even + odd, 1e-10f);
                data[m * frames + t] = MathF.Log10(mel);
            }
            return buffers;
        }, _ => { });

        var floor = data.Length > 0 ? data.Max() - 8.0f : 0f;
        for (var i = 0; i < data.Length; i++)
            data[i] = (Math.Max(data[i], floor) + 4.0f) / 4.0f;
        return (data, frames);
    }

    /// <summary>Frames [start, start + count) of the features, zero-padded to a full 3000-frame window.</summary>
    public float[] Window3000(float[] data, int frames, int start, int count)
    {
        var window = new float[NMels * FramesPerWindow];
        count = Math.Min(count, FramesPerWindow);
        for (var m = 0; m < NMels; m++)
            Array.Copy(data, m * frames + start, window, m * FramesPerWindow, count);
        return window;
    }

    private sealed class FrameBuffers
    {
        public readonly float[] Input = new float[NFft];
        public readonly double[] ARe = new double[N2 * N1];
        public readonly double[] AIm = new double[N2 * N1];
        public readonly float[] Power = new float[Bins];
    }

    /// <summary>|rfft(frame * window)|² for one frame, with the spectrum rounded to complex64 like numpy's.</summary>
    private static void ComputeFrame(ReadOnlySpan<float> frame, FrameBuffers b)
    {
        for (var j = 0; j < NFft; j++)
            b.Input[j] = frame[j] * Window[j]; // float32 product, as numpy multiplies two float32 arrays

        // n = N2*n1 + n2, k = k1 + N1*k2:  X[k] = sum_n2 W400^(n2 k1) W25^(n2 k2) sum_n1 x[N2 n1 + n2] W16^(n1 k1)
        for (var n2 = 0; n2 < N2; n2++)
        {
            for (var k1 = 0; k1 < N1; k1++)
            {
                double re = 0, im = 0;
                for (var n1 = 0; n1 < N1; n1++)
                {
                    var (wr, wi) = Twiddle[(n1 * k1 % N1) * N2];
                    double x = b.Input[N2 * n1 + n2];
                    re += x * wr;
                    im += x * wi;
                }
                var (tr, ti) = Twiddle[n2 * k1 % NFft];
                b.ARe[n2 * N1 + k1] = re * tr - im * ti;
                b.AIm[n2 * N1 + k1] = re * ti + im * tr;
            }
        }
        for (var k = 0; k < Bins; k++)
        {
            int k1 = k % N1, k2 = k / N1;
            double re = 0, im = 0;
            for (var n2 = 0; n2 < N2; n2++)
            {
                var (wr, wi) = Twiddle[(n2 * k2 % N2) * N1];
                double ar = b.ARe[n2 * N1 + k1], ai = b.AIm[n2 * N1 + k1];
                re += ar * wr - ai * wi;
                im += ar * wi + ai * wr;
            }
            float fr = (float)re, fi = (float)im; // .astype("complex64")
            b.Power[k] = Square(ComplexAbs(fr, fi));
        }
    }

    /// <summary>np.abs on complex64, as numpy's SIMD loop computes it: larger * sqrt(1 + (smaller / larger)²).</summary>
    private static float ComplexAbs(float re, float im)
    {
        float a = Math.Abs(re), b = Math.Abs(im);
        var (larger, smaller) = a >= b ? (a, b) : (b, a);
        if (larger == 0)
            return 0;
        var ratio = smaller / larger;
        return larger * MathF.Sqrt(1.0f + ratio * ratio);
    }

    private static float Square(float x) => x * x;

    /// <summary>exp(-2πi j / NFft) for j in [0, NFft).</summary>
    private static (double, double)[] Twiddles()
    {
        var w = new (double, double)[NFft];
        for (var j = 0; j < NFft; j++)
        {
            var angle = -2 * Math.PI * j / NFft;
            w[j] = (Math.Cos(angle), Math.Sin(angle));
        }
        return w;
    }

    /// <summary>np.hanning(401)[:-1] as float32.</summary>
    private static float[] HannWindow()
    {
        var w = new float[NFft];
        for (var i = 0; i < NFft; i++)
        {
            double n = 1 - (NFft + 1) + 2 * i; // arange(1 - M, M, 2)
            w[i] = (float)(0.5 + 0.5 * Math.Cos(Math.PI * n / NFft));
        }
        return w;
    }

    /// <summary>Slaney-style mel filter bank (librosa's mel with htk=False, norm="slaney"), in float64 then float32.</summary>
    private static float[] MelFilters(int sr, int nFft, int nMels)
    {
        var bins = nFft / 2 + 1;
        var fftFreqs = new double[bins];
        var val = 1.0 / (nFft * (1.0 / sr)); // np.fft.rfftfreq(n, d=1/sr)
        for (var k = 0; k < bins; k++)
            fftFreqs[k] = k * val;

        const double minMel = 0.0, maxMel = 45.245640471924965;
        var count = nMels + 2;
        var mels = new double[count];
        var step = (maxMel - minMel) / (count - 1); // np.linspace
        for (var i = 0; i < count; i++)
            mels[i] = i * step + minMel;
        mels[^1] = maxMel;

        const double fMin = 0.0, fSp = 200.0 / 3;
        var freqs = new double[count];
        for (var i = 0; i < count; i++)
            freqs[i] = fMin + fSp * mels[i];
        const double minLogHz = 1000.0;
        const double minLogMel = (minLogHz - fMin) / fSp;
        var logStep = Math.Log(6.4) / 27.0;
        for (var i = 0; i < count; i++)
        {
            if (mels[i] >= minLogMel)
                freqs[i] = minLogHz * Math.Exp(logStep * (mels[i] - minLogMel));
        }

        var weights = new float[nMels * bins];
        for (var m = 0; m < nMels; m++)
        {
            var enorm = 2.0 / (freqs[m + 2] - freqs[m]);
            for (var k = 0; k < bins; k++)
            {
                var lower = -(freqs[m] - fftFreqs[k]) / (freqs[m + 1] - freqs[m]);
                var upper = (freqs[m + 2] - fftFreqs[k]) / (freqs[m + 2] - freqs[m + 1]);
                weights[m * bins + k] = (float)(Math.Max(0.0, Math.Min(lower, upper)) * enorm);
            }
        }
        return weights;
    }
}
