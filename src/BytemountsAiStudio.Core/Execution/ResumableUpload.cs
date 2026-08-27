using System.Globalization;

namespace BytemountsAiStudio.Core.Execution;

/// Yarım kalmış bir yüklemenin durumu (P1-25).
///
/// Bu kayıt VERİTABANINDA duruyor ve işin kendisiyle birlikte
/// yaşıyor. Bellekte tutulsaydı worker çöktüğünde kaybolur ve yükleme
/// baştan başlardı — 60 MB'lık bir video için bu, harcanan bant
/// genişliğinin ve kotanın çöpe gitmesi demek.
public sealed record UploadSession
{
    /// Platformun verdiği sürdürme adresi. Oturum ömürlü: YouTube
    /// bunu bir hafta saklıyor, sonra baştan başlamak gerekiyor.
    public required string SessionUrl { get; init; }

    public required long TotalBytes { get; init; }

    /// Platformun ONAYLADIĞI son bayt. Bizim gönderdiğimiz değil:
    /// gönderdiğimiz bir parça karşı tarafa hiç ulaşmamış olabilir ve
    /// oradan devam etmek dosyada delik bırakırdı.
    public long ConfirmedBytes { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public int Attempts { get; init; }

    public bool Complete => ConfirmedBytes >= TotalBytes;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{ConfirmedBytes}/{TotalBytes} bayt ({Percent:0.#}%), {Attempts} deneme");

    public double Percent => TotalBytes <= 0 ? 0 : ConfirmedBytes * 100.0 / TotalBytes;
}

/// Sürdürülebilir yükleme mantığı (P1-25, §15.2).
///
/// SAF: ağ yok, dosya yok. Parça sınırlarının hesabı ve "nereden devam
/// edilecek" kararı, 60 MB'lık bir dosya gerçekten yüklenerek
/// öğrenilecek bir şey olmamalı.
public static class ResumableUpload
{
    /// Parça boyutu 256 KiB'in TAM KATI olmak zorunda.
    ///
    /// Google'ın sürdürülebilir yükleme protokolü bunu şart koşuyor:
    /// katı olmayan bir parça, son parça değilse 400 ile reddediliyor.
    /// Hata mesajı da bunu açıkça söylemiyor, o yüzden burada zorlanıyor.
    public const int ChunkAlignment = 256 * 1024;

    /// Varsayılan parça boyutu: 8 MiB.
    ///
    /// Küçük parça daha çok istek ve daha çok gecikme; büyük parça,
    /// koptuğunda daha çok tekrar. 8 MiB, kopan bir bağlantıda en
    /// fazla 8 MiB'ı tekrar göndermek demek — 60 MB'lık bir videoda
    /// kabul edilebilir.
    public const int DefaultChunkSize = 8 * 1024 * 1024;

    /// Parça boyutunu hizalar.
    ///
    /// AŞAĞI yuvarlanıyor: yukarı yuvarlamak, istenen sınırın üstüne
    /// çıkıp bellekte daha büyük bir tampon ayırmak demekti.
    public static int AlignChunk(int requested)
    {
        var aligned = Math.Max(requested, ChunkAlignment) / ChunkAlignment * ChunkAlignment;

        return aligned;
    }

    /// Sıradaki parçanın sınırları: (başlangıç, uzunluk).
    ///
    /// Son parça hizalı OLMAK ZORUNDA DEĞİL ve olamaz da — dosya
    /// boyutu nadiren 256 KiB'in katı.
    public static (long Start, int Length) NextChunk(UploadSession session, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(session);

        var start = Math.Clamp(session.ConfirmedBytes, 0, session.TotalBytes);
        var remaining = session.TotalBytes - start;

        if (remaining <= 0)
        {
            return (start, 0);
        }

        var length = (int)Math.Min(AlignChunk(chunkSize), remaining);

        return (start, length);
    }

    /// `Content-Range` başlığının değeri.
    ///
    /// Biçim kesin: `bytes 0-8388607/62914560`. Bitiş DAHİL — bir eksik
    /// yazmak sunucunun bir bayt eksik almasına ve son parçada
    /// "tamamlanmadı" demesine yol açıyor.
    public static string ContentRange(long start, int length, long total)
    {
        if (length <= 0)
        {
            // Boyut sorgusu: "ne kadarını aldın" diye sormanın biçimi.
            // Çökme sonrası ilk adım bu.
            return string.Create(CultureInfo.InvariantCulture, $"bytes */{total}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{start + length - 1}/{total}");
    }

    /// Sunucunun `Range: bytes=0-8388607` cevabından onaylanan bayt
    /// sayısını okur.
    ///
    /// PLATFORMUN SÖYLEDİĞİ doğru kabul ediliyor, bizim gönderdiğimiz
    /// değil: gönderilen bir parça karşı tarafa hiç ulaşmamış olabilir
    /// ve oradan devam etmek dosyada DELİK bırakırdı — yükleme
    /// tamamlanmış görünür, video bozuk çıkardı.
    ///
    /// Başlık YOKSA sıfır dönüyor: sunucu hiçbir şey almamış demektir.
    /// Bunu "bilinmiyor" sayıp mevcut değeri korumak, hiç ulaşmamış bir
    /// parçadan sonrasını göndermek olurdu.
    public static long ConfirmedFrom(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return 0;
        }

        var separator = rangeHeader.LastIndexOf('-');

        if (separator < 0
            || !long.TryParse(rangeHeader[(separator + 1)..].Trim(), CultureInfo.InvariantCulture, out var last))
        {
            return 0;
        }

        // Başlık SON BAYTIN İNDİSİNİ veriyor; onaylanan bayt sayısı
        // bir fazlası. Bunu karıştırmak her sürdürmede bir baytlık
        // kayma üretirdi.
        return last < 0 ? 0 : last + 1;
    }

    /// Yükleme sürdürülebilir mi, yoksa baştan mı başlamalı.
    ///
    /// Oturum ömrü dolmuşsa sürdürme adresi artık geçersiz ve o adrese
    /// yapılan istek 404 dönüyor. Baştan başlamak pahalı ama tek
    /// seçenek; önemli olan bunu ÖNCEDEN bilmek ve boşuna bir tur
    /// harcamamak.
    public static bool CanResume(UploadSession? session, DateTimeOffset now, TimeSpan lifetime)
        => session is { Complete: false }
           && !string.IsNullOrWhiteSpace(session.SessionUrl)
           && now - session.StartedAt < lifetime;

    /// Google sürdürme oturumlarını bir hafta saklıyor.
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
}
