using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ChatHost.Stt;

/// <summary>PCM helpers: anything in → 16 kHz / 16-bit / mono out.</summary>
public static class AudioConvert
{
    public const int TargetRate = 16_000;

    /// <summary>Decode a WAV blob (any PCM/float format NAudio understands) to 16k mono PCM16.</summary>
    public static byte[] WavTo16kMono(byte[] wav)
    {
        using var ms = new MemoryStream(wav);
        using var reader = new WaveFileReader(ms);
        var f = reader.WaveFormat;
        if (f.SampleRate == TargetRate && f.Channels == 1 && f.BitsPerSample == 16 && f.Encoding == WaveFormatEncoding.Pcm)
            return ReadAll(reader);
        return Resample(reader.ToSampleProvider());
    }

    /// <summary>Raw PCM16 at an arbitrary rate/channel count → 16k mono PCM16.</summary>
    public static byte[] Pcm16To16kMono(byte[] pcm, int sampleRate, int channels)
    {
        if (sampleRate == TargetRate && channels == 1) return pcm;
        var fmt = new WaveFormat(sampleRate, 16, channels);
        using var raw = new RawSourceWaveStream(new MemoryStream(pcm), fmt);
        return Resample(raw.ToSampleProvider());
    }

    private static byte[] Resample(ISampleProvider provider)
    {
        if (provider.WaveFormat.Channels > 1) provider = new StereoToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != TargetRate) provider = new WdlResamplingSampleProvider(provider, TargetRate);
        var pcm16 = provider.ToWaveProvider16();
        using var outMs = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = pcm16.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, n);
        return outMs.ToArray();
    }

    private static byte[] ReadAll(WaveStream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Wrap 16k mono PCM16 into a WAV container (for saving debug captures).</summary>
    public static byte[] PcmToWav(byte[] pcm, int rate = TargetRate, int channels = 1)
    {
        using var ms = new MemoryStream();
        using (var w = new WaveFileWriter(ms, new WaveFormat(rate, 16, channels)))
            w.Write(pcm, 0, pcm.Length);
        return ms.ToArray();
    }

    /// <summary>Peak / RMS in dBFS for a quick "was there any signal" log line.</summary>
    public static (double peakDb, double rmsDb) Levels(byte[] pcm)
    {
        if (pcm.Length < 2) return (-100, -100);
        double peak = 0, sum = 0; int n = pcm.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768.0;
            peak = Math.Max(peak, Math.Abs(s));
            sum += s * s;
        }
        double rms = Math.Sqrt(sum / n);
        return (20 * Math.Log10(Math.Max(peak, 1e-6)), 20 * Math.Log10(Math.Max(rms, 1e-6)));
    }
}

/// <summary>
/// IMA ADPCM (4-bit) decoder. Each block from the device is self-contained:
///   [predictor int16 LE][step index u8][reserved u8][nibbles: low nibble first]
/// so a lost BLE notification only costs one block, not the rest of the utterance.
/// </summary>
public static class ImaAdpcm
{
    private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107,
        118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876,
        963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871,
        5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385,
        24623, 27086, 29794, 32767
    };

    public static void DecodeBlock(ReadOnlySpan<byte> block, Stream pcmOut)
    {
        if (block.Length < 4) return;
        int predictor = (short)(block[0] | (block[1] << 8));
        int index = Math.Clamp((int)block[2], 0, 88);
        Span<byte> two = stackalloc byte[2];
        for (int i = 4; i < block.Length; i++)
        {
            byte b = block[i];
            Step(b & 0x0F, ref predictor, ref index, two, pcmOut);
            Step(b >> 4, ref predictor, ref index, two, pcmOut);
        }
    }

    private static void Step(int nibble, ref int predictor, ref int index, Span<byte> two, Stream outp)
    {
        int step = StepTable[index];
        int diff = step >> 3;
        if ((nibble & 1) != 0) diff += step >> 2;
        if ((nibble & 2) != 0) diff += step >> 1;
        if ((nibble & 4) != 0) diff += step;
        if ((nibble & 8) != 0) predictor -= diff; else predictor += diff;
        predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[nibble], 0, 88);
        two[0] = (byte)predictor; two[1] = (byte)(predictor >> 8);
        outp.Write(two);
    }

    /// <summary>Split 16-bit PCM into self-contained ADPCM blocks of `samplesPerBlock` samples each.</summary>
    public static List<byte[]> EncodeBlocks(byte[] pcm16, int samplesPerBlock)
    {
        var shorts = new short[pcm16.Length / 2];
        for (int i = 0; i < shorts.Length; i++) shorts[i] = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
        var blocks = new List<byte[]>();
        int pred = 0, idx = 0;
        for (int off = 0; off < shorts.Length; off += samplesPerBlock)
        {
            int n = Math.Min(samplesPerBlock, shorts.Length - off);
            if ((n & 1) == 1) n--;                       // blocks hold whole byte pairs
            if (n <= 0) break;
            blocks.Add(EncodeBlock(shorts.AsSpan(off, n), ref pred, ref idx));
        }
        return blocks;
    }

    /// <summary>Encoder (used by the self-test endpoint and useful as the reference for the firmware side).</summary>
    public static byte[] EncodeBlock(ReadOnlySpan<short> samples, ref int predictor, ref int index)
    {
        var outp = new byte[4 + (samples.Length + 1) / 2];
        outp[0] = (byte)predictor; outp[1] = (byte)(predictor >> 8); outp[2] = (byte)index; outp[3] = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int step = StepTable[index];
            int diff = samples[i] - predictor;
            int nibble = 0;
            if (diff < 0) { nibble = 8; diff = -diff; }
            int vp = step >> 3;
            if (diff >= step) { nibble |= 4; diff -= step; vp += step; }
            if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; vp += step >> 1; }
            if (diff >= step >> 2) { nibble |= 1; vp += step >> 2; }
            predictor = Math.Clamp((nibble & 8) != 0 ? predictor - vp : predictor + vp, short.MinValue, short.MaxValue);
            index = Math.Clamp(index + IndexTable[nibble], 0, 88);
            if ((i & 1) == 0) outp[4 + i / 2] = (byte)nibble; else outp[4 + i / 2] |= (byte)(nibble << 4);
        }
        return outp;
    }
}
