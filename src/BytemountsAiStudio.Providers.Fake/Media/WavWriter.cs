using System.Buffers.Binary;
using System.Text;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Providers.Fake.Media;

/// Sessiz PCM WAV üretir.
///
/// Sahte TTS'in gerçek bir ses dosyası döndürmesi şart: süre ölçümü (ffprobe),
/// ses birleştirme ve timeline derlemesi bu dosya üzerinden sınanacak. Boş bayt
/// dizisi döndürseydik Faz 0'ın kabul kriteri sahte veriyle geçerdi ama gerçek
/// veriyle patlardı — sınamanın anlamı kalmazdı.
internal static class WavWriter
{
    public const int SampleRate = 48_000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    public static byte[] Silence(Ms duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Value);

        var samples = (int)((long)duration.Value * SampleRate / 1000);
        var dataBytes = samples * Channels * (BitsPerSample / 8);

        var buffer = new byte[44 + dataBytes];
        var span = buffer.AsSpan();

        // RIFF başlığı
        WriteAscii(span[..4], "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + dataBytes);
        WriteAscii(span.Slice(8, 4), "WAVE");

        // fmt bloğu (PCM)
        WriteAscii(span.Slice(12, 4), "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), SampleRate * Channels * (BitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), (short)(Channels * (BitsPerSample / 8)));
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), BitsPerSample);

        // data bloğu — örnekler sıfır, yani sessizlik
        WriteAscii(span.Slice(36, 4), "data");
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), dataBytes);

        return buffer;
    }

    /// WAV başlığından süreyi okur. Testler, üretilen dosyanın gerçekten
    /// istenen süreye sahip olduğunu bununla doğrular.
    public static Ms ReadDuration(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44)
        {
            throw new ArgumentException("WAV en az 44 bayt olmalı.", nameof(wav));
        }

        var byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(28, 4));
        var dataBytes = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(40, 4));

        return new Ms((int)((long)dataBytes * 1000 / byteRate));
    }

    private static void WriteAscii(Span<byte> target, string value)
        => Encoding.ASCII.GetBytes(value, target);
}
