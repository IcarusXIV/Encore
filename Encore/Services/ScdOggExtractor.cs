using System;
using System.IO;

namespace Encore.Services;

// pulls first OGG Vorbis audio stream from an SCD file. Ported from Lumina ScdFile.cs.
// v2 = single-byte XOR on OGG header. v3 = table XOR over whole OGG buffer.
internal static class ScdOggExtractor
{
    // stolen from FFXIV Explorer via Lumina
    private static readonly byte[] OggXorTable =
    {
        0x3A, 0x32, 0x32, 0x32, 0x03, 0x7E, 0x12, 0xF7, 0xB2, 0xE2, 0xA2, 0x67, 0x32, 0x32, 0x22, 0x32,
        0x32, 0x52, 0x16, 0x1B, 0x3C, 0xA1, 0x54, 0x7B, 0x1B, 0x97, 0xA6, 0x93, 0x1A, 0x4B, 0xAA, 0xA6,
        0x7A, 0x7B, 0x1B, 0x97, 0xA6, 0xF7, 0x02, 0xBB, 0xAA, 0xA6, 0xBB, 0xF7, 0x2A, 0x51, 0xBE, 0x03,
        0xF4, 0x2A, 0x51, 0xBE, 0x03, 0xF4, 0x2A, 0x51, 0xBE, 0x12, 0x06, 0x56, 0x27, 0x32, 0x32, 0x36,
        0x32, 0xB2, 0x1A, 0x3B, 0xBC, 0x91, 0xD4, 0x7B, 0x58, 0xFC, 0x0B, 0x55, 0x2A, 0x15, 0xBC, 0x40,
        0x92, 0x0B, 0x5B, 0x7C, 0x0A, 0x95, 0x12, 0x35, 0xB8, 0x63, 0xD2, 0x0B, 0x3B, 0xF0, 0xC7, 0x14,
        0x51, 0x5C, 0x94, 0x86, 0x94, 0x59, 0x5C, 0xFC, 0x1B, 0x17, 0x3A, 0x3F, 0x6B, 0x37, 0x32, 0x32,
        0x30, 0x32, 0x72, 0x7A, 0x13, 0xB7, 0x26, 0x60, 0x7A, 0x13, 0xB7, 0x26, 0x50, 0xBA, 0x13, 0xB4,
        0x2A, 0x50, 0xBA, 0x13, 0xB5, 0x2E, 0x40, 0xFA, 0x13, 0x95, 0xAE, 0x40, 0x38, 0x18, 0x9A, 0x92,
        0xB0, 0x38, 0x00, 0xFA, 0x12, 0xB1, 0x7E, 0x00, 0xDB, 0x96, 0xA1, 0x7C, 0x08, 0xDB, 0x9A, 0x91,
        0xBC, 0x08, 0xD8, 0x1A, 0x86, 0xE2, 0x70, 0x39, 0x1F, 0x86, 0xE0, 0x78, 0x7E, 0x03, 0xE7, 0x64,
        0x51, 0x9C, 0x8F, 0x34, 0x6F, 0x4E, 0x41, 0xFC, 0x0B, 0xD5, 0xAE, 0x41, 0xFC, 0x0B, 0xD5, 0xAE,
        0x41, 0xFC, 0x3B, 0x70, 0x71, 0x64, 0x33, 0x32, 0x12, 0x32, 0x32, 0x36, 0x70, 0x34, 0x2B, 0x56,
        0x22, 0x70, 0x3A, 0x13, 0xB7, 0x26, 0x60, 0xBA, 0x1B, 0x94, 0xAA, 0x40, 0x38, 0x00, 0xFA, 0xB2,
        0xE2, 0xA2, 0x67, 0x32, 0x32, 0x12, 0x32, 0xB2, 0x32, 0x32, 0x32, 0x32, 0x75, 0xA3, 0x26, 0x7B,
        0x83, 0x26, 0xF9, 0x83, 0x2E, 0xFF, 0xE3, 0x16, 0x7D, 0xC0, 0x1E, 0x63, 0x21, 0x07, 0xE3, 0x01
    };

