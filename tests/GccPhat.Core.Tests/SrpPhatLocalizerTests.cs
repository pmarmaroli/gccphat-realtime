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

    // Builds per-pair centred GCC-PHAT correlations from broadband noise delayed by each pair's
    // far-field geometric delay for the given azimuth, using the real analyzer.
    private static double[][] SimulateCorrelations(double azDeg)
    {
        var rng = new Random(1234);
        var source = new double[Nfft * 3];
        for (int i = 0; i < source.Length; i++) source[i] = rng.NextDouble() * 2.0 - 1.0;

        double rad = azDeg * Math.PI / 180.0;
        double dx = Math.Cos(rad), dy = Math.Sin(rad);

        var analyzer = new GccPhatAnalyzer(Nfft, Fs, 200, 8000);
        var corr = new double[Pairs.Length][];
        for (int p = 0; p < Pairs.Length; p++)
        {
            double dax = MicX[Pairs[p].a] - MicX[Pairs[p].b];
            double day = MicY[Pairs[p].a] - MicY[Pairs[p].b];
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
