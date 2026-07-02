using System;

namespace GccPhat.Core;

/// <summary>
/// Near-field (point-source) time-difference-of-arrival math. Unlike
/// <see cref="BeamPatternCalculator.SteeringDelaySeconds"/> (far-field plane-wave, no range term),
/// this accounts for the actual distance from each microphone to a known source position — needed
/// when the source is only a few array apertures away, e.g. a calibration clap or a triangulated fix.
/// </summary>
public static class NearFieldTdoa
{
    private const double SpeedOfSound = 343.0; // m/s, matches BeamPatternCalculator's constant

    /// <summary>
    /// Predicted delay (seconds) of arrival at mic 2 relative to mic 1, for a point source at
    /// (<paramref name="sourceX"/>, <paramref name="sourceY"/>). All coordinates are metres in the
    /// same global frame. Positive means mic 2 is farther from the source than mic 1 (its wavefront
    /// arrives later).
    /// </summary>
    public static double PredictedDelaySeconds(
        double micX1, double micY1,
        double micX2, double micY2,
        double sourceX, double sourceY,
        double speedOfSound = SpeedOfSound)
    {
        double d1 = Math.Sqrt(Math.Pow(sourceX - micX1, 2) + Math.Pow(sourceY - micY1, 2));
        double d2 = Math.Sqrt(Math.Pow(sourceX - micX2, 2) + Math.Pow(sourceY - micY2, 2));
        return (d2 - d1) / speedOfSound;
    }
}
