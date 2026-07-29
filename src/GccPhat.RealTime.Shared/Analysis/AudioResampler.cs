using System;

namespace GccPhat.RealTime.Analysis;

/// <summary>
/// Downsample double-precision PCM to 16 kHz float for YAMNet input.
/// Uses a Kaiser-windowed-sinc FIR anti-alias filter followed by integer decimation.
/// Supported source rates: any integer multiple of 16000 (16, 32, 48, 96 kHz).
///
/// The filter is specified against YAMNet's 8 kHz Nyquist: flat to <see cref="PassbandEdgeHz"/>,
/// at least <see cref="StopbandAttenuationDb"/> down by <see cref="StopbandEdgeHz"/>, so content
/// above 8 kHz cannot fold back into the band the model actually looks at.
///
/// Callers should pass a block carrying <see cref="ContextSamplesFor"/> extra source samples at
/// each end. Those feed the filter but are not emitted, so every window is convolved with real
/// neighbouring audio rather than zero-padding — no per-window edge transients.
/// </summary>
internal static class AudioResampler
{
    private const double PassbandEdgeHz = 7200.0;
    private const double StopbandEdgeHz = 8000.0; // YAMNet Nyquist
    private const double StopbandAttenuationDb = 70.0;

    private const double CutoffHz = (PassbandEdgeHz + StopbandEdgeHz) / 2.0; // -6 dB point
    private const double TransitionWidthHz = StopbandEdgeHz - PassbandEdgeHz;

    // Lazily cached FIR per decimation factor. The factor determines the source rate
    // (sourceRate = factor * 16000), so caching on the factor alone is unambiguous.
    private static float[]? s_fir3;  // 48 → 16 kHz (÷3)
    private static float[]? s_fir6;  // 96 → 16 kHz (÷6)

    /// <summary>
    /// Extra source samples the caller must supply at <em>each</em> end of the window it wants
    /// resampled, so the anti-alias filter never runs off the end of real audio. Zero when the
    /// source is already 16 kHz (no filtering needed).
    /// </summary>
    public static int ContextSamplesFor(int sourceSampleRate)
    {
        if (sourceSampleRate == 16000)
            return 0;

        RequireSupportedRate(sourceSampleRate);
        int factor = sourceSampleRate / 16000;
        int half = GetFir(factor, sourceSampleRate).Length / 2;

        // Round up to a whole number of decimation steps so the output phase is unaffected.
        return (half + factor - 1) / factor * factor;
    }

    /// <summary>
    /// Downsample <paramref name="input"/> (sampled at <paramref name="sourceSampleRate"/> Hz) to
    /// 16 kHz, emitting only the central region — the first and last
    /// <paramref name="contextSamples"/> source samples act as filter context and are not emitted.
    /// Pass 0 to resample the whole block with zero-padded edges (legacy behaviour).
    /// </summary>
    public static float[] ResampleTo16kHz(double[] input, int sourceSampleRate, int contextSamples = 0)
    {
        if (contextSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(contextSamples));

        int coreLength = input.Length - 2 * contextSamples;
        if (coreLength <= 0)
            return Array.Empty<float>();

        if (sourceSampleRate == 16000)
            return ToFloat(input, contextSamples, coreLength);

        RequireSupportedRate(sourceSampleRate);

        int factor = sourceSampleRate / 16000;
        float[] fir = GetFir(factor, sourceSampleRate);
        return DecimateWithFir(input, fir, factor, contextSamples, coreLength);
    }

    private static void RequireSupportedRate(int sourceSampleRate)
    {
        if (sourceSampleRate % 16000 != 0)
            throw new NotSupportedException(
                $"Classification requires the device sample rate to be a multiple of 16 000 Hz (got {sourceSampleRate} Hz).");
    }

    private static float[] ToFloat(double[] input, int offset, int count)
    {
        var f = new float[count];
        for (int i = 0; i < count; i++) f[i] = (float)input[offset + i];
        return f;
    }

    private static float[] DecimateWithFir(double[] input, float[] fir, int factor, int contextSamples, int coreLength)
    {
        int inLen = input.Length;
        int outLen = coreLength / factor;
        if (outLen <= 0) return Array.Empty<float>();

        var output = new float[outLen];
        int taps = fir.Length;
        int half = taps / 2;

        // With at least `half` samples of context every tap lands inside the buffer, so the inner
        // loop needs no bounds check. Without it, fall back to zero-padding at the edges.
        bool hasFullContext = contextSamples >= half;

        for (int outIdx = 0; outIdx < outLen; outIdx++)
        {
            int baseIdx = contextSamples + outIdx * factor - half;
            double acc = 0.0;

            if (hasFullContext)
            {
                for (int k = 0; k < taps; k++)
                    acc += input[baseIdx + k] * fir[k];
            }
            else
            {
                for (int k = 0; k < taps; k++)
                {
                    int inIdx = baseIdx + k;
                    if ((uint)inIdx < (uint)inLen)
                        acc += input[inIdx] * fir[k];
                }
            }

            output[outIdx] = (float)acc;
        }
        return output;
    }

    private static float[] GetFir(int factor, int sourceSampleRate) => factor switch
    {
        3 => s_fir3 ??= DesignKaiserLowPass(sourceSampleRate),
        6 => s_fir6 ??= DesignKaiserLowPass(sourceSampleRate),
        _ => DesignKaiserLowPass(sourceSampleRate)
    };

    /// <summary>
    /// Kaiser-windowed sinc low-pass, sized from the transition width and stopband attenuation via
    /// Kaiser's standard design formulas. Odd length, so it is linear phase with an integer group
    /// delay of (n - 1) / 2.
    /// </summary>
    private static float[] DesignKaiserLowPass(double sampleRateHz)
    {
        double deltaOmega = 2.0 * Math.PI * TransitionWidthHz / sampleRateHz;
        int n = (int)Math.Ceiling((StopbandAttenuationDb - 8.0) / (2.285 * deltaOmega)) + 1;
        if (n % 2 == 0) n++;

        double beta = StopbandAttenuationDb > 50.0
            ? 0.1102 * (StopbandAttenuationDb - 8.7)
            : 0.5842 * Math.Pow(StopbandAttenuationDb - 21.0, 0.4) + 0.07886 * (StopbandAttenuationDb - 21.0);

        var h = new float[n];
        double fc = CutoffHz / sampleRateHz; // normalized cycles/sample
        double half = (n - 1) / 2.0;
        double i0Beta = BesselI0(beta);
        double sum = 0.0;

        for (int i = 0; i < n; i++)
        {
            double x = i - half;
            double sinc = x == 0.0 ? 2.0 * fc : Math.Sin(2.0 * Math.PI * fc * x) / (Math.PI * x);
            double r = x / half;
            double window = BesselI0(beta * Math.Sqrt(Math.Max(0.0, 1.0 - r * r))) / i0Beta;
            double v = sinc * window;
            h[i] = (float)v;
            sum += v;
        }

        // Normalise DC gain to 1.0
        for (int i = 0; i < n; i++) h[i] /= (float)sum;
        return h;
    }

    /// <summary>Modified Bessel function of the first kind, order 0: I0(x) = Σ ((x/2)^k / k!)².</summary>
    private static double BesselI0(double x)
    {
        double sum = 1.0;
        double term = 1.0;
        double halfX = x / 2.0;
        for (int k = 1; k < 64; k++)
        {
            term *= halfX / k;
            double squared = term * term;
            sum += squared;
            if (squared < sum * 1e-17) break;
        }
        return sum;
    }
}
