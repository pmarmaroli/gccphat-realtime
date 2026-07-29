using GccPhat.RealTime.Analysis;

namespace GccPhat.RealTime.Shared.Tests;

/// <summary>
/// Measures the anti-alias filter through the real resampler rather than asserting on
/// coefficients: feed a pure tone at the source rate, resample, compare output RMS to input RMS.
/// Above 8 kHz a tone can only survive by aliasing, so that ratio is the alias rejection.
/// </summary>
public class AudioResamplerTests
{
    private const int OutputRate = 16_000;

    private static double Rms(float[] x)
    {
        if (x.Length == 0) return 0.0;
        double sum = 0.0;
        foreach (float v in x) sum += (double)v * v;
        return Math.Sqrt(sum / x.Length);
    }

    private static double GainDb(float[] output, double inputRms)
        => 20.0 * Math.Log10(Math.Max(Rms(output) / inputRms, 1e-30));

    /// <summary>One second of a unit sine, padded with `context` samples of it at each end.</summary>
    private static double[] Tone(double frequencyHz, int sampleRate, int context)
    {
        var x = new double[sampleRate + 2 * context];
        for (int i = 0; i < x.Length; i++)
            x[i] = Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        return x;
    }

    private static double UnitSineRms => Math.Sqrt(0.5);

    [Theory]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void ContextIsAWholeNumberOfDecimationSteps(int sampleRate)
    {
        int context = AudioResampler.ContextSamplesFor(sampleRate);
        int factor = sampleRate / OutputRate;

        Assert.True(context > 0);
        Assert.Equal(0, context % factor);
    }

    [Fact]
    public void SixteenKilohertzSourceNeedsNoContext()
        => Assert.Equal(0, AudioResampler.ContextSamplesFor(OutputRate));

    [Theory]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void OutputIsExactlyOneSecondAtSixteenKilohertz(int sampleRate)
    {
        int context = AudioResampler.ContextSamplesFor(sampleRate);

        float[] output = AudioResampler.ResampleTo16kHz(Tone(1_000.0, sampleRate, context), sampleRate, context);

        Assert.Equal(OutputRate, output.Length);
    }

    /// <summary>The band YAMNet actually uses must come through untouched.</summary>
    [Theory]
    [InlineData(48_000, 100.0)]
    [InlineData(48_000, 1_000.0)]
    [InlineData(48_000, 4_000.0)]
    [InlineData(48_000, 7_000.0)]
    [InlineData(96_000, 100.0)]
    [InlineData(96_000, 1_000.0)]
    [InlineData(96_000, 4_000.0)]
    [InlineData(96_000, 7_000.0)]
    public void PassbandIsFlat(int sampleRate, double frequencyHz)
    {
        int context = AudioResampler.ContextSamplesFor(sampleRate);

        float[] output = AudioResampler.ResampleTo16kHz(Tone(frequencyHz, sampleRate, context), sampleRate, context);

        Assert.InRange(GainDb(output, UnitSineRms), -0.5, 0.5);
    }

    /// <summary>
    /// Nothing above the 8 kHz output Nyquist may fold back into the band. The previous
    /// Hann-windowed filter only managed roughly 20-30 dB here.
    /// </summary>
    [Theory]
    [InlineData(48_000, 9_000.0)]
    [InlineData(48_000, 12_000.0)]
    [InlineData(48_000, 20_000.0)]
    [InlineData(96_000, 9_000.0)]
    [InlineData(96_000, 12_000.0)]
    [InlineData(96_000, 20_000.0)]
    public void StopbandIsRejected(int sampleRate, double frequencyHz)
    {
        int context = AudioResampler.ContextSamplesFor(sampleRate);

        float[] output = AudioResampler.ResampleTo16kHz(Tone(frequencyHz, sampleRate, context), sampleRate, context);

        Assert.True(GainDb(output, UnitSineRms) < -60.0);
    }

    /// <summary>
    /// A window resampled with its filter context must equal the same region taken from a wider
    /// resample. That is what makes successive classification windows free of edge transients.
    /// </summary>
    [Fact]
    public void ContextedWindowMatchesWiderReference()
    {
        const int sampleRate = 48_000;
        int context = AudioResampler.ContextSamplesFor(sampleRate);

        var rng = new Random(1234);
        var stream = new double[sampleRate * 3];
        for (int i = 0; i < stream.Length; i++) stream[i] = rng.NextDouble() * 2.0 - 1.0;

        int start = sampleRate; // the middle second

        var window = new double[sampleRate + 2 * context];
        Array.Copy(stream, start - context, window, 0, window.Length);
        float[] fromWindow = AudioResampler.ResampleTo16kHz(window, sampleRate, context);

        int widePad = context * 4;
        var wide = new double[sampleRate + 2 * widePad];
        Array.Copy(stream, start - widePad, wide, 0, wide.Length);
        float[] fromWide = AudioResampler.ResampleTo16kHz(wide, sampleRate, widePad);

        Assert.Equal(fromWide.Length, fromWindow.Length);
        for (int i = 0; i < fromWindow.Length; i++)
        {
            Assert.True(Math.Abs(fromWindow[i] - fromWide[i]) < 1e-6,
                $"sample {i}: {fromWindow[i]} vs {fromWide[i]}");
        }
    }

    [Fact]
    public void SixteenKilohertzSourcePassesThroughUnchanged()
    {
        var input = new double[OutputRate];
        for (int i = 0; i < input.Length; i++) input[i] = Math.Sin(2.0 * Math.PI * 440.0 * i / OutputRate);

        float[] output = AudioResampler.ResampleTo16kHz(input, OutputRate);

        Assert.Equal(input.Length, output.Length);
        for (int i = 0; i < input.Length; i++) Assert.Equal((float)input[i], output[i]);
    }

    /// <summary>
    /// Rates that are not an integer multiple of 16 kHz are rejected loudly here. Note the
    /// classification loop currently swallows this, so such a device produces no results at all -
    /// see the note in LINUX_SETUP.md.
    /// </summary>
    [Theory]
    [InlineData(44_100)]
    [InlineData(141_120)] // XMOS ultrasonic capture
    public void UnsupportedRatesThrow(int sampleRate)
    {
        Assert.Throws<NotSupportedException>(() => AudioResampler.ContextSamplesFor(sampleRate));
        Assert.Throws<NotSupportedException>(() => AudioResampler.ResampleTo16kHz(new double[sampleRate], sampleRate));
    }
}
