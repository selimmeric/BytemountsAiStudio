using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Sürdürülebilir yükleme mantığının testleri (P1-25).
///
/// Saf: ağ ve dosya yok. Parça sınırlarının hesabı ve "nereden devam
/// edilecek" kararı, 60 MB'lık bir dosya gerçekten yüklenerek
/// öğrenilecek bir şey olmamalı.
public sealed class ResumableUploadTests
{
    private const long VideoBytes = 62_914_560; // 60 MiB

    private static UploadSession Session(long confirmed = 0, int attempts = 0)
        => new()
        {
            SessionUrl = "https://upload.example/session/abc",
            TotalBytes = VideoBytes,
            ConfirmedBytes = confirmed,
            StartedAt = new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero),
            Attempts = attempts,
        };

    /// Parça boyutu 256 KiB'in TAM KATI olmak zorunda: katı olmayan bir
    /// parça, son parça değilse 400 ile reddediliyor ve hata mesajı
    /// bunu açıkça söylemiyor.
    [Theory]
    [InlineData(8 * 1024 * 1024, 8 * 1024 * 1024)]
    [InlineData(300_000, 262_144)]
    [InlineData(1, 262_144)]
    [InlineData(0, 262_144)]
    [InlineData(-5, 262_144)]
    public void ParcaBoyutu_HizalanIyor(int requested, int expected)
    {
        Assert.Equal(expected, ResumableUpload.AlignChunk(requested));
        Assert.Equal(0, ResumableUpload.AlignChunk(requested) % ResumableUpload.ChunkAlignment);
    }

    /// AŞAĞI yuvarlanıyor: yukarı yuvarlamak, istenen sınırın üstüne
    /// çıkıp bellekte daha büyük bir tampon ayırmak demekti.
    [Fact]
    public void ParcaBoyutu_AsagiYuvarlaniyor()
    {
        Assert.Equal(524_288, ResumableUpload.AlignChunk(600_000));
    }

    [Fact]
    public void IlkParca_BastanBasliyor()
    {
        var (start, length) = ResumableUpload.NextChunk(Session(), ResumableUpload.DefaultChunkSize);

        Assert.Equal(0, start);
        Assert.Equal(ResumableUpload.DefaultChunkSize, length);
    }

    [Fact]
    public void SonrakiParca_OnaylanandanDevam()
    {
        var (start, _) = ResumableUpload.NextChunk(Session(confirmed: 8 * 1024 * 1024), ResumableUpload.DefaultChunkSize);

        Assert.Equal(8 * 1024 * 1024, start);
    }

    /// Son parça hizalı OLMAK ZORUNDA DEĞİL ve olamaz da — dosya
    /// boyutu nadiren 256 KiB'in katı.
    [Fact]
    public void SonParca_KalanKadar()
    {
        var (start, length) = ResumableUpload.NextChunk(
            Session(confirmed: VideoBytes - 1000), ResumableUpload.DefaultChunkSize);

        Assert.Equal(VideoBytes - 1000, start);
        Assert.Equal(1000, length);
    }

    [Fact]
    public void TamamlanmisYukleme_ParcaVermiyor()
    {
        var (_, length) = ResumableUpload.NextChunk(
            Session(confirmed: VideoBytes), ResumableUpload.DefaultChunkSize);

        Assert.Equal(0, length);
    }

    /// `Content-Range` bitişi DAHİL: bir eksik yazmak sunucunun bir
    /// bayt eksik almasına ve son parçada "tamamlanmadı" demesine yol
    /// açıyor.
    [Fact]
    public void ContentRange_BitisDahil()
    {
        Assert.Equal("bytes 0-8388607/62914560",
            ResumableUpload.ContentRange(0, 8 * 1024 * 1024, VideoBytes));
    }

    /// Çökme sonrası ilk adım "ne kadarını aldın" sorusu.
    [Fact]
    public void BoyutSorgusu_YildizliBicim()
    {
        Assert.Equal("bytes */62914560", ResumableUpload.ContentRange(0, 0, VideoBytes));
    }

    /// Başlık SON BAYTIN İNDİSİNİ veriyor; onaylanan bayt sayısı bir
    /// fazlası. Karıştırmak her sürdürmede bir baytlık kayma üretirdi.
    [Fact]
    public void OnaylananBayt_IndisDegilSayi()
    {
        Assert.Equal(8_388_608, ResumableUpload.ConfirmedFrom("bytes=0-8388607"));
        Assert.Equal(1, ResumableUpload.ConfirmedFrom("bytes=0-0"));
    }

    /// Başlık YOKSA sıfır: sunucu hiçbir şey almamış demektir. Bunu
    /// "bilinmiyor" sayıp mevcut değeri korumak, hiç ulaşmamış bir
    /// parçadan sonrasını göndermek ve dosyada DELİK bırakmak olurdu.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bozuk")]
    [InlineData("bytes=0-abc")]
    public void BasliksizVeyaBozuk_SifirDonuyor(string? header)
    {
        Assert.Equal(0, ResumableUpload.ConfirmedFrom(header));
    }

    [Fact]
    public void SurdurulebilirOturum_Taniniyor()
    {
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.True(ResumableUpload.CanResume(Session(confirmed: 1000), now, ResumableUpload.SessionLifetime));
    }

    /// Oturum ömrü dolmuşsa sürdürme adresi geçersiz ve o adrese
    /// yapılan istek 404 dönüyor. Baştan başlamak pahalı ama tek
    /// seçenek; önemli olan bunu ÖNCEDEN bilip boşuna bir tur
    /// harcamamak.
    [Fact]
    public void OmruDolmusOturum_SurdurulemIyor()
    {
        var late = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.False(ResumableUpload.CanResume(Session(confirmed: 1000), late, ResumableUpload.SessionLifetime));
    }

    [Fact]
    public void TamamlanmisVeyaYokOturum_SurdurulemIyor()
    {
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

        Assert.False(ResumableUpload.CanResume(null, now, ResumableUpload.SessionLifetime));
        Assert.False(ResumableUpload.CanResume(Session(confirmed: VideoBytes), now, ResumableUpload.SessionLifetime));
        Assert.False(ResumableUpload.CanResume(
            Session() with { SessionUrl = "  " }, now, ResumableUpload.SessionLifetime));
    }

    [Fact]
    public void Ilerleme_YuzdeOlarakOkunabiliyor()
    {
        Assert.Equal(50, Session(confirmed: VideoBytes / 2).Percent, 1);
        Assert.Equal(0, (Session() with { TotalBytes = 0 }).Percent);
    }

    /// Onaylanan bayt toplamı aşarsa (sunucu hatası ya da bozuk kayıt)
    /// negatif uzunluk üretilmiyor.
    [Fact]
    public void AsiriOnay_NegatifUzunlukUretmiyor()
    {
        var (start, length) = ResumableUpload.NextChunk(
            Session(confirmed: VideoBytes + 5000), ResumableUpload.DefaultChunkSize);

        Assert.Equal(VideoBytes, start);
        Assert.Equal(0, length);
    }
}