    public static byte[]? ExtractFirstOgg(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return ExtractFirstOgg(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? ExtractFirstOgg(byte[] scdBytes)
    {
        if (scdBytes == null || scdBytes.Length < 256) return null;

        try
        {
            using var ms = new MemoryStream(scdBytes);
            using var br = new BinaryReader(ms);

            // SCD "SEDB" preamble. Offset at +0x0E points at ScdHeader.
            br.BaseStream.Position = 0;
            var magic = br.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != (byte)'S' || magic[1] != (byte)'E'
                || magic[2] != (byte)'D' || magic[3] != (byte)'B') return null;
            br.BaseStream.Position = 0x0E;
            ushort binHeaderSize = br.ReadUInt16();
            if (binHeaderSize < 0x20 || binHeaderSize > 0x80) return null;

            br.BaseStream.Position = binHeaderSize;
            ushort soundCount = br.ReadUInt16();
            ushort trackCount = br.ReadUInt16();
            ushort audioCount = br.ReadUInt16();
            ushort number = br.ReadUInt16();
            uint trackOffset = br.ReadUInt32();
            uint audioOffset = br.ReadUInt32();

            if (audioCount == 0 || audioOffset == 0) return null;

            // mod SCDs almost always carry a single track in slot 0
            br.BaseStream.Position = audioOffset;
            uint entry0Offset = br.ReadUInt32();
            if (entry0Offset == 0 || entry0Offset >= scdBytes.Length - 32) return null;

            // AudioBasicDesc (32 bytes)
            br.BaseStream.Position = entry0Offset;
            uint size = br.ReadUInt32();
            uint channel = br.ReadUInt32();
            uint rate = br.ReadUInt32();
            int format = br.ReadInt32();
            uint loopStart = br.ReadUInt32();
            uint loopEnd = br.ReadUInt32();
            uint subInfoSize = br.ReadUInt32();
            uint flg = br.ReadUInt32();

            const int OggVorbisFormat = 6;
            if (format != OggVorbisFormat) return null;
            if (size < 64 || size > scdBytes.Length) return null;

            long subInfoStart = br.BaseStream.Position;

            if ((flg & 0x01) == 0)
            {
                br.BaseStream.Position = subInfoStart;
            }
            else
            {
                // skip MarkerChunk
                br.BaseStream.Position = subInfoStart;
                uint mcId = br.ReadUInt32();
                uint mcSize = br.ReadUInt32();
                int sampleLoopStart = br.ReadInt32();
                int sampleLoopEnd = br.ReadInt32();
                int numMarkers = br.ReadInt32();
                br.BaseStream.Position = subInfoStart + mcSize;
            }

            byte version = br.ReadByte();
            byte structSize = br.ReadByte();
            byte xorByte = br.ReadByte();
            br.BaseStream.Position += 9;
            float step = br.ReadSingle();
            uint seekTableSize = br.ReadUInt32();
            uint oggHeaderSize = br.ReadUInt32();
            br.BaseStream.Position += 8;

            br.BaseStream.Position = subInfoStart + subInfoSize;

            long oggBlockStart = br.BaseStream.Position;
            long oggTotalLen = (long)oggHeaderSize + size;
            if (oggBlockStart + oggTotalLen > scdBytes.Length) return null;

            var ogg = new byte[oggTotalLen];
            Buffer.BlockCopy(scdBytes, (int)oggBlockStart, ogg, 0, (int)oggTotalLen);

            switch (version)
            {
                case 2:
                    if (xorByte != 0)
                    {
                        for (int j = 0; j < oggHeaderSize && j < ogg.Length; j++)
                            ogg[j] ^= xorByte;
                    }
                    break;

                case 3:
                {
                    byte byte1 = (byte)(size & 0x7F);
                    byte byte2 = (byte)(byte1 & 0x3F);
                    for (int j = 0; j < ogg.Length; j++)
                    {
                        byte tbl = OggXorTable[(byte2 + j) & 0xFF];
                        tbl ^= ogg[j];
                        tbl ^= byte1;
                        ogg[j] = tbl;
                    }
                    break;
                }
                // v0/v1: raw OGG, no XOR
            }

            if (ogg.Length < 4 || ogg[0] != (byte)'O' || ogg[1] != (byte)'g'
                || ogg[2] != (byte)'g' || ogg[3] != (byte)'S') return null;

            return ogg;
        }
        catch
        {
            return null;
        }
    }
}
