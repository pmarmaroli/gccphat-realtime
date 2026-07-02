using System;

namespace GccPhat.Core
{
    /// <summary>
    /// Theoretical (continuous-time) array-factor math for a delay-and-sum-style beamformer.
    /// Shared between <c>RealTimeEngine</c> (actual audio steering) and any UI that wants to plot
    /// the array's theoretical polar response, so the two can never disagree.
    /// </summary>
    public static class BeamPatternCalculator
    {
        private const double SpeedOfSound = 343.0; // m/s

        /// <summary>
        /// Steering delay (seconds) to apply to a microphone at (x, y) so that a plane wave arriving
        /// from <paramref name="azimuthDeg"/> (0° = +X, counter-clockwise positive) sums coherently.
        /// </summary>
        public static double SteeringDelaySeconds(double x, double y, double azimuthDeg)
        {
            double theta = azimuthDeg * Math.PI / 180.0;
            return (x * Math.Cos(theta) + y * Math.Sin(theta)) / SpeedOfSound;
        }

        /// <summary>
        /// Evaluates the array factor at a single frequency over incoming azimuths 0..360°, given
        /// per-microphone compensation delays (seconds) and weights. This mirrors exactly what
        /// <see cref="Beamformer.Process"/> does in the frequency domain: each channel's spectrum is
        /// shifted by <c>-2*pi*f*delay</c> and summed with its weight.
        /// </summary>
        public static double[] ComputeArrayFactor(double[] micX, double[] micY, double[] delaysSeconds, double[] weights, double frequencyHz, double azimuthStepDeg)
        {
            if (micX is null) throw new ArgumentNullException(nameof(micX));
            if (micY is null) throw new ArgumentNullException(nameof(micY));
            if (delaysSeconds is null) throw new ArgumentNullException(nameof(delaysSeconds));
            if (weights is null) throw new ArgumentNullException(nameof(weights));
            int m = micX.Length;
            if (micY.Length != m || delaysSeconds.Length != m || weights.Length != m)
            {
                throw new ArgumentException("micX, micY, delaysSeconds and weights must all have the same length");
            }
            if (azimuthStepDeg <= 0.0) throw new ArgumentOutOfRangeException(nameof(azimuthStepDeg));

            int steps = Math.Max(1, (int)Math.Round(360.0 / azimuthStepDeg));
            var magnitudes = new double[steps];

            for (int s = 0; s < steps; s++)
            {
                double thetaIn = s * azimuthStepDeg * Math.PI / 180.0;
                double uxIn = Math.Cos(thetaIn);
                double uyIn = Math.Sin(thetaIn);

                double re = 0.0, im = 0.0;
                for (int i = 0; i < m; i++)
                {
                    double travel = (micX[i] * uxIn + micY[i] * uyIn) / SpeedOfSound;
                    double phase = 2.0 * Math.PI * frequencyHz * (travel - delaysSeconds[i]);
                    re += weights[i] * Math.Cos(phase);
                    im += weights[i] * Math.Sin(phase);
                }

                magnitudes[s] = Math.Sqrt(re * re + im * im);
            }

            return magnitudes;
        }
    }
}
