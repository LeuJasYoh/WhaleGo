using System.IO.Compression;

namespace WhaleGo.Platform.Capture;

/// <summary>
/// Minimal PNG encoder for 32bpp BGRA DIB buffers (no System.Drawing.Common dependency). Produces an 8-bit RGBA
/// non-interlaced PNG using zlib (ZLibStream) for the IDAT payload: signature + IHDR + IDAT + IEND with correct CRC32s.
/// Fully deterministic and safe on any host.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeBgra32(int width, int height, byte[] bgra, int stride)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        }

        if (stride < width * 4 || bgra.Length < stride * height)
        {
            throw new ArgumentException("BGRA buffer is smaller than the declared dimensions.", nameof(bgra));
        }

        var rowBytes = width * 4;
        var raw = new byte[(rowBytes + 1) * height];
        var rawOffset = 0;
        for (var y = 0; y < height; y++)
        {
            raw[rawOffset++] = 0; // filter type: none
            var source = y * stride;
            for (var x = 0; x < width; x++)
            {
                var b = bgra[source + (x * 4)];
                var g = bgra[source + (x * 4) + 1];
                var r = bgra[source + (x * 4) + 2];
                raw[rawOffset++] = r;
                raw[rawOffset++] = g;
                raw[rawOffset++] = b;
                raw[rawOffset++] = 0xFF; // opaque
            }
        }

        using var compressed = new MemoryStream((raw.Length / 2) + 256);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        using var output = new MemoryStream(checked((int)compressed.Length) + 128);
        output.Write(Signature, 0, Signature.Length);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // color type: RGBA
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length, 0, 4);

        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        stream.Write(crcBytes, 0, 4);
    }

    private static uint Crc32(byte[] typeBytes, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
