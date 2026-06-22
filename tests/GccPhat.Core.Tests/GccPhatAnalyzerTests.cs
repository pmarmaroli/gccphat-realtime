using System;
using System.Collections.Generic;
using System.IO;
using GccPhat.Core;
using NAudio.Wave;
using Xunit;

namespace GccPhat.Core.Tests;

public class GccPhatAnalyzerTests
{
    private const int BufferSize = 4096;
    private const int Fmin = 200;
    private const int Fmax = 8000;

    // Known result produced by the gccphat CLI on the bundled stereo_noise.wav:
    // a constant -10.0208333... ms delay (a -481 sample shift at 48 kHz) on every window.
    private const double ExpectedDelayMs = -481.0 / 48000.0 * 1000.0;

    [Fact]
    public void Process_StereoNoise_MatchesCliReference()
    {
        (double[] left, double[] right, int fs) = ReadStereo(AssetPath("stereo_noise.wav"));

        var analyzer = new GccPhatAnalyzer(BufferSize, fs, Fmin, Fmax);

        int numBuffers = left.Length / BufferSize;
        Assert.True(numBuffers >= 10, $"Expected at least 10 windows, got {numBuffers}.");

        var leftFrame = new double[BufferSize];
        var rightFrame = new double[BufferSize];
        for (int i = 0; i < numBuffers; i++)
        {
            Array.Copy(left, i * BufferSize, leftFrame, 0, BufferSize);
            Array.Copy(right, i * BufferSize, rightFrame, 0, BufferSize);

            DelayEstimate result = analyzer.Process(leftFrame, rightFrame);

            Assert.True(
                Math.Abs(result.DelayMs - ExpectedDelayMs) < 1e-9,
                $"Window {i}: expected {ExpectedDelayMs} ms, got {result.DelayMs} ms.");
            Assert.True(result.Rms > 0, $"Window {i}: RMS should be positive.");
        }
    }

    [Fact]
    public void StaticEstimate_AgreesWithInstance()
    {
        (double[] left, double[] right, int fs) = ReadStereo(AssetPath("stereo_noise.wav"));
        var leftFrame = new double[BufferSize];
        var rightFrame = new double[BufferSize];
        Array.Copy(left, 0, leftFrame, 0, BufferSize);
        Array.Copy(right, 0, rightFrame, 0, BufferSize);

        DelayEstimate instance = new GccPhatAnalyzer(BufferSize, fs, Fmin, Fmax).Process(leftFrame, rightFrame);
        DelayEstimate oneShot = GccPhatAnalyzer.Estimate(leftFrame, rightFrame, fs, Fmin, Fmax);

        Assert.Equal(instance.DelayMs, oneShot.DelayMs, 12);
        Assert.Equal(instance.Rms, oneShot.Rms, 12);
    }

    [Fact]
    public void Constructor_RejectsNonPowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new GccPhatAnalyzer(3000, 48000, Fmin, Fmax));
    }

    private static string AssetPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static (double[] left, double[] right, int sampleRate) ReadStereo(string path)
    {
        using var reader = new AudioFileReader(path);
        Assert.Equal(2, reader.WaveFormat.Channels);

        int sampleCount = (int)(reader.Length / sizeof(float));
        var samples = new float[sampleCount];
        int read = reader.Read(samples, 0, sampleCount);

        int frames = read / 2;
        var left = new double[frames];
        var right = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = samples[2 * i];
            right[i] = samples[2 * i + 1];
        }
        return (left, right, reader.WaveFormat.SampleRate);
    }
}
