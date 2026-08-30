using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Timeline;
using BytemountsAiStudio.Quality;

namespace BytemountsAiStudio.Quality.Tests;

/// Mekanik QC testleri (P1-21, §14.1).
///
/// Kabul kriteri: BİLEREK BOZULMUŞ 12 girdinin hepsi yakalanıyor.
/// Her kontrolün kendi testi var ve her test yalnızca O kontrolü
/// bozuyor — böylece bir kontrolün yanlışlıkla başkasının hatasını
/// yakalaması durumu görünür oluyor.
public sealed class MechanicalQcTests
{
    private static readonly AssetRef Asset =
        AssetRef.Create("0000000000000000000000000000000000000000000000000000000000000001");

    /// Bütün kontrolleri geçen girdi. Her test bundan TEK bir şey
    /// bozarak türüyor.
    private static QcInput Healthy() => new()
    {
        Timeline = Timeline(),
        Media = new MediaMeasurements
        {
            DurationSeconds = 10.0,
            Width = 1080,
            Height = 1920,
            HasAudio = true,
            LoudnessLufs = -16.0,
            TruePeakDb = -1.5,
            SpeechRatio = 0.85,
            SizeBytes = 3_000_000,
        },
        Metadata = new PublishMetadata
        {
            Title = "Göbeklitepe: Dünyanın En Eski Tapınağı",
            Description = "Kısa bir anlatı.",
            Tags = ["tarih", "arkeoloji", "göbeklitepe"],
            Thumbnail = new ThumbnailInfo(1280, 720, 200_000),
        },
        Claims = new ClaimCoverage(5, 5),
        Uniqueness = new UniquenessCheck(true, 0.31, null),
    };

    private static TimelineDocument Timeline(
        IReadOnlyList<Scene>? scenes = null,
        CaptionTrack? captions = null,
        Ms? duration = null)
    {
        var total = duration ?? new Ms(10_000);

        return new TimelineDocument
        {
            Canvas = Canvas.Shorts1080,
            Language = LanguageTag.Create("tr-TR"),
            Duration = total,
            Audio = new AudioTrack
            {
                VoiceSegments =
                [
                    new()
                    {
                        Id = "s0",
                        Asset = Asset,
                        Start = Ms.Zero,
                        Duration = total,
                        SpeechText = "Bir cümle.",
                    },
                ],
            },
            Scenes = scenes ??
            [
                Scene(0, Ms.Zero, new Ms(5_000)),
                Scene(1, new Ms(5_000), new Ms(5_000)),
            ],
            Captions = captions ?? new CaptionTrack
            {
                StyleRef = "caption",
                Cues =
                [
                    new() { Text = "Bir", Range = new TimeRange(Ms.Zero, new Ms(2_000)) },
                    new() { Text = "cümle", Range = new TimeRange(new Ms(2_000), new Ms(4_000)) },
                ],
            },
            Output = new OutputSpec { Preset = "shorts-1080x1920" },
        };
    }

    private static Scene Scene(int index, Ms start, Ms duration, AssetRef? asset = null) => new()
    {
        Index = index,
        Range = TimeRange.FromDuration(start, duration),
        VoiceSegmentIds = ["s0"],
        Visual = new SceneVisual { Asset = asset ?? Asset },
    };

    private static CheckResult Check(QualityReport report, string code)
        => Assert.Single(report.Checks, c => c.Code == code);

    // ---- Sağlıklı girdi ----

    [Fact]
    public void SaglikliGirdi_TumKontrolleriGecer()
    {
        var report = MechanicalQc.Run(Healthy());

        Assert.True(report.Checks.All(c => c.Passed),
            string.Join(" | ", report.Failures.Select(f => f.ToString())));
        Assert.False(report.HasBlockingFailure);
        Assert.Equal(100, report.Score);
        Assert.Equal(QualityDecision.Publish, report.Decision);
        Assert.Equal(RetryTarget.None, report.Target);
    }

