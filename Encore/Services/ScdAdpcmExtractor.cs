using System;
using System.IO;

namespace Encore.Services;

// MS-ADPCM decoder for SCD format 12 (sound/*.scd). Used when mods ship music as SFX
// rather than BGM, since BPM analysis would otherwise fall back to the 120 BPM default.
internal static class ScdAdpcmExtractor
{
    // standard MS-ADPCM step-delta scaling. 230 = 0.9 in 8.8 fixed-point (no change).
    private static readonly int[] AdaptTable =
    {
        230, 230, 230, 230, 307, 409, 512, 614,
        768, 614, 512, 409, 307, 230, 230, 230
    };

    public readonly struct Result
    {
        public readonly float[] Mono;
        public readonly int SampleRate;
        public Result(float[] mono, int sr) { Mono = mono; SampleRate = sr; }
        public bool IsValid => Mono != null && Mono.Length > 0 && SampleRate > 0;
    }

    public static Result ExtractFirstAdpcmAsMono(byte[] scdBytes, float maxSeconds = 60f)
    {
        if (scdBytes == null || scdBytes.Length < 256) return default;
        try
        {
            using var ms = new MemoryStream(scdBytes);
            using var br = new BinaryReader(ms);

            var magic = br.ReadBytes(4);
            if (magic[0] != (byte)'S' || magic[1] != (byte)'E') return default;
            br.BaseStream.Position = 0x0E;
            ushort binHeaderSize = br.ReadUInt16();
            br.BaseStream.Position = binHeaderSize;
            br.ReadUInt16(); br.ReadUInt16();
            ushort audioCount = br.ReadUInt16();
            br.ReadUInt16(); br.ReadUInt32();
            uint audioOffset = br.ReadUInt32();
            if (audioCount == 0) return default;

            br.BaseStream.Position = audioOffset;
            uint entry0 = br.ReadUInt32();
            br.BaseStream.Position = entry0;

            uint size = br.ReadUInt32();
            uint channel = br.ReadUInt32();
            uint rate = br.ReadUInt32();
            int format = br.ReadInt32();
            br.ReadUInt32(); br.ReadUInt32();
            uint subInfoSize = br.ReadUInt32();
            uint flg = br.ReadUInt32();
            const int MsAdpcmFormat = 12;
            if (format != MsAdpcmFormat) return default;
            if (channel == 0 || channel > 2 || rate < 8000 || size < 64) return default;

            long subInfoStart = br.BaseStream.Position;
            if ((flg & 0x01) != 0)
            {
                br.BaseStream.Position = subInfoStart;
                br.ReadUInt32();
                uint mcSize = br.ReadUInt32();
                br.BaseStream.Position = subInfoStart + mcSize;
            }

            // AdpcmWaveFormat header (matches Lumina struct)
            ushort formatTag = br.ReadUInt16();
            short channels = br.ReadInt16();
            int samplesPerSec = br.ReadInt32();
            int avgBytesPerSec = br.ReadInt32();
            short blockAlign = br.ReadInt16();
            ushort bitsPerSample = br.ReadUInt16();
            short cbSize = br.ReadInt16();
            ushort samplesPerBlock = br.ReadUInt16();
            ushort numCoef = br.ReadUInt16();
            var coef1 = new short[numCoef];
            var coef2 = new short[numCoef];
            for (int i = 0; i < numCoef; i++)
            {
                coef1[i] = br.ReadInt16();
                coef2[i] = br.ReadInt16();
            }

            br.BaseStream.Position = subInfoStart + subInfoSize;

            int sr = samplesPerSec > 0 ? samplesPerSec : (int)rate;
            int ch = channels > 0 ? channels : (int)channel;
            if (blockAlign <= 0 || samplesPerBlock <= 0 || ch <= 0 || ch > 2)
                return default;

            // ~60s is enough for tempo detection; full songs are 4-5 MB
            long maxBytesToRead = (long)((sr * maxSeconds) * (blockAlign / (float)samplesPerBlock));
            long bytesAvailable = scdBytes.LongLength - br.BaseStream.Position;
            long readLen = Math.Min(Math.Min((long)size, bytesAvailable), maxBytesToRead);
            if (readLen <= 0) return default;
            var adpcmData = br.ReadBytes((int)readLen);

            int blocks = adpcmData.Length / blockAlign;
            int outSampleCount = blocks * samplesPerBlock;
            var pcm = new short[outSampleCount * ch];

            for (int b = 0; b < blocks; b++)
            {
                int blockStart = b * blockAlign;
                int outBase = b * samplesPerBlock * ch;
                if (!DecodeBlock(adpcmData, blockStart, blockAlign, ch, samplesPerBlock,
                        coef1, coef2, pcm, outBase))
                {
                    Array.Resize(ref pcm, outBase);
                    break;
                }
            }
            if (pcm.Length == 0) return default;

            int frames = pcm.Length / ch;
            var mono = new float[frames];
            const float invShortMax = 1f / 32768f;
            if (ch == 1)
            {
                for (int i = 0; i < frames; i++) mono[i] = pcm[i] * invShortMax;
            }
            else // stereo
            {
                for (int i = 0; i < frames; i++)
                {
                    int s = pcm[i * 2] + pcm[i * 2 + 1];
                    mono[i] = (s * 0.5f) * invShortMax;
                }
            }
            return new Result(mono, sr);
        }
        catch
        {
            return default;
        }
    }

