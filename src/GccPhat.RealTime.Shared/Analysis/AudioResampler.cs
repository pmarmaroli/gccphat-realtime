using System;

namespace GccPhat.RealTime.Analysis;

/// <summary>
/// Downsample double-precision PCM to 16 kHz float for YAMNet input.
/// Uses a windowed-sinc FIR anti-alias filter followed by integer decimation.
/// Supported source rates: any integer multiple of 16000 (16, 32, 48, 96 kHz).
/// </summary>
internal static class AudioResampler
{
    // Lazily cached FIR per decimation factor.
    private static float[]? s_fir3;  // 48 → 16 kHz (÷3)
    private static float[]? s_fir6;  // 96 → 16 kHz (÷6)

    /// <summary>
    /// Downsample <paramref name="input"/> (sampled at <paramref name="sourceSampleRate"/> Hz) to 16 kHz.
    /// </summary>
    public static float[] ResampleTo16kHz(double[] input, int sourceSampleRate)
    {
        if (sourceSampleRate == 16000)
            return ToFloat(input);

        if (sourceSampleRate % 16000 != 0)
            throw new NotSupportedException(
                $"Classification requires the device sample rate to be a multiple of 16 000 Hz (got {sourceSampleRate} Hz).");

        int factor = sourceSampleRate / 16000;
        float[] fir = GetFir(factor, sourceSampleRate);
        return DecimateWithFir(input, fir, factor);
    }

    private static float[] ToFloat(double[] input)
    {
        var f = new float[input.Length];
        for (int i = 0; i < input.Length; i++) f[i] = (float)input[i];
        return f;
    }

    private static float[] DecimateWithFir(double[] input, float[] fir, int factor)
    {
        int inLen = input.Length;
        int outLen = inLen / factor;
        var output = new float[outLen];
        int half = fir.Length / 2;

        for (int outIdx = 0; outIdx < outLen; outIdx++)
        {
            int center = outIdx * factor;
            double acc = 0.0;
            for (int k = 0; k < fir.Length; k++)
            {
                int inIdx = center - half + k;
                if ((uint)inIdx < (uint)inLen)
                    acc += input[inIdx] * fir[k];
            }
            output[outIdx] = (float)acc;
        }
        return output;
    }

    private static float[] GetFir(int factor, int sourceSampleRate) => factor switch
    {
        3 => s_fir3 ??= DesignLowPass(64, cutoffHz: 7000.0, sourceSampleRate),
        6 => s_fir6 ??= DesignLowPass(128, cutoffHz: 7000.0, sourceSampleRate),
        _ => DesignLowPass(64 * factor, cutoffHz: 7000.0, sourceSampleRate)
    };

    private static float[] DesignLowPass(int n, double cutoffHz, double sampleRateHz)
    {
        var h = new float[n];
        double fc = cutoffHz / sampleRateHz; // normalized [0, 0.5]
        double M = (n - 1) / 2.0;
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double x = i - M;
            double hann = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1));
            double sinc = x == 0.0 ? 2.0 * fc : Math.Sin(2.0 * Math.PI * fc * x) / (Math.PI * x);
            h[i] = (float)(sinc * hann);
            sum += h[i];
        }
        // Normalise DC gain to 1.0
        for (int i = 0; i < n; i++) h[i] /= (float)sum;
        return h;
    }
}
