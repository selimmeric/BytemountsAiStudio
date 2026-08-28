using System.Text.Json;
using BytemountsAiStudio.Nodes;

namespace BytemountsAiStudio.Nodes.Tests;

/// QC node'unun run bağlamını okumasının testleri.
///
/// EKSİK HALKA BUYDU: `MechanicalQc` yazılmıştı ve otuz testi
/// geçiyordu ama hiçbir node onu çağırmıyordu — gerçek bir koşuda QC
/// hiç koşmuyor, skor hiç üretilmiyordu. Onay kapısı skoru bulamadığı
/// için HER videoyu insana soruyordu ve seçici onay (P2-08) pratikte
/// hiç devreye giremiyordu.
public sealed class QualityCheckHandlerTests
{
    private static JsonElement Context(object value)
        => JsonSerializer.SerializeToElement(value);

    /// Render koşmadıysa ölçüm YOK: render'a bağlı kontroller
    /// "ölçülemedi" olarak düşüyor, "geçti" değil. İkisini eşitlemek,
    /// hiç render edilmemiş bir videoyu tam puanla geçirmekti.
    [Fact]
    public void RenderKosmadi_OlcumYok()
    {
        Assert.Null(QualityCheckHandler.MediaFrom(Context(new { })));
    }

    [Fact]
    public void RenderCiktisi_OlcumeCevriliyor()
    {
        var media = QualityCheckHandler.MediaFrom(Context(new
        {
            render = new
            {
                duration_seconds = 42.5,
                width = 1080,
                height = 1920,
                audio_codec = "aac",
                size_bytes = 8_500_000,
            },
        }));

        Assert.NotNull(media);
        Assert.Equal(42.5, media.DurationSeconds);
        Assert.Equal(1080, media.Width);
        Assert.True(media.HasAudio);
        Assert.Equal(8_500_000, media.SizeBytes);
    }

    /// Ses kodeği YOKSA ses de yok: boş bir kodek adını "var" saymak,
    /// sessiz bir videoyu sesli sanmaktı.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SesKodegiYok_SesYok(string? codec)
    {
        var media = QualityCheckHandler.MediaFrom(Context(new
        {
            render = new { duration_seconds = 10.0, width = 1080, height = 1920, audio_codec = codec },
        }));

        Assert.False(media!.HasAudio);
    }

    [Fact]
    public void SeoCiktisi_MetadataYaCevriliyor()
    {
        var metadata = QualityCheckHandler.MetadataFrom(Context(new
        {
            seo = new { title = "Başlık", description = "Açıklama", tags = new[] { "a", "b", "" } },
            thumbnail = new { width = 1280, height = 720, size_bytes = 120_000 },
        }));

        Assert.NotNull(metadata);
        Assert.Equal("Başlık", metadata.Title);

        // Boş etiket atlanıyor: platform da onu saymıyor ve etiket
        // sayısı kontrolünü yanıltırdı.
        Assert.Equal(2, metadata.Tags.Count);
        Assert.Equal(1280, metadata.Thumbnail!.Width);
    }

    [Fact]
    public void SeoKosmadi_MetadataYok()
    {
        Assert.Null(QualityCheckHandler.MetadataFrom(Context(new { })));
    }

    [Fact]
    public void IddiaCiktisi_KapsamaCevriliyor()
    {
        var claims = QualityCheckHandler.ClaimsFrom(Context(new
        {
            claims = new { total = 4, supported = 3 },
        }));

        Assert.NotNull(claims);
        Assert.Equal(4, claims.TotalClaims);
        Assert.Equal(3, claims.SourcedClaims);
        Assert.False(claims.AllSourced);
    }

    [Fact]
    public void IddiaKosmadi_KapsamYok()
    {
        Assert.Null(QualityCheckHandler.ClaimsFrom(Context(new { })));
        Assert.Null(QualityCheckHandler.ClaimsFrom(Context(new { claims = new { baska = 1 } })));
    }

    [Fact]
    public void TekillikCiktisi_Okunuyor()
    {
        var uniqueness = QualityCheckHandler.UniquenessFrom(Context(new
        {
            topic = new { is_unique = false, similarity = 0.94, conflicting_title = "Aynı konu" },
        }));

        Assert.NotNull(uniqueness);
        Assert.False(uniqueness.IsUnique);
        Assert.Equal(0.94, uniqueness.Similarity);
        Assert.Equal("Aynı konu", uniqueness.ConflictingTitle);
    }

    /// Tekillik kontrolü koşmadıysa null: "kontrol edilmedi" ile
    /// "tekil" aynı şey değil.
    [Fact]
    public void TekillikKosmadi_Null()
    {
        Assert.Null(QualityCheckHandler.UniquenessFrom(Context(new { topic = new { topic = "x" } })));
    }

    /// Sayaç bir yerde tutulmazsa döngü sınırı hiç dolmuyor ve aynı
    /// hata sonsuza kadar para harcayarak tekrarlanıyor.
    [Fact]
    public void DonguSayaci_OncekiQcCiktisindanOkunuyor()
    {
        Assert.Equal(0, QualityCheckHandler.LoopsFrom(Context(new { })));

        Assert.Equal(2, QualityCheckHandler.LoopsFrom(Context(new
        {
            qc = new { retry = new { loop = 2 } },
        })));
    }
}
