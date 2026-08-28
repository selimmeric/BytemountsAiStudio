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
///
/// PUBLIC: sahte müzik indiricisi de gerçek bir dosya üretmek zorunda.
/// Bir `.invalid` adresine gidip boş dönseydi sahte hat müziksiz
/// koşar ve müzik → timeline → render yolu hiç sınanmazdı.
public static class WavWriter
{
    public const int SampleRate = 48_000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    /// Konuşmayı taklit eden ses.
    ///
    /// SESSİZLİK YETMİYORDU ve bu gerçek bir eksiği gizledi: render
    /// çıktısı −70 LUFS oluyor, QC'nin "ses var ve sessiz değil"
    /// kontrolü haklı olarak düşüyor ve sahte hat kabul kriterini
    /// HİÇBİR ZAMAN sağlayamıyordu. Yani sahte sağlayıcı, temsil ettiği
    /// şeyi temsil etmiyordu — gerçek bir TTS konuşma döndürüyor,
    /// dijital sessizlik değil.
    ///
    /// SES KONUŞMA GİBİ ŞEKİLLİ: kısa duraklamalarla bölünmüş
    /// patlamalar. Sürekli bir ton, konuşma oranını %100 gösterirdi ve
    /// o ölçüm de temsili olmazdı.
    ///
    /// Seviye −20 dBFS civarı: yayın hedefine (−16 LUFS) yakın ama
    /// kırpılmaktan uzak. Tam ölçek bir ton, kırpılma kontrolünü
    /// yanlışlıkla tetiklerdi.
    public static byte[] Speech(Ms duration)
    {
        var buffer = Silence(duration);
        var samples = (int)((long)duration.Value * SampleRate / 1000);

        // 220 Hz: insan sesinin temel frekans aralığında.
        const double frequency = 220.0;
        const double amplitude = 0.1 * short.MaxValue;

        // 1,6 sn konuşma + 0,4 sn duraklama: konuşma oranı ~%80.
        const int burstSamples = (int)(1.6 * SampleRate);
        const int gapSamples = (int)(0.4 * SampleRate);
        const int period = burstSamples + gapSamples;

        var data = buffer.AsSpan(44);

        for (var i = 0; i < samples; i++)
        {
            if (i % period >= burstSamples)
            {
                continue;
            }

            // Patlama başında ve sonunda yumuşatma: sert kesme,
            // kırpılma ölçümünde tepe olarak görünürdü.
            var position = i % period;
            var fade = Math.Min(Math.Min(position, burstSamples - position) / (0.05 * SampleRate), 1.0);

            var value = (short)(amplitude * fade * Math.Sin(2 * Math.PI * frequency * i / SampleRate));

            BinaryPrimitives.WriteInt16LittleEndian(data.Slice(i * 2, 2), value);
        }

        return buffer;
    }

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
