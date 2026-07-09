using System;
using GccPhat.Core;
using Xunit;

namespace GccPhat.Core.Tests;

public class SrpPhatLocalizerTests
{
    private const int Fs = 48000;
    private const int Nfft = 4096;
    private const double C = 343.0;

    // Square 2D array (0.2 m side), three independent pairs, far-field azimuth scan.
    private static readonly double[] MicX = { -0.1, 0.1, 0.1, -0.1 };
    private static readonly double[] MicY = { -0.1, -0.1, 0.1, 0.1 };
    private static readonly (int a, int b)[] Pairs = { (0, 1), (1, 2), (0, 2) };

    // Linear array, 4 mics, 4 cm spacing, along X (Y=0) — the default broadside preset.
    private static readonly double[] MicXLinear = { -0.06, -0.02, 0.02, 0.06 };
    private static readonly double[] MicYLinear = { 0.0, 0.0, 0.0, 0.0 };
    private static readonly (int a, int b)[] PairsLinear = { (0, 1), (1, 2), (2, 3), (0, 3) };

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(90.0)]
    [InlineData(200.0)]
    [InlineData(315.0)]
    public void Estimate_RecoversFarFieldAzimuth(double trueAz)
    {
        double[][] corr = SimulateCorrelations(trueAz);
        var loc = new SrpPhatLocalizer(MicX, MicY, Pairs, Fs, C);

        SrpEstimate est = loc.Estimate(corr, Nfft / 2);

        double err = Math.Abs(((est.AzimuthDeg - trueAz + 540.0) % 360.0) - 180.0);
        Assert.True(err <= 6.0, $"Expected ~{trueAz}°, got {est.AzimuthDeg}° (err {err}°).");
    }

    [Fact]
    public void Constructor_RejectsMismatchedGeometry()
    {
        Assert.Throws<ArgumentException>(() =>
            new SrpPhatLocalizer(new[] { 0.0, 0.1 }, new[] { 0.0 }, Pairs, Fs));
    }

    [Fact]
    public void Estimate_RejectsWrongPairCount()
    {
        var loc = new SrpPhatLocalizer(MicX, MicY, Pairs, Fs);
        Assert.Throws<ArgumentException>(() => loc.Estimate(new[] { new double[Nfft] }, Nfft / 2));
    }

    [Fact]
    public void Constructor_DetectsFrontBackAmbiguity_ForCollinearArrayOnly()
    {
        Assert.False(new SrpPhatLocalizer(MicX, MicY, Pairs, Fs).HasFrontBackAmbiguity);
        Assert.True(new SrpPhatLocalizer(MicXLinear, MicYLinear, PairsLinear, Fs).HasFrontBackAmbiguity);
    }

    [Theory]
    [InlineData(210.0)]
    [InlineData(270.0)]
    [InlineData(330.0)]
    public void Estimate_LinearArray_RecoversFrontHalfAzimuth(double trueAz)
    {
        // Front half is [180°, 360°): the user is assumed south of the array (mics at north).
        double[][] corr = SimulateCorrelations(MicXLinear, MicYLinear, PairsLinear, trueAz);
        var loc = new SrpPhatLocalizer(MicXLinear, MicYLinear, PairsLinear, Fs, C);

        SrpEstimate est = loc.Estimate(corr, Nfft / 2);

        Assert.InRange(est.AzimuthDeg, 180.0, 360.0);
        Assert.True(Math.Abs(est.AzimuthDeg - trueAz) <= 6.0, $"Expected ~{trueAz}°, got {est.AzimuthDeg}°.");
    }

    [Fact]
    public void Estimate_LinearArray_FoldsBackHalfSourceOntoFrontMirror()
    {
        // A source physically behind the array (20°, north) is indistinguishable from its front
        // mirror (360° - 20° = 340°); the restricted search must report the front-half angle.
        double[][] corr = SimulateCorrelations(MicXLinear, MicYLinear, PairsLinear, 20.0);
        var loc = new SrpPhatLocalizer(MicXLinear, MicYLinear, PairsLinear, Fs, C);

        SrpEstimate est = loc.Estimate(corr, Nfft / 2);

        Assert.InRange(est.AzimuthDeg, 180.0, 360.0);
        Assert.True(Math.Abs(est.AzimuthDeg - 340.0) <= 8.0, $"Expected ~340°, got {est.AzimuthDeg}°.");
    }

    // Builds per-pair centred GCC-PHAT correlations from broadband noise delayed by each pair's
    // far-field geometric delay for the given azimuth, using the real analyzer.
    private static double[][] SimulateCorrelations(double azDeg) => SimulateCorrelations(MicX, MicY, Pairs, azDeg);

    private static double[][] SimulateCorrelations(double[] micX, double[] micY, (int a, int b)[] pairs, double azDeg)
    {
        var rng = new Random(1234);
        var source = new double[Nfft * 3];
        for (int i = 0; i < source.Length; i++) source[i] = rng.NextDouble() * 2.0 - 1.0;

        double rad = azDeg * Math.PI / 180.0;
        double dx = Math.Cos(rad), dy = Math.Sin(rad);

        var analyzer = new GccPhatAnalyzer(Nfft, Fs, 200, 8000);
        var corr = new double[pairs.Length][];
        for (int p = 0; p < pairs.Length; p++)
        {
            double dax = micX[pairs[p].a] - micX[pairs[p].b];
            double day = micY[pairs[p].a] - micY[pairs[p].b];
            int lag = (int)Math.Round(Fs * (dax * dx + day * dy) / C);

            double[] a = Frame(source, 1000);
            double[] b = Frame(source, 1000 - lag);
            corr[p] = new double[Nfft];
            analyzer.CrossCorrelation(a, b, corr[p]);
        }
        return corr;
    }

    private static double[] Frame(double[] src, int start)
    {
        var f = new double[Nfft];
        for (int i = 0; i < Nfft; i++) f[i] = src[start + i];
        return f;
    }
}
