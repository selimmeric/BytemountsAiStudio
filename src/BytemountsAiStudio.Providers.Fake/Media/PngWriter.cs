using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BytemountsAiStudio.Providers.Fake.Media;

/// Düz renk PNG üretir.
///
/// Neden elle: sahte görsellerin FFmpeg'in gerçekten okuyabildiği geçerli
/// dosyalar olması gerekiyor — yoksa Faz 0'ın "uçtan uca mp4" hedefi sahte
/// veriyle sınanamaz. Bunun için bir görüntü kütüphanesi bağımlılığı eklemek
/// ise sahte sağlayıcıya gerçek bir maliyet yüklerdi. PNG'nin düz renk hâli
/// zaten yüz satırlık bir iş.
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] SolidColor(int width, int height, byte r, byte g, byte b)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        using var output = new MemoryStream();
        output.Write(Signature);

        // IHDR: 8 bit derinlik, renk tipi 2 (truecolor RGB), sıkıştırma 0,
        // filtre 0, interlace yok.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(output, "IHDR", ihdr);

        WriteChunk(output, "IDAT", CompressScanlines(width, height, r, g, b));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] CompressScanlines(int width, int height, byte r, byte g, byte b)
    {
        // Her satır bir filtre baytıyla başlar; 0 = filtre yok. Düz renkte
        // filtreleme kazanç sağlamaz, sıkıştırma zaten tekrarı yakalar.
        var rowLength = 1 + (width * 3);
        var row = new byte[rowLength];
        for (var x = 0; x < width; x++)
        {
            var offset = 1 + (x * 3);
            row[offset] = r;
            row[offset + 1] = g;
            row[offset + 2] = b;
        }

        using var compressed = new MemoryStream();

        // PNG IDAT zlib akışı ister (deflate değil): ZLibStream tam olarak bunu üretir.
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        // CRC tip ve veriyi birlikte kapsar, uzunluğu kapsamaz.
        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }
}

/// PNG chunk'ları için CRC-32 (IEEE 802.3).
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        crc = Update(crc, first);
        crc = Update(crc, second);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
