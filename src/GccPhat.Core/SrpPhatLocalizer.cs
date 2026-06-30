using System;

namespace GccPhat.Core;

/// <summary>Result of a single SRP-PHAT direction-of-arrival scan.</summary>
/// <param name="AzimuthDeg">Estimated azimuth of the source, in degrees [0, 360).</param>
/// <param name="Power">Steered response power at the winning azimuth (sum of per-pair correlations).</param>
public readonly record struct SrpEstimate(double AzimuthDeg, double Power);

/// <summary>
/// Steered Response Power with Phase Transform (SRP-PHAT) far-field azimuth localizer.
///
/// For every candidate direction the localizer sums the GCC-PHAT cross-correlation of each
/// microphone pair sampled at that direction's geometric inter-mic delay, and keeps the azimuth
/// with the highest cumulated power. The geometric delays are pre-computed once per coarse
/// azimuth (a delay look-up table), and the search is coarse-to-fine: a sparse full-circle scan
/// followed by a dense refinement around the best coarse bin. This keeps the per-frame cost a
/// handful of look-ups + sums, which is what makes multi-pair SRP-PHAT viable in real time.
///
/// Geometry is 2D and in metres; sources are assumed far enough that the wavefront is planar
/// (direction-of-arrival only). An instance is bound to one geometry/configuration and is NOT
/// thread-safe (it owns scratch buffers); use one per worker.
/// </summary>
public sealed class SrpPhatLocalizer
{
    private readonly double[] _micX;
    private readonly double[] _micY;
    private readonly int[] _pairA;
    private readonly int[] _pairB;
    private readonly int _fs;
    private readonly double _c;

    private readonly double _coarseStepDeg;
    private readonly double _fineStepDeg;
    private readonly double _fineRangeDeg;

    private readonly int _coarseCount;
    private readonly double[] _coarseAz;
    // _lut[pair * _coarseCount + bin] = inter-mic lag (in fractional samples) for that pair/azimuth.
    private readonly double[] _lut;

    public int PairCount => _pairA.Length;

    /// <param name="micX">X coordinate of each microphone, in metres.</param>
    /// <param name="micY">Y coordinate of each microphone, in metres.</param>
    /// <param name="pairs">Index pairs (a, b) into the microphone arrays to correlate.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="soundSpeed">Speed of sound in m/s.</param>
    /// <param name="coarseStepDeg">Azimuth step of the full-circle coarse scan.</param>
    /// <param name="fineStepDeg">Azimuth step of the refinement scan.</param>
    /// <param name="fineRangeDeg">Half-width (deg) of the refinement window around the best coarse bin.</param>
    public SrpPhatLocalizer(
        double[] micX,
        double[] micY,
        (int a, int b)[] pairs,
        int sampleRate,
        double soundSpeed = 343.0,
        double coarseStepDeg = 5.0,
        double fineStepDeg = 0.5,
        double fineRangeDeg = 6.0)
    {
        if (micX is null) throw new ArgumentNullException(nameof(micX));
        if (micY is null) throw new ArgumentNullException(nameof(micY));
        if (pairs is null) throw new ArgumentNullException(nameof(pairs));
        if (micX.Length != micY.Length) throw new ArgumentException("micX and micY must be the same length.");
        if (micX.Length < 2) throw new ArgumentException("At least two microphones are required.");
        if (pairs.Length == 0) throw new ArgumentException("At least one pair is required.", nameof(pairs));
        if (sampleRate <= 0) throw new ArgumentException("Sample rate must be positive.", nameof(sampleRate));
        if (soundSpeed <= 0) throw new ArgumentException("Sound speed must be positive.", nameof(soundSpeed));
        if (coarseStepDeg <= 0 || coarseStepDeg > 90) throw new ArgumentException("coarseStepDeg out of range.", nameof(coarseStepDeg));
        if (fineStepDeg <= 0) throw new ArgumentException("fineStepDeg must be positive.", nameof(fineStepDeg));

        _micX = micX;
        _micY = micY;
        _pairA = new int[pairs.Length];
        _pairB = new int[pairs.Length];
        for (int p = 0; p < pairs.Length; p++)
        {
            if (pairs[p].a < 0 || pairs[p].a >= micX.Length || pairs[p].b < 0 || pairs[p].b >= micX.Length)
            {
                throw new ArgumentException("Pair references a non-existent microphone.", nameof(pairs));
            }
            _pairA[p] = pairs[p].a;
            _pairB[p] = pairs[p].b;
        }

        _fs = sampleRate;
        _c = soundSpeed;
        _coarseStepDeg = coarseStepDeg;
        _fineStepDeg = fineStepDeg;
        _fineRangeDeg = fineRangeDeg;

        _coarseCount = (int)Math.Round(360.0 / coarseStepDeg);
        _coarseAz = new double[_coarseCount];
        _lut = new double[pairs.Length * _coarseCount];
        for (int bin = 0; bin < _coarseCount; bin++)
        {
            double az = bin * coarseStepDeg;
            _coarseAz[bin] = az;
            for (int p = 0; p < pairs.Length; p++)
            {
                _lut[p * _coarseCount + bin] = LagSamples(p, az);
            }
        }
    }