    // mono block: [predictor:1][delta:2][sample1:2][sample2:2][nibbles:rest]; stereo interleaves headers
    private static bool DecodeBlock(byte[] data, int offset, int blockAlign, int channels,
        int samplesPerBlock, short[] coef1, short[] coef2, short[] outPcm, int outBase)
    {
        if (offset + blockAlign > data.Length) return false;
        if (channels < 1 || channels > 2) return false;

        int hdrBytes = 7 * channels;
        if (blockAlign < hdrBytes) return false;

        Span<int> predIdx = stackalloc int[2];
        Span<int> delta = stackalloc int[2];
        Span<int> sample1 = stackalloc int[2];
        Span<int> sample2 = stackalloc int[2];

        int p = offset;
        for (int c = 0; c < channels; c++) predIdx[c] = data[p++];
        for (int c = 0; c < channels; c++)
        {
            delta[c] = (short)(data[p] | (data[p + 1] << 8));
            p += 2;
        }
        for (int c = 0; c < channels; c++)
        {
            sample1[c] = (short)(data[p] | (data[p + 1] << 8));
            p += 2;
        }
        for (int c = 0; c < channels; c++)
        {
            sample2[c] = (short)(data[p] | (data[p + 1] << 8));
            p += 2;
        }

        for (int c = 0; c < channels; c++)
            if (predIdx[c] < 0 || predIdx[c] >= coef1.Length) return false;

        // seed samples are real samples; emit them first
        int outIdx = outBase;
        if (samplesPerBlock < 2) return false;
        for (int c = 0; c < channels; c++) outPcm[outIdx + c] = (short)sample2[c];
        outIdx += channels;
        for (int c = 0; c < channels; c++) outPcm[outIdx + c] = (short)sample1[c];
        outIdx += channels;

        int remainingSamples = samplesPerBlock - 2;
        int nibbleByteCount = blockAlign - hdrBytes;
        int nibbleSamplesAvail = nibbleByteCount * 2;
        // stereo: high nibble = L, low nibble = R. mono: two samples per byte.
        if (channels == 2 && nibbleByteCount * 1 < remainingSamples) remainingSamples = nibbleByteCount;
        else if (channels == 1 && nibbleSamplesAvail < remainingSamples) remainingSamples = nibbleSamplesAvail;

        for (int s = 0; s < remainingSamples; s++)
        {
            int byteIdx;
            int nibble;
            if (channels == 2)
            {
                byteIdx = p + s;
                if (byteIdx >= offset + blockAlign) break;
                int b = data[byteIdx];
                int channelHigh = b >> 4;
                int channelLow = b & 0x0F;

                outPcm[outIdx++] = DecodeNibble(channelHigh, 0,
                    coef1, coef2, predIdx, sample1, sample2, delta);
                outPcm[outIdx++] = DecodeNibble(channelLow, 1,
                    coef1, coef2, predIdx, sample1, sample2, delta);
            }
            else // mono
            {
                byteIdx = p + s / 2;
                if (byteIdx >= offset + blockAlign) break;
                int b = data[byteIdx];
                nibble = (s & 1) == 0 ? (b >> 4) : (b & 0x0F);
                outPcm[outIdx++] = DecodeNibble(nibble, 0,
                    coef1, coef2, predIdx, sample1, sample2, delta);
            }
        }
        return true;
    }

    private static short DecodeNibble(int nibble, int c,
        short[] coef1, short[] coef2,
        Span<int> predIdx, Span<int> sample1, Span<int> sample2, Span<int> delta)
    {
        int predictor = (sample1[c] * coef1[predIdx[c]] + sample2[c] * coef2[predIdx[c]]) >> 8;

        int signedNibble = nibble;
        if ((signedNibble & 0x08) != 0) signedNibble -= 0x10;

        int newSample = predictor + signedNibble * delta[c];
        if (newSample > short.MaxValue) newSample = short.MaxValue;
        else if (newSample < short.MinValue) newSample = short.MinValue;

        delta[c] = (AdaptTable[nibble & 0x0F] * delta[c]) >> 8;
        if (delta[c] < 16) delta[c] = 16;

        sample2[c] = sample1[c];
        sample1[c] = newSample;
        return (short)newSample;
    }
}
