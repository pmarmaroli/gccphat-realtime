using System;

namespace GccPhat.Core;

/// <summary>
/// In-place complex FFT (radix-2, decimation-in-frequency).
///
/// Originally released under the MIT License, Copyright (c) 2010 Gerald T. Beauregard.
/// Reworked here to use flat reusable <see cref="double"/> buffers (instead of an array of
/// objects) so that a single instance can be reused across calls without per-call allocation.
///
/// An instance is NOT thread-safe: it owns internal scratch buffers. Use one instance per thread.
/// </summary>
public sealed class Fft2
{
    private readonly uint _logN;
    private readonly uint _n;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly uint[] _revTgt;

    public Fft2(uint logN)
    {
        _logN = logN;
        _n = 1u << (int)logN;
        _re = new double[_n];
        _im = new double[_n];
        _revTgt = new uint[_n];
        for (uint i = 0; i < _n; i++)
        {
            _revTgt[i] = BitReverse(i, _logN);
        }
    }

    public uint Length => _n;

    /// <summary>
    /// Runs an in-place complex FFT (or inverse FFT) over the supplied real/imaginary buffers.
    /// </summary>
    public void Run(double[] xRe, double[] xIm, bool inverse = false)
    {
        double scale = inverse ? 1.0 / _n : 1.0;
        double[] re = _re;
        double[] im = _im;
        for (uint i = 0; i < _n; i++)
        {
            re[i] = scale * xRe[i];
            im[i] = scale * xIm[i];
        }

        uint numFlies = _n >> 1;
        uint span = _n >> 1;
        uint spacing = _n;
        uint wIndexStep = 1;

        for (uint stage = 0; stage < _logN; stage++)
        {
            double wAngleInc = wIndexStep * 2.0 * Math.PI / _n;
            if (!inverse)
            {
                wAngleInc = -wAngleInc;
            }

            double wMulRe = Math.Cos(wAngleInc);
            double wMulIm = Math.Sin(wAngleInc);

            for (uint start = 0; start < _n; start += spacing)
            {
                double wRe = 1.0;
                double wIm = 0.0;

                for (uint flyCount = 0; flyCount < numFlies; flyCount++)
                {
                    uint topIndex = start + flyCount;
                    uint botIndex = topIndex + span;

                    double topRe = re[topIndex];
                    double topIm = im[topIndex];
                    double botRe = re[botIndex];
                    double botIm = im[botIndex];

                    re[topIndex] = topRe + botRe;
                    im[topIndex] = topIm + botIm;

                    double diffRe = topRe - botRe;
                    double diffIm = topIm - botIm;

                    re[botIndex] = diffRe * wRe - diffIm * wIm;
                    im[botIndex] = diffRe * wIm + diffIm * wRe;

                    double tRe = wRe;
                    wRe = wRe * wMulRe - wIm * wMulIm;
                    wIm = tRe * wMulIm + wIm * wMulRe;
                }
            }

            numFlies >>= 1;
            span >>= 1;
            spacing >>= 1;
            wIndexStep <<= 1;
        }

        for (uint i = 0; i < _n; i++)
        {
            uint target = _revTgt[i];
            xRe[target] = re[i];
            xIm[target] = im[i];
        }
    }

    private static uint BitReverse(uint x, uint numBits)
    {
        uint y = 0;
        for (uint i = 0; i < numBits; i++)
        {
            y <<= 1;
            y |= x & 1;
            x >>= 1;
        }
        return y;
    }
}
