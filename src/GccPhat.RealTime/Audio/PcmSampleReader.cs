using System;
using NAudio.Wave;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Builds a per-sample decode delegate for a given <see cref="WaveFormat"/> (PCM 16/24/32-bit or
/// IEEE float). Shared by <see cref="MultichannelCapture"/> (live capture) and
/// <see cref="WavFileReplayCapture"/> (file replay) so both decode audio identically.
/// </summary>
internal static class PcmSampleReader
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static Func<byte[], int, double> BuildReader(WaveFormat format)
    {
        int bits = format.BitsPerSample;
        bool isFloat = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => true,
            WaveFormatEncoding.Pcm => false,
            WaveFormatEncoding.Extensible when format is WaveFormatExtensible ext => ext.SubFormat == SubtypeIeeeFloat,
            _ => throw new NotSupportedException($"Unsupported capture encoding: {format.Encoding}.")
        };

        if (isFloat && bits == 32)
        {
            return static (buf, off) => BitConverter.ToSingle(buf, off);
        }

        return bits switch
        {
            16 => static (buf, off) => BitConverter.ToInt16(buf, off) / 32768.0,
            24 => static (buf, off) =>
            {
                int sample = buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16);
                if ((sample & 0x800000) != 0)
                {
                    sample |= unchecked((int)0xFF000000);
                }
                return sample / 8388608.0;
            },
            32 => static (buf, off) => BitConverter.ToInt32(buf, off) / 2147483648.0,
            _ => throw new NotSupportedException($"Unsupported capture format: {format.Encoding} {bits}-bit.")
        };
    }
}
