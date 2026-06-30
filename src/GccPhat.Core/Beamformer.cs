using System;

namespace GccPhat.Core
{
    /// <summary>
    /// Simple frequency-domain delay-and-sum beamformer. Uses Fft2 for FFT/IFFT and applies
    /// per-channel fractional-sample delays as phase rotations in the frequency domain.
    /// </summary>
    public sealed class Beamformer
    {
        private readonly int _n;
        private readonly int _fs;
        private readonly Fft2 _fft;
        private readonly double[] _re; // temporary real buffer
        private readonly double[] _im; // temporary imag buffer
        private readonly double[] _accRe;
        private readonly double[] _accIm;

        // Optional spatial-passband: outside [fmin, fmax] the array gives no useful directivity
        // (too low) or aliases (too high), so those bins can be zeroed before the inverse FFT.
        private bool _bandLimited;
        private int _binLo;
        private int _binHi;

        public Beamformer(int nfft, int sampleRate)
        {
            if ((nfft & (nfft - 1)) != 0) throw new ArgumentException("nfft must be a power of two", nameof(nfft));
            _n = nfft;
            _fs = sampleRate;
            _fft = new Fft2((uint)Math.Log2(nfft));
            _re = new double[_n];
            _im = new double[_n];
            _accRe = new double[_n];
            _accIm = new double[_n];
        }

        /// <summary>
        /// Restricts the beamformed output to the spatial passband [fmin, fmax] (Hz). Pass a band
        /// covering the whole spectrum (fmin &lt;= 0 and fmax &gt;= Nyquist) to disable band-limiting.
        /// </summary>
        public void SetBand(double fmin, double fmax)
        {
            double nyquist = _fs / 2.0;
            if (fmin <= 0.0 && fmax >= nyquist)
            {
                _bandLimited = false;
                return;
            }
            _binLo = Math.Max(0, (int)Math.Ceiling(fmin * _n / _fs));
            _binHi = Math.Min(_n / 2, (int)Math.Floor(fmax * _n / _fs));
            _bandLimited = _binLo <= _binHi;
        }

        /// <summary>
        /// Runs the beamformer over the supplied per-channel time-domain frames.
        /// channels.Length = number of channels. Each channel array must have length = nfft.
        /// delays contains per-channel delays in fractional samples (can be positive/negative).
        /// The output array must have length nfft and will receive the real-valued beamformed signal.
        /// </summary>
        public void Process(double[][] channels, double[] delays, double[] output)
        {
            if (channels is null) throw new ArgumentNullException(nameof(channels));
            Process(channels, delays, CreateUnitWeights(channels.Length), output);
        }

        /// <summary>
        /// Runs the beamformer over the supplied per-channel frames with an explicit per-channel
        /// weight vector. Delays and weights are applied in the frequency domain before the inverse FFT.
        /// </summary>
        public void Process(double[][] channels, double[] delays, double[] weights, double[] output)
        {
            if (channels is null) throw new ArgumentNullException(nameof(channels));
            if (delays is null) throw new ArgumentNullException(nameof(delays));
            if (weights is null) throw new ArgumentNullException(nameof(weights));
            if (output is null) throw new ArgumentNullException(nameof(output));
            int m = channels.Length;
            if (m == 0) throw new ArgumentException("channels must not be empty", nameof(channels));
            if (delays.Length != m) throw new ArgumentException("delays length must match channels length", nameof(delays));
            if (weights.Length != m) throw new ArgumentException("weights length must match channels length", nameof(weights));
            if (output.Length != _n) throw new ArgumentException("output length must equal nfft", nameof(output));

            // zero the accumulation buffer in freq domain
            Array.Clear(_accRe, 0, _n);
            Array.Clear(_accIm, 0, _n);

            for (int ch = 0; ch < m; ch++)
            {
                double[] frame = channels[ch];
                if (frame.Length != _n) throw new ArgumentException("each channel frame must have length nfft", nameof(channels));

                // copy real input into _re and zero imag
                Array.Copy(frame, _re, _n);
                Array.Clear(_im, 0, _n);

                // forward FFT
                _fft.Run(_re, _im, inverse: false);

                // apply phase shift for fractional delay
                double delay = delays[ch];
                double weight = weights[ch];
                if (Math.Abs(delay) > 0.0)
                {
                    // Use the SIGNED frequency index so the phase ramp is antisymmetric about Nyquist
                    // (bins above N/2 represent negative frequencies). This keeps the spectrum
                    // Hermitian-symmetric after the shift, so the IFFT yields a real-valued signal.
                    int half = _n / 2;
                    for (int k = 0; k < _n; k++)
                    {
                        int kk = k <= half ? k : k - _n; // signed bin: 0..N/2 positive, rest negative
                        double phase = -2.0 * Math.PI * kk * delay / _n;
                        double c = Math.Cos(phase);
                        double s = Math.Sin(phase);
                        double a = _re[k];
                        double b = _im[k];
                        double r = (a * c - b * s) * weight;
                        double im = (a * s + b * c) * weight;
                        // accumulate
                        _accRe[k] += r;
                        _accIm[k] += im;
                    }
                }
                else
                {
                    for (int k = 0; k < _n; k++)
                    {
                        _accRe[k] += _re[k] * weight;
                        _accIm[k] += _im[k] * weight;
                    }
                }
            }

            // spatial passband: zero bins outside [binLo, binHi] and their Hermitian mirrors
            if (_bandLimited)
            {
                for (int k = 0; k < _n; k++)
                {
                    int f = k <= _n / 2 ? k : _n - k; // |bin| index (0..N/2)
                    if (f < _binLo || f > _binHi)
                    {
                        _accRe[k] = 0.0;
                        _accIm[k] = 0.0;
                    }
                }
            }

            // inverse FFT into output
            // copy accumulation into temp buffers
            Array.Copy(_accRe, _re, _n);
            Array.Copy(_accIm, _im, _n);

            _fft.Run(_re, _im, inverse: true);

            // result is in _re (real part)
            for (int i = 0; i < _n; i++)
            {
                output[i] = _re[i];
            }
        }

        private static double[] CreateUnitWeights(int count)
        {
            var weights = new double[count];
            Array.Fill(weights, 1.0);
            return weights;
        }
    }
}
