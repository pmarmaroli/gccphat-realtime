using System;
using System.Numerics;

namespace GccPhat.Core;

/// <summary>Result of a single GCC-PHAT estimation.</summary>
/// <param name="DelayMs">
/// Estimated time delay in milliseconds. Sign convention (same as the gccphat CLI):
/// a negative delay means channel 2 is delayed relative to channel 1, a positive delay
/// means channel 1 is delayed relative to channel 2.
/// </param>
/// <param name="Rms">RMS energy of channel 1 within the analysed band (Parseval, frequency domain).</param>
/// <param name="LevelA">Linear RMS level of the raw channel-1 frame (≈ full-scale 0..1).</param>
/// <param name="LevelB">Linear RMS level of the raw channel-2 frame (≈ full-scale 0..1).</param>
/// <param name="Coherence">
/// Reliability of the delay estimate: the magnitude of the normalised cross-correlation
/// coefficient of the two raw frames aligned at the estimated lag, in [0, 1].
/// 1 ≈ the channels are (delayed) copies of each other; ~0 ≈ unrelated / no common source.
/// </param>
/// <param name="ZeroLagCorrelation">
/// Signed Pearson correlation of the two channels WITHOUT shifting, in [-1, 1]. Used to tell
/// a genuine dual signal apart from a "mono duplicated onto both channels" feed:
/// 1.0 ≈ identical channels (mono/false stereo); lower values ≈ genuinely distinct channels.
/// </param>
/// <param name="DifferenceRatio">
/// Inter-channel difference RMS(A - B) / RMS(A). 0 ⇒ the two channels are bit-identical;
/// larger values ⇒ the channels carry different signals.
/// </param>
public readonly record struct DelayEstimate(
    double DelayMs,
    double Rms,
    double LevelA,
    double LevelB,
    double Coherence,
    double ZeroLagCorrelation,
    double DifferenceRatio);

/// <summary>
/// Generalized Cross-Correlation with Phase Transform (GCC-PHAT) time-delay estimator,
/// after Knapp and Carter (1976).
///
/// This is an instance-based, allocation-free port of the original gccphat algorithm:
/// all frequency axes, band masks, the exponential lookup table and the FFT engine are
/// pre-computed once in the constructor, and per-call work reuses instance scratch buffers.
///
/// A single instance is bound to one (nfft, fs, fmin, fmax) configuration and is NOT
/// thread-safe. For concurrent analysis of several channel pairs, create one analyzer
/// per pair (or per worker thread).
/// </summary>
public sealed class GccPhatAnalyzer
{
    private const int ExpNumPoints = 360;

    private readonly int _nfft;
    private readonly int _fs;
    private readonly bool _phat;
    private readonly Fft2 _fft;

    // Pre-computed configuration.
    private readonly int[] _outBandIndex;
    private readonly int _outBandCount;
    private readonly Complex[] _expTable;
    private readonly double _expStep;

    // Per-instance scratch (single-threaded use).
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly double[] _absBuf;
    private readonly Complex[] _fS1;
    private readonly Complex[] _fS2;
    private readonly double[] _gRe;
    private readonly double[] _gIm;

    public int Nfft => _nfft;
    public int SampleRate => _fs;

    public GccPhatAnalyzer(int nfft, int fs, int fmin, int fmax, bool phat = true)
    {
        if (nfft <= 1 || (nfft & (nfft - 1)) != 0)
        {
            throw new ArgumentException("nfft must be a power of two greater than 1.", nameof(nfft));
        }
        if (fs <= 0)
        {
            throw new ArgumentException("Sample rate must be positive.", nameof(fs));
        }

        _nfft = nfft;
        _fs = fs;
        _phat = phat;
        _fft = new Fft2((uint)Math.Log2(nfft));

        double[] vfc = BuildFrequencyAxis(nfft, fs);

        var outBand = new int[nfft];
        int oc = 0;
        for (int i = 0; i < nfft; i++)
        {
            double v = vfc[i];
            bool inBand = (v >= fmin && v <= fmax) || (v <= -fmin && v >= -fmax);
            bool outOfBand = (v < fmin && v > -fmin) || (v < -fmax || v > fmax);
            if (!inBand && outOfBand)
            {
                outBand[oc++] = i;
            }
        }
        _outBandIndex = outBand;
        _outBandCount = oc;

        _expStep = 2.0 * Math.PI / ExpNumPoints;
        _expTable = new Complex[ExpNumPoints];
        for (int i = 0; i < ExpNumPoints; i++)
        {
            double phase = i * _expStep - Math.PI; // -π .. π
            _expTable[i] = Complex.Exp(Complex.ImaginaryOne * phase);
        }

        _re = new double[nfft];
        _im = new double[nfft];
        _absBuf = new double[nfft];
        _fS1 = new Complex[nfft];
        _fS2 = new Complex[nfft];
        _gRe = new double[nfft];
        _gIm = new double[nfft];
    }

