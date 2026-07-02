using System;
using GccPhat.Core;
using Xunit;

namespace GccPhat.Core.Tests;

/// <summary>
/// Regression coverage for the delay-and-sum steering sign: the physical arriving signal for each
/// channel is built from first principles (independent of <see cref="BeamPatternCalculator"/>), then
/// run through the real <see cref="Beamformer"/> FFT pipeline. This can catch a sign inversion in
/// either the steering-delay formula or the array-factor math, unlike a test that reuses the same
/// helper on both sides of the comparison.
/// </summary>
public class BeamPatternCalculatorTests
{
    private const double SpeedOfSound = 343.0;

    private static (double[] x, double[] y) HexagonPlusCenter(double radiusMeters)
    {
        var x = new double[7];
        var y = new double[7];
        for (int i = 0; i < 6; i++)
        {
            double a = 2.0 * Math.PI * i / 6.0;
            x[i] = radiusMeters * Math.Cos(a);
            y[i] = radiusMeters * Math.Sin(a);
        }
        // x[6], y[6] default to 0.0 — the center microphone.
        return (x, y);
    }

    private static double[] PhysicalChannelSignal(double micX, double micY, double arrivalAzimuthDeg, double frequencyHz, int fs, int n)
    {
        double theta = arrivalAzimuthDeg * Math.PI / 180.0;
        double travelSeconds = (micX * Math.Cos(theta) + micY * Math.Sin(theta)) / SpeedOfSound;
        var signal = new double[n];
        for (int k = 0; k < n; k++)
        {
            double t = k / (double)fs;
            signal[k] = Math.Cos(2.0 * Math.PI * frequencyHz * (t + travelSeconds));
        }
        return signal;
    }

    private static double Rms(double[] signal, int skip)
    {
        double sum = 0.0;
        int count = 0;
        for (int i = skip; i < signal.Length - skip; i++)
        {
            sum += signal[i] * signal[i];
            count++;
        }
        return Math.Sqrt(sum / count);
    }

    [Theory]
    [InlineData(40.0)]
    [InlineData(200.0)]
    public void SteeringAtTrueArrivalAzimuth_OutperformsSteeringAtTheOppositeAzimuth(double arrivalAzimuthDeg)
    {
        const int fs = 48000;
        const int n = 4096;
        const double frequencyHz = 3000.0;
        (double[] micX, double[] micY) = HexagonPlusCenter(0.04);
        int channelCount = micX.Length;

        var channels = new double[channelCount][];
        for (int i = 0; i < channelCount; i++)
        {
            channels[i] = PhysicalChannelSignal(micX[i], micY[i], arrivalAzimuthDeg, frequencyHz, fs, n);
        }

        double RmsForSteering(double steerAzimuthDeg)
        {
            var delays = new double[channelCount];
            var weights = new double[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                delays[i] = BeamPatternCalculator.SteeringDelaySeconds(micX[i], micY[i], steerAzimuthDeg) * fs;
                weights[i] = 1.0 / channelCount;
            }

            var beamformer = new Beamformer(n, fs);
            var output = new double[n];
            beamformer.Process(channels, delays, weights, output);
            return Rms(output, skip: n / 4);
        }

        double rmsOnAxis = RmsForSteering(arrivalAzimuthDeg);
        double rmsOpposite = RmsForSteering(arrivalAzimuthDeg + 180.0);

        // Perfectly aligned unit-amplitude cosines summed with weights totalling 1 give RMS = 1/sqrt(2).
        Assert.True(rmsOnAxis > 0.65,
            $"Expected near-full coherent gain (~0.707 RMS) steering at the true arrival azimuth {arrivalAzimuthDeg}°, got RMS={rmsOnAxis:F3}.");
        Assert.True(rmsOnAxis > rmsOpposite * 1.5,
            $"Steering at the true arrival azimuth ({arrivalAzimuthDeg}°) should clearly outperform steering at the opposite azimuth ({arrivalAzimuthDeg + 180}°), but got RMS on-axis={rmsOnAxis:F3} vs opposite={rmsOpposite:F3}.");
    }
}
