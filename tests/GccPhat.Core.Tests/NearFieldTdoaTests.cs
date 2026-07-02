using System;
using GccPhat.Core;
using Xunit;

namespace GccPhat.Core.Tests;

/// <summary>
/// Coverage for the near-field point-source TDOA formula used by the array-sync cross-check
/// feature. Ground truth is re-derived independently in each test (not by calling the method under
/// test twice), so a sign or distance-formula regression is actually caught.
/// </summary>
public class NearFieldTdoaTests
{
    private const double SpeedOfSound = 343.0;

    [Fact]
    public void SymmetricPlacement_GivesZeroDelay()
    {
        // Mics straddle the origin on the X axis; source is straight ahead on the Y axis, so both
        // mics are equidistant from it by construction.
        double delay = NearFieldTdoa.PredictedDelaySeconds(
            micX1: -0.05, micY1: 0.0,
            micX2: 0.05, micY2: 0.0,
            sourceX: 0.0, sourceY: 5.0);

        Assert.True(Math.Abs(delay) < 1e-9,
            $"Expected ~0 s delay for a source equidistant from both mics, got {delay:E3} s.");
    }

    [Theory]
    [InlineData(0.0, 5.0)]
    [InlineData(3.0, -2.0)]
    [InlineData(-10.0, 0.001)]
    public void CoincidentMics_AlwaysGiveZeroDelay(double sourceX, double sourceY)
    {
        double delay = NearFieldTdoa.PredictedDelaySeconds(
            micX1: 0.02, micY1: -0.01,
            micX2: 0.02, micY2: -0.01,
            sourceX, sourceY);

        Assert.True(Math.Abs(delay) < 1e-12,
            $"Two coincident mics must have zero TDOA for any source position, got {delay:E3} s for source ({sourceX}, {sourceY}).");
    }

    [Fact]
    public void FarSource_ConvergesToFarFieldPlaneWaveFormula()
    {
        // Baseline of 10 cm on the X axis; source far away along a 30 degree bearing. As distance
        // grows much larger than the baseline, the near-field formula must converge to the standard
        // far-field plane-wave approximation: delay ~= -(dx*cos(theta) + dy*sin(theta)) / c.
        const double micX1 = -0.05, micY1 = 0.0;
        const double micX2 = 0.05, micY2 = 0.0;
        const double thetaDeg = 30.0;
        double theta = thetaDeg * Math.PI / 180.0;
        double dx = micX2 - micX1;
        double dy = micY2 - micY1;
        double farFieldDelay = -(dx * Math.Cos(theta) + dy * Math.Sin(theta)) / SpeedOfSound;

        const double distance = 10.0; // 100x the 0.1 m baseline
        double sourceX = distance * Math.Cos(theta);
        double sourceY = distance * Math.Sin(theta);

        double nearFieldDelay = NearFieldTdoa.PredictedDelaySeconds(micX1, micY1, micX2, micY2, sourceX, sourceY);

        double relativeError = Math.Abs(nearFieldDelay - farFieldDelay) / Math.Abs(farFieldDelay);
        Assert.True(relativeError < 0.001,
            $"Expected near-field delay ({nearFieldDelay:E6} s) to converge to the far-field approximation ({farFieldDelay:E6} s) within 0.1% at distance={distance} m, got relative error {relativeError:P4}.");
    }

    [Fact]
    public void SourceCloserToMic2_GivesNegativeDelay()
    {
        // Mics at x=-1 and x=+1; source strictly between them but closer to mic2.
        const double micX1 = -1.0, micY1 = 0.0;
        const double micX2 = 1.0, micY2 = 0.0;
        const double sourceX = 0.5, sourceY = 0.0;

        double d1 = Math.Sqrt(Math.Pow(sourceX - micX1, 2) + Math.Pow(sourceY - micY1, 2));
        double d2 = Math.Sqrt(Math.Pow(sourceX - micX2, 2) + Math.Pow(sourceY - micY2, 2));
        Assert.True(d2 < d1, "Test setup sanity check: source at x=0.5 should be closer to mic2 (x=1) than mic1 (x=-1).");

        double delay = NearFieldTdoa.PredictedDelaySeconds(micX1, micY1, micX2, micY2, sourceX, sourceY);

        Assert.True(delay < 0.0,
            $"Expected a negative delay (mic2 is closer to the source, so its wavefront arrives sooner), got {delay:E6} s.");
    }

    [Theory]
    [InlineData(343.0, 1500.0)]
    [InlineData(343.0, 200.0)]
    public void DelayScalesInverselyWithSpeedOfSound(double speedA, double speedB)
    {
        const double micX1 = -0.05, micY1 = 0.0;
        const double micX2 = 0.05, micY2 = 0.0;
        const double sourceX = 2.0, sourceY = 1.0;

        double delayA = NearFieldTdoa.PredictedDelaySeconds(micX1, micY1, micX2, micY2, sourceX, sourceY, speedA);
        double delayB = NearFieldTdoa.PredictedDelaySeconds(micX1, micY1, micX2, micY2, sourceX, sourceY, speedB);

        double expectedRatio = speedB / speedA;
        double actualRatio = delayA / delayB;
        Assert.True(Math.Abs(actualRatio - expectedRatio) < 1e-9,
            $"Delay should scale as 1/speedOfSound: expected ratio delayA/delayB={expectedRatio:F6}, got {actualRatio:F6}.");
    }
}