    /// §14.1'in on iki kontrolu + muzik lisansi (P2-09) + iddia
    /// bagimsizligi (30 Agu 2026).
    ///
    /// ***SAYI SABIT TUTULUYOR VE BU KASITLI:*** yeni bir kontrol
    /// eklemek bu testi dusuruyor, yani ekleyen kisi SAYIYI da
    /// guncellemek zorunda kaliyor. Kontrol listesine sessizce bir
    /// sey eklemek ya da -- daha tehlikelisi -- bir seyi sessizce
    /// CIKARMAK boylece imkansiz.
    [Fact]
    public void OnDortKontrol_Kosuyor()
    {
        Assert.Equal(14, MechanicalQc.Run(Healthy()).Checks.Count);
    }

    [Fact]
    public void KontrolKodlari_Tekil()
    {
        var checks = MechanicalQc.Run(Healthy()).Checks;

        Assert.Equal(checks.Count, checks.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count());
    }

    // ---- 12 bozuk girdi ----

    /// 1 — Süre sapması %1'i aşıyor. Bundan büyük bir sapma ses ile
    /// görselin ayrıştığı anlamına geliyor ve videoda görülüyor.
    [Fact]
    public void Bozuk01_SureUyumsuz()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { DurationSeconds = 9.0 },
        };

        var check = Check(MechanicalQc.Run(input), "qc.duration");

        Assert.False(check.Passed);
        Assert.Equal(CheckSeverity.Blocking, check.Severity);
        Assert.Equal(RetryTarget.Render, check.Target);
        Assert.Contains("9", check.Detail!, StringComparison.Ordinal);
    }

    /// Tolerans içinde kalan küçük sapma geçmeli; yoksa her render
    /// yeniden denenirdi.
    [Fact]
    public void KucukSureSapmasi_Gecer()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { DurationSeconds = 10.05 },
        };

        Assert.True(Check(MechanicalQc.Run(input), "qc.duration").Passed);
    }

    /// 2 — Çözünürlük hedeften farklı.
    [Fact]
    public void Bozuk02_CozunurlukYanlis()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { Width = 1920, Height = 1080 },
        };

        var check = Check(MechanicalQc.Run(input), "qc.resolution");

        Assert.False(check.Passed);
        Assert.Equal(CheckSeverity.Blocking, check.Severity);
    }

    /// 3 — Ses kanalı yok.
    [Fact]
    public void Bozuk03_SesKanaliYok()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { HasAudio = false },
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.audio_present").Passed);
    }

    /// Ses kanalı VAR ama sessiz. Yalnızca "kanal var mı" diye bakmak
    /// bunu geçirirdi ve bu en sinsi hatalardan biri.
    [Fact]
    public void Bozuk03b_KanalVarAmaSessiz()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { HasAudio = true, LoudnessLufs = -70.0 },
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.audio_present").Passed);
    }

    /// 4 — Ses seviyesi hedef aralığın dışında.
    [Theory]
    [InlineData(-24.0)]
    [InlineData(-9.0)]
    public void Bozuk04_SesSeviyesiAralikDisi(double lufs)
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { LoudnessLufs = lufs },
        };

        var check = Check(MechanicalQc.Run(input), "qc.loudness");

        Assert.False(check.Passed);
        Assert.Equal(CheckSeverity.Blocking, check.Severity);
    }

    /// 5 — Kırpılma. UYARI: video bozuk değil, yalnızca kötü.
    [Fact]
    public void Bozuk05_Kirpilma_UyariSeviyesinde()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { TruePeakDb = 0.5 },
        };

        var report = MechanicalQc.Run(input);
        var check = Check(report, "qc.clipping");

        Assert.False(check.Passed);
        Assert.Equal(CheckSeverity.Warning, check.Severity);

        // Uyarı bloklamıyor: karar hâlâ yayın ya da onay tarafında.
        Assert.False(report.HasBlockingFailure);
    }

    /// 6 — Konuşma oranı makul değil. UYARI.
    [Theory]
    [InlineData(0.20)]
    [InlineData(0.995)]
    public void Bozuk06_KonusmaOraniMakulDegil(double ratio)
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { SpeechRatio = ratio },
        };

        var check = Check(MechanicalQc.Run(input), "qc.speech_ratio");

        Assert.False(check.Passed);
        Assert.Equal(CheckSeverity.Warning, check.Severity);
    }

    /// 7 — Sahne yok.
    [Fact]
    public void Bozuk07_HicSahneYok()
    {
        var input = Healthy() with { Timeline = Timeline(scenes: []) };

        var check = Check(MechanicalQc.Run(input), "qc.scene_visuals");

        Assert.False(check.Passed);
        Assert.Equal(RetryTarget.Visuals, check.Target);
    }

    /// Sahne var ama görsel referansı BOŞ. Tip geçerli, değer değil.
    [Fact]
    public void Bozuk07b_BosGorselReferansi()
    {
        var input = Healthy() with
        {
            Timeline = Timeline(scenes:
            [
                Scene(0, Ms.Zero, new Ms(5_000)),
                Scene(1, new Ms(5_000), new Ms(5_000), asset: default(AssetRef)),
            ]),
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.scene_visuals").Passed);
    }

    /// Sahneler arasında BOŞLUK var — videoda siyah kare demek.
    [Fact]
    public void Bozuk07c_SahnelerArasindaBosluk()
    {
        var input = Healthy() with
        {
            Timeline = Timeline(scenes:
            [
                Scene(0, Ms.Zero, new Ms(4_000)),
                Scene(1, new Ms(5_000), new Ms(5_000)),
            ]),
        };

        var check = Check(MechanicalQc.Run(input), "qc.scene_visuals");

        Assert.False(check.Passed);
        Assert.Contains("boşluk", check.Detail!, StringComparison.Ordinal);
    }

    /// Sahneler videoyu tam kaplamıyor: son sahne erken bitiyor.
    [Fact]
    public void Bozuk07d_SahnelerVideoyuKaplamiyor()
    {
        var input = Healthy() with
        {
            Timeline = Timeline(scenes: [Scene(0, Ms.Zero, new Ms(4_000))]),
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.scene_visuals").Passed);
    }

    /// 8 — Altyazı videonun sonunu aşıyor.
    [Fact]
    public void Bozuk08_AltyaziVideoyuAsiyor()
    {
        var input = Healthy() with
        {
            Timeline = Timeline(captions: new CaptionTrack
            {
                StyleRef = "caption",
                Cues = [new() { Text = "geç", Range = new TimeRange(new Ms(9_000), new Ms(12_000)) }],
            }),
        };

        var check = Check(MechanicalQc.Run(input), "qc.caption_bounds");

        Assert.False(check.Passed);
        Assert.Equal(RetryTarget.Timeline, check.Target);
    }

    /// Ters aralık: bitiş başlangıçtan önce.
    [Fact]
    public void Bozuk08b_TersAltyaziAraligi()
    {
        var input = Healthy() with
        {
            Timeline = Timeline(captions: new CaptionTrack
            {
                StyleRef = "caption",
                Cues = [new() { Text = "ters", Range = new TimeRange(new Ms(3_000), new Ms(3_000)) }],
            }),
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.caption_bounds").Passed);
    }

    /// Altyazısız video geçerli bir çıktı; eksikliği bloklamak yanlış
    /// olurdu.
    [Fact]
    public void AltyaziYok_Bloklamaz()
    {
        var input = Healthy() with { Timeline = Timeline(captions: new CaptionTrack { StyleRef = "c", Cues = [] }) };

        Assert.True(Check(MechanicalQc.Run(input), "qc.caption_bounds").Passed);
    }

    /// 9 — Kaynaksız iddia var (§2.2/8).
    [Fact]
    public void Bozuk09_KaynaksizIddia()
    {
        var input = Healthy() with { Claims = new ClaimCoverage(5, 3) };

        var check = Check(MechanicalQc.Run(input), "qc.claims_sourced");

        Assert.False(check.Passed);
        Assert.Equal(RetryTarget.Script, check.Target);
        Assert.Contains("3", check.Detail!, StringComparison.Ordinal);
    }

    /// 10 — Başlık sınırı aşılmış. Aşarsa upload REDDEDİLİYOR.
    [Fact]
    public void Bozuk10_BaslikCokUzun()
    {
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with { Title = new string('a', 101) },
        };

        var check = Check(MechanicalQc.Run(input), "qc.metadata_limits");

        Assert.False(check.Passed);
        Assert.Equal(RetryTarget.Metadata, check.Target);
    }

    [Fact]
    public void Bozuk10b_BosBaslik()
    {
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with { Title = "   " },
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.metadata_limits").Passed);
    }

    /// Etiket toplamı AYRAÇLARLA sayılıyor: platform da öyle sayıyor ve
    /// ayraçları unutmak sınırın hemen altındaki bir kümeyi reddettirir.
    [Fact]
    public void Bozuk10c_EtiketToplamiAsiyor()
    {
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with
            {
                Tags = [.. Enumerable.Repeat(new string('e', 50), 11)],
            },
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.metadata_limits").Passed);
    }

    /// Tam sınırdaki etiket kümesi geçmeli.
    [Fact]
    public void SinirdakiEtiketler_Gecer()
    {
        // 10 etiket × 49 karakter + 9 ayraç = 499
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with
            {
                Tags = [.. Enumerable.Repeat(new string('e', 49), 10)],
            },
        };

        Assert.True(Check(MechanicalQc.Run(input), "qc.metadata_limits").Passed);
    }

    /// 11 — Thumbnail oranı yanlış. Platform kendi kırpıyor ve
    /// kırptığı yer genellikle metnin ortası oluyor.
    [Fact]
    public void Bozuk11_ThumbnailOraniYanlis()
    {
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with { Thumbnail = new ThumbnailInfo(1280, 1280, 200_000) },
        };

        var check = Check(MechanicalQc.Run(input), "qc.thumbnail");

        Assert.False(check.Passed);
        Assert.Contains("oran", check.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Bozuk11b_ThumbnailCokKucuk()
    {
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with { Thumbnail = new ThumbnailInfo(640, 360, 50_000) },
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.thumbnail").Passed);
    }

    [Fact]
    public void Bozuk11c_ThumbnailCokBuyuk()
    {
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with { Thumbnail = new ThumbnailInfo(1920, 1080, 3 * 1024 * 1024) },
        };

        Assert.False(Check(MechanicalQc.Run(input), "qc.thumbnail").Passed);
    }

    /// 12 — Konu daha önce yayınlanmış (ADR-003).
    [Fact]
    public void Bozuk12_KonuTekrar()
    {
        var input = Healthy() with
        {
            Uniqueness = new UniquenessCheck(false, 0.94, "Dünyanın En Eski Tapınağı"),
        };

        var check = Check(MechanicalQc.Run(input), "qc.topic_unique");

        Assert.False(check.Passed);
        Assert.Contains("En Eski Tapınağı", check.Detail!, StringComparison.Ordinal);
    }

    // ---- Ölçülmemiş = geçmemiş ----

    /// QC'nin bel kemiği: "ölçemedim" ile "geçti" aynı şey değil.
    /// Eşitlemek, thumbnail üretilmediği için hiç bakılmayan bir
    /// videonun tam puanla geçmesi demek olurdu.
    [Fact]
    public void OlculmemisKontrol_GecmisSayilmaz()
    {
        var input = new QcInput { Timeline = Timeline() };

        var report = MechanicalQc.Run(input);

        Assert.True(report.HasBlockingFailure);
        Assert.Equal(0, report.Score);
        Assert.All(
            report.Checks.Where(c => c.Detail == "ölçülmedi"),
            c => Assert.False(c.Passed));
    }

    [Fact]
    public void RenderYapilmamis_RenderKontrolleriDuser()
    {
        var report = MechanicalQc.Run(Healthy() with { Media = null });

        Assert.False(Check(report, "qc.duration").Passed);
        Assert.False(Check(report, "qc.resolution").Passed);
        Assert.False(Check(report, "qc.audio_present").Passed);
    }

    // ---- Skor ve karar ----

    /// Bloklayıcı düştüyse skor ANLAMSIZ ve sıfır. Yüksek bir skorla
    /// birlikte "ama bloklayıcı düştü" demek, ikisinden birinin gözden
    /// kaçmasına davetiye olurdu.
    [Fact]
    public void BloklayiciDuserse_SkorSifir()
    {
        var input = Healthy() with { Claims = new ClaimCoverage(5, 0) };

        var report = MechanicalQc.Run(input);

        Assert.Equal(0, report.Score);
        Assert.Equal(QualityDecision.Retry, report.Decision);
    }

    /// Yalnızca uyarılar düşerse skor düşüyor ama yayın durmuyor.
    [Fact]
    public void YalnizcaUyarilarDuserse_SkorDuserYayinDurmaz()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { TruePeakDb = 0.5, SpeechRatio = 0.2 },
        };

        var report = MechanicalQc.Run(input);

        Assert.False(report.HasBlockingFailure);
        Assert.InRange(report.Score, 85, 99);
        Assert.Equal(QualityDecision.Publish, report.Decision);
    }

    [Theory]
    [InlineData(100, QualityDecision.Publish)]
    [InlineData(85, QualityDecision.Publish)]
    [InlineData(84, QualityDecision.NeedsApproval)]
    [InlineData(70, QualityDecision.NeedsApproval)]
    [InlineData(69, QualityDecision.Retry)]
    public void KararEsikleri(int score, QualityDecision expected)
    {
        // Eşiklerin kendisini sınamak için yapay bir rapor: gerçek
        // girdiden tam olarak 84 puan üretmek kırılgan olurdu.
        var report = new QualityReport
        {
            Checks =
            [
                new()
                {
                    Code = "x", Name = "x", Passed = true,
                    Severity = CheckSeverity.Warning, Weight = score,
                },
                new()
                {
                    Code = "y", Name = "y", Passed = false,
                    Severity = CheckSeverity.Warning, Weight = 100 - score,
                },
            ],
        };

        Assert.Equal(score, report.Score);
        Assert.Equal(expected, report.Decision);
    }

    // ---- retry_target ----

    /// §14.3'ün kritik noktası: birden çok hedef varsa BORU HATTINDA
    /// EN ERKEN olan seçiliyor. Senaryo bozukken render'a dönmek iki
    /// tur harcar ve ikinci turda yine aynı senaryo hatasına düşer.
    [Fact]
    public void BirdenCokHedef_EnErkeniSecilir()
    {
        var input = Healthy() with
        {
            Claims = new ClaimCoverage(5, 1),
            Media = Healthy().Media! with { DurationSeconds = 3.0 },
            Metadata = Healthy().Metadata! with { Title = new string('a', 200) },
        };

        var report = MechanicalQc.Run(input);

        Assert.Equal(RetryTarget.Script, report.Target);
    }

    [Fact]
    public void YalnizcaRenderBozuksa_RenderaDonulur()
    {
        var input = Healthy() with
        {
            Media = Healthy().Media! with { DurationSeconds = 3.0 },
        };

        Assert.Equal(RetryTarget.Render, MechanicalQc.Run(input).Target);
    }

    [Fact]
    public void YalnizcaMetadataBozuksa_MetadataDonulur()
    {
        var input = Healthy() with
        {
            Metadata = Healthy().Metadata! with { Title = new string('a', 200) },
        };

        Assert.Equal(RetryTarget.Metadata, MechanicalQc.Run(input).Target);
    }

    /// Geçen bir kontrolün hedefi bir niyet beyanı değil; hedef
    /// seçiminde sayılmamalı.
    [Fact]
    public void HerSeyGecerse_HedefYok()
    {
        Assert.Equal(RetryTarget.None, MechanicalQc.Run(Healthy()).Target);
    }

    /// Her düşen kontrolün bir hedefi olmalı — yoksa "kötü ama nereye
    /// döneceğim belli değil" durumu oluşur ve §14.3 boşa çıkar.
    [Fact]
    public void DusenHerKontrolun_HedefiVar()
    {
        var report = MechanicalQc.Run(new QcInput { Timeline = Timeline(scenes: []) });

        Assert.All(
            report.Failures.Where(f => f.Code != "qc.clipping" && f.Code != "qc.speech_ratio"),
            f => Assert.NotEqual(RetryTarget.None, f.Target));
    }

    /* ---- iddia bagimsizligi ---- */

    /// ***AYNI MODELLE DOGRULAMA SKORDAN PUAN GOTURUYOR.***
    ///
    /// BU TESTIN VAR OLMA SEBEBI: `same_model` alani
    /// `claims.same_model` kolonuna yaziliyor, node ciktisinda
    /// duruyor ve QC girdisine HIC girmiyordu -- yani "bu iddialari
    /// uyduran modelin KENDISI dogruladi" bilgisi hicbir karara
    /// dokunmuyordu.
    ///
    /// Ayni modelin kendi yazdigini dogrulamasi ZAYIF bir dogrulama:
    /// model kendi urettigi hatayi hata olarak gormuyor.
    [Fact]
    public void AyniModel_KontroluDusuruyor()
    {
        var input = Healthy() with { Claims = new ClaimCoverage(5, 5) { SameModel = true } };

        var check = MechanicalQc.Run(input).Checks
            .Single(c => c.Code == "qc.claims_independent");

        Assert.False(check.Passed);
        Assert.Contains("KEND", check.Detail, StringComparison.Ordinal);
    }

    /// ***BLOKLAYICI DEGIL -- YAYIN DURMUYOR.***
    ///
    /// Anahtarsiz hatta senaryoyu da iddiayi da AYNI model uretiyor
    /// (tek LLM var). Bloklayici yapmak bugun HER videoyu dusurmek
    /// demekti -- yani kontrolu kapatmak zorunda kalirdik ve kapali
    /// bir kontrol hic yokla ayni.
    [Fact]
    public void AyniModel_YayiniDurdurmuyor()
    {
        var report = MechanicalQc.Run(
            Healthy() with { Claims = new ClaimCoverage(5, 5) { SameModel = true } });

        Assert.False(report.HasBlockingFailure);
        Assert.True(report.Score < 100, "zayiflik skora yansimali");
    }

    /// AYRI MODELLE DOGRULAMA GECIYOR.
    [Fact]
    public void AyriModel_Geciyor()
    {
        var check = MechanicalQc.Run(
            Healthy() with { Claims = new ClaimCoverage(5, 5) { SameModel = false } })
            .Checks.Single(c => c.Code == "qc.claims_independent");

        Assert.True(check.Passed);
    }

    /// ***IDDIA YOKSA GECIYOR.***
    ///
    /// Dogrulanacak bir sey olmadiginda "bagimsiz dogrulanmadi"
    /// demek yanlis olurdu.
    [Fact]
    public void IddiaYok_Geciyor()
    {
        var check = MechanicalQc.Run(
            Healthy() with { Claims = new ClaimCoverage(0, 0) { SameModel = true } })
            .Checks.Single(c => c.Code == "qc.claims_independent");

        Assert.True(check.Passed);
    }
}