    /// <summary>
    /// Estimates the time delay between two equally-sized frames (length must equal <see cref="Nfft"/>).
    /// </summary>
    public DelayEstimate Process(double[] channel1, double[] channel2)
    {
        if (channel1 is null) throw new ArgumentNullException(nameof(channel1));
        if (channel2 is null) throw new ArgumentNullException(nameof(channel2));
        if (channel1.Length != _nfft || channel2.Length != _nfft)
        {
            throw new ArgumentException($"Both frames must have length {_nfft}.");
        }

        double rms1 = ComputeCorrelation(channel1, channel2);

        int n = _nfft;
        int half = n / 2;
        int maxIndex = 0;
        double maxG = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            int shifted = (i + half) % n;
            double value = _gRe[shifted];
            if (value > maxG)
            {
                maxG = value;
                maxIndex = i;
            }
        }

        double delayMs = ((maxIndex - half) / (double)_fs) * 1000.0;

        double levelA = RmsLevel(channel1);
        double levelB = RmsLevel(channel2);
        double coherence = NormalizedCrossCorrelation(channel1, channel2, maxIndex - half);
        (double zeroLag, double diffRatio) = CompareChannels(channel1, channel2);

        return new DelayEstimate(delayMs, rms1, levelA, levelB, coherence, zeroLag, diffRatio);
    }

    /// <summary>
    /// Computes the centred GCC-PHAT cross-correlation of two equally-sized frames (length must
    /// equal <see cref="Nfft"/>) into <paramref name="dst"/> (also length <see cref="Nfft"/>):
    /// <c>dst[i]</c> is the correlation at integer lag <c>i - Nfft/2</c>, so the centre bin is the
    /// zero-lag value. This is the per-pair input consumed by <see cref="SrpPhatLocalizer"/>.
    /// </summary>
    public void CrossCorrelation(double[] channel1, double[] channel2, double[] dst)
    {
        if (channel1 is null) throw new ArgumentNullException(nameof(channel1));
        if (channel2 is null) throw new ArgumentNullException(nameof(channel2));
        if (dst is null) throw new ArgumentNullException(nameof(dst));
        if (channel1.Length != _nfft || channel2.Length != _nfft || dst.Length != _nfft)
        {
            throw new ArgumentException($"All frames must have length {_nfft}.");
        }

        ComputeCorrelation(channel1, channel2);
        int n = _nfft;
        int half = n / 2;
        for (int i = 0; i < n; i++)
        {
            dst[i] = _gRe[(i + half) % n];
        }
    }

    // Builds the PHAT cross-spectrum of the two channels and inverse-transforms it into the
    // time-domain correlation _gRe (lag 0 at index 0, wrapped). Returns RMS of channel 1.
    private double ComputeCorrelation(double[] channel1, double[] channel2)
    {
        double rms1 = Colore(channel1, _fS1);
        Colore(channel2, _fS2);

        int n = _nfft;
        for (int i = 0; i < n; i++)
        {
            Complex pxy = _fS1[i] * Complex.Conjugate(_fS2[i]);
            if (_phat)
            {
                double abs = Complex.Abs(pxy);
                double denom = abs < 1e-6 ? 1e-6 : abs;
                _gRe[i] = (pxy / new Complex(denom, 0.0)).Real;
                _gIm[i] = (pxy / new Complex(denom, 0.0)).Imaginary;
            }
            else
            {
                _gRe[i] = pxy.Real;
                _gIm[i] = pxy.Imaginary;
            }
        }

        _fft.Run(_gRe, _gIm, inverse: true);
        return rms1;
    }

    private static double RmsLevel(double[] frame)
    {
        double sum = 0.0;
        for (int i = 0; i < frame.Length; i++)
        {
            sum += frame[i] * frame[i];
        }
        return Math.Sqrt(sum / frame.Length);
    }

    // Compares the two channels WITHOUT shifting, to decide whether they are the same signal
    // (mono duplicated onto both channels) or genuinely distinct (true dual / stereo):
    //   zeroLag    = signed Pearson correlation at lag 0, in [-1, 1] (1 ⇒ identical).
    //   diffRatio  = RMS(A - B) / RMS(A) (0 ⇒ bit-identical channels).
    private static (double zeroLag, double diffRatio) CompareChannels(double[] a, double[] b)
    {
        int n = a.Length;
        double sab = 0.0;
        double saa = 0.0;
        double sbb = 0.0;
        double sdiff = 0.0;
        for (int i = 0; i < n; i++)
        {
            double av = a[i];
            double bv = b[i];
            sab += av * bv;
            saa += av * av;
            sbb += bv * bv;
            double d = av - bv;
            sdiff += d * d;
        }

        double zeroLag = (saa <= 1e-12 || sbb <= 1e-12) ? 0.0 : sab / Math.Sqrt(saa * sbb);
        double diffRatio = saa <= 1e-12 ? 0.0 : Math.Sqrt(sdiff / saa);
        return (zeroLag, diffRatio);
    }

    // |Pearson-style normalised cross-correlation| of the two frames aligned at the given
    // integer lag (in samples), evaluated over the overlapping region only. Result in [0, 1].
    private static double NormalizedCrossCorrelation(double[] a, double[] b, int lag)
    {
        int n = a.Length;
        int lo = Math.Max(0, lag);
        int hi = Math.Min(n, n + lag);

        double cab = 0.0;
        double caa = 0.0;
        double cbb = 0.0;
        for (int i = lo; i < hi; i++)
        {
            double av = a[i];
            double bv = b[i - lag];
            cab += av * bv;
            caa += av * av;
            cbb += bv * bv;
        }

        if (caa <= 1e-12 || cbb <= 1e-12)
        {
            return 0.0;
        }
        double rho = cab / Math.Sqrt(caa * cbb);
        return Math.Min(1.0, Math.Abs(rho));
    }

    /// <summary>One-shot helper: builds a throwaway analyzer and estimates a single delay.</summary>
    public static DelayEstimate Estimate(double[] channel1, double[] channel2, int fs, int fmin, int fmax, bool phat = true)
    {
        return new GccPhatAnalyzer(channel1.Length, fs, fmin, fmax, phat).Process(channel1, channel2);
    }

    // Applies the band mask and PHAT-style colouring (magnitude = |Re(FFT)|, original phase),
    // writes the coloured spectrum into <paramref name="outSpectrum"/> and returns its RMS.
    private double Colore(double[] sigin, Complex[] outSpectrum)
    {
        int n = _nfft;
        Array.Copy(sigin, _re, n);
        Array.Clear(_im, 0, n);
        _fft.Run(_re, _im);

        double[] abs = _absBuf;
        for (int i = 0; i < n; i++)
        {
            abs[i] = Math.Abs(_re[i]);
        }
        for (int k = 0; k < _outBandCount; k++)
        {
            abs[_outBandIndex[k]] = 0.0;
        }

        double sumOfSquares = 0.0;
        for (int i = 0; i < n; i++)
        {
            double phase = Math.Atan2(_im[i], _re[i]);
            Complex sig = abs[i] * ExpFromLookup(phase);
            outSpectrum[i] = sig;
            double magnitude = sig.Magnitude;
            sumOfSquares += magnitude * magnitude;
        }

        return Math.Sqrt(sumOfSquares / n);
    }

    private Complex ExpFromLookup(double phase)
    {
        // Nearest-neighbour index on the uniform [-π, π] grid (O(1); ties to the higher index).
        int index = (int)Math.Round((phase + Math.PI) / _expStep, MidpointRounding.AwayFromZero);
        if (index < 0)
        {
            index = 0;
        }
        else if (index >= ExpNumPoints)
        {
            index = ExpNumPoints - 1;
        }
        return _expTable[index];
    }

    private static double[] BuildFrequencyAxis(int nfft, int fs)
    {
        int half = nfft / 2;
        var vfc = new double[nfft];
        for (int i = 0; i <= half; i++)
        {
            vfc[i] = (double)i * fs / 2 / half;
        }
        int pos = half + 1;
        for (int i = half - 1; i >= 1; i--)
        {
            vfc[pos++] = -((double)i * fs / 2 / half);
        }
        return vfc;
    }
}