    /// <summary>
    /// Far-field inter-mic lag (in samples) of pair <paramref name="p"/> for an azimuth, matching
    /// the centred-lag convention of <see cref="GccPhatAnalyzer.CrossCorrelation"/> (same sign as
    /// the analyzer's DelayMs: negative when mic A leads mic B).
    /// </summary>
    private double LagSamples(int p, double azDeg)
    {
        double rad = azDeg * Math.PI / 180.0;
        double dx = Math.Cos(rad);
        double dy = Math.Sin(rad);
        double dax = _micX[_pairA[p]] - _micX[_pairB[p]];
        double day = _micY[_pairA[p]] - _micY[_pairB[p]];
        return -_fs * (dax * dx + day * dy) / _c;
    }

    /// <summary>
    /// Scans azimuth coarse-to-fine over the supplied per-pair centred cross-correlations and
    /// returns the azimuth with the highest steered response power. Each correlation must have the
    /// same length and be ordered like this localizer's pairs; <paramref name="corrHalf"/> is the
    /// zero-lag bin (e.g. <c>nfft / 2</c>).
    /// </summary>
    public SrpEstimate Estimate(double[][] correlations, int corrHalf)
    {
        if (correlations is null) throw new ArgumentNullException(nameof(correlations));
        if (correlations.Length != _pairA.Length)
        {
            throw new ArgumentException($"Expected {_pairA.Length} correlation arrays.", nameof(correlations));
        }
        int len = correlations[0].Length;

        int bestBin = 0;
        double bestPower = double.MinValue;
        for (int bin = 0; bin < _coarseCount; bin++)
        {
            double power = 0.0;
            for (int p = 0; p < _pairA.Length; p++)
            {
                power += Sample(correlations[p], corrHalf + _lut[p * _coarseCount + bin], len);
            }
            if (power > bestPower)
            {
                bestPower = power;
                bestBin = bin;
            }
        }

        double centre = _coarseAz[bestBin];
        double bestAz = centre;
        for (double off = -_fineRangeDeg; off <= _fineRangeDeg + 1e-9; off += _fineStepDeg)
        {
            double az = centre + off;
            double power = 0.0;
            for (int p = 0; p < _pairA.Length; p++)
            {
                power += Sample(correlations[p], corrHalf + LagSamples(p, az), len);
            }
            if (power > bestPower)
            {
                bestPower = power;
                bestAz = az;
            }
        }

        bestAz %= 360.0;
        if (bestAz < 0) bestAz += 360.0;
        return new SrpEstimate(bestAz, bestPower);
    }

    // Linearly interpolates a correlation array at a fractional index, clamping to the valid range.
    private static double Sample(double[] corr, double index, int len)
    {
        if (index <= 0) return corr[0];
        if (index >= len - 1) return corr[len - 1];
        int i0 = (int)index;
        double frac = index - i0;
        return corr[i0] + (corr[i0 + 1] - corr[i0]) * frac;
    }
}
