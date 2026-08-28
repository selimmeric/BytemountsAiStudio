using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Media.Timeline;

/// Render'ın girdi belgesi (mimari §11).
///
/// ADR-007 — DEĞİŞMEZ KURAL: burada "sonra bulunacak" hiçbir alan olamaz.
/// Her varlık sha256 ile çözümlenmiş, her süre ölçülmüş, her metin normalize
/// edilmiştir. Render worker'ı ağa çıkmaz.
///
/// Bunun karşılığı üç şey: render tekrarlanabilir, önbelleklenebilir
/// (belge hash'i = çıktı kimliği) ve FFmpeg olmadan test edilebilir.
public sealed record TimelineDocument
{
    /// Şema sürümü. Eski belgeler yeni motorla açıldığında göç yolu bundan
    /// geçer; sürümsüz belge bir gün sessizce yanlış yorumlanır.
    public int SchemaVersion { get; init; } = 1;

    public required Canvas Canvas { get; init; }

    public required LanguageTag Language { get; init; }

    /// Sağdan sola yazılan diller için. Altyazı hizalaması ve kutu yönü buna bakar.
    public bool RightToLeft { get; init; }

    /// Sıralı font zinciri. Eksik glif bir sonrakinden alınır; zincir yoksa
    /// ekranda tofu (□□□) çıkar ve bunu ancak izleyici fark eder (§20.4).
    public IReadOnlyList<string> FontStack { get; init; } = ["Inter", "Noto Sans"];

    public required Ms Duration { get; init; }

    public required AudioTrack Audio { get; init; }

    public required IReadOnlyList<Scene> Scenes { get; init; }

    public CaptionTrack? Captions { get; init; }

    public IReadOnlyList<PersistentLayer> PersistentLayers { get; init; } = [];

    public IReadOnlyDictionary<string, TextStyle> Styles { get; init; } =
        new Dictionary<string, TextStyle>(StringComparer.Ordinal);

    public required OutputSpec Output { get; init; }

    /// Hangi prompt sürümü hangi videoyu üretti — öğrenme döngüsünün temeli
    /// (ADR-012). Render için gereksiz, hesap verebilirlik için zorunlu.
    public Provenance? Provenance { get; init; }
}

public sealed record AudioTrack
{
    public required IReadOnlyList<VoiceSegment> VoiceSegments { get; init; }

    public MusicBed? Music { get; init; }

    /// Yayın standardı: konuşma ağırlıklı içerikte -16 LUFS.
    public double TargetLufs { get; init; } = -16.0;
}

/// Tek bir seslendirme parçası.
///
/// `Duration` TTS'in bildirdiği değil, ÖLÇÜLEN süredir (ADR-006). Sağlayıcının
/// bildirdiği süreye güvenmek her videoda birikimli kayma üretir.
public sealed record VoiceSegment
{
    public required string Id { get; init; }

    public required AssetRef Asset { get; init; }

    public required Ms Start { get; init; }

    public required Ms Duration { get; init; }

    public Ms End => Start + Duration;

    /// Okunmuş metin — ekranda görünen değil. "1453" burada
    /// "bin dört yüz elli üç" olarak durur (§20.3).
    public string? SpeechText { get; init; }
}

public sealed record MusicBed
{
    public required AssetRef Asset { get; init; }

    public bool Loop { get; init; } = true;

    /// Konuşmanın altında kalması gereken seviye. §13: müzik %8–15.
    public double GainDb { get; init; } = -22.0;

    public DuckingSpec? Ducking { get; init; }

    public Ms FadeIn { get; init; } = new(1200);

    public Ms FadeOut { get; init; } = new(2000);

    /// LİSANS KANITI (§2.3/13).
    ///
    /// Nullable ve bilinçli öyle: eksikliği GÖRÜLEBİLİR olmalı.
    /// Zorunlu kılsaydık çağıran taraf boş bir kayıt uydurup geçerdi
    /// ve kontrol hiçbir şey yakalamazdı. Bloklayıcı QC kuralı tam
    /// olarak bu alanın dolu olup olmadığına bakıyor — Content ID
    /// talebi kanalın gelirini götürüyor ve bu, düzeltilebilir bir
    /// kusur değil.
    public MusicLicense? License { get; init; }
}

/// Müzik varlığının lisans kanıtı (P2-09).
public sealed record MusicLicense
{
    public required string Name { get; init; }

    public string? Author { get; init; }

    public Uri? Url { get; init; }

    /// Atıf zorunluysa video açıklamasına girmek ZORUNDA.
    public bool RequiresAttribution { get; init; }

    /// Lisansın hangi anda okunduğu. Kurallar değişiyor ve "o gün ne
    /// yazıyordu" sorusunun cevabı ancak alındığı anda saklanmışsa
    /// var.
    public required DateTimeOffset CapturedAt { get; init; }

    /// Kanıt yeterli mi.
    ///
    /// Atıf gerekiyorsa YAZAR ADI ŞART: "CC BY" deyip yazarı
    /// bilmemek, atfı yapılamaz kılıyor ve lisansı ihlal ediyor.
    public bool IsComplete
        => !string.IsNullOrWhiteSpace(Name)
           && (!RequiresAttribution || !string.IsNullOrWhiteSpace(Author));
}

public sealed record DuckingSpec
{
    public double TargetGainDb { get; init; } = -30.0;

    public int AttackMs { get; init; } = 150;

    public int ReleaseMs { get; init; } = 600;
}

public sealed record Scene
{
    public required int Index { get; init; }

    public required TimeRange Range { get; init; }

    /// Bu sahnede duyulan ses parçaları.
    public IReadOnlyList<string> VoiceSegmentIds { get; init; } = [];

    /// §11.3: sahne başına TEK görsel. İki görsel gerekiyorsa iki sahnedir.
    /// Bu kural modeli basit ve render'ı öngörülebilir tutuyor.
    public required SceneVisual Visual { get; init; }

    public IReadOnlyList<TextOverlay> Overlays { get; init; } = [];

    /// Sahnenin BAŞINDAKİ açılma (P3-04).
    ///
    /// `TransitionOut`'un simetriği ve ilk sahne için var: video
    /// siyahtan açılmıyordu, ilk kare tam parlaklıkta patlıyordu.
    /// Kapanış da öyleydi — son sahnenin `TransitionOut`'u kasten
    /// `null`'dı, çünkü "sahneler arası geçiş" olarak düşünülmüştü.
    /// Oysa videonun kendi başı ve sonu da bir geçiş.
    public Transition? TransitionIn { get; init; }

    public Transition? TransitionOut { get; init; }
}

public enum VisualFit
{
    /// Kadrajı doldur, taşanı kes. Varsayılan.
    Cover = 0,

    /// Tamamı görünsün, boşluğu bulanık arka planla doldur.
    Contain = 1,
}

public sealed record SceneVisual
{
    public required AssetRef Asset { get; init; }

    public VisualFit Fit { get; init; } = VisualFit.Cover;

    public KenBurns? Motion { get; init; }
}

/// Yavaş yakınlaşma/kaydırma.
///
/// §12.1/L3: burada "Slow Zoom In" gibi bir arayüz metni YOK. Hareket bir
/// değer nesnesi; sunum katmanının sözlüğü render çekirdeğine sızmıyor.
public sealed record KenBurns
{
    public required double FromScale { get; init; }

    public required double ToScale { get; init; }

    public double FromX { get; init; }

    public double FromY { get; init; }

    public double ToX { get; init; }

    public double ToY { get; init; }

    public Easing Easing { get; init; } = Easing.EaseInOut;
}

public enum Easing
{
    Linear = 0,
    EaseIn = 1,
    EaseOut = 2,
    EaseInOut = 3,
}

public sealed record TextOverlay
{
    public required string Text { get; init; }

    public required string StyleRef { get; init; }

    public required TimeRange Range { get; init; }
}

public enum TransitionKind
{
    None = 0,
    Fade = 1,
}

public sealed record Transition(TransitionKind Kind, Ms Duration);

public sealed record CaptionTrack
{
    public required string StyleRef { get; init; }

    /// §11.3: kelime seviyesinde, çünkü ASR/TTS hizalamasından geliyor.
    /// Cümle altyazısı bunlardan türetilir; tersi mümkün değil.
    public required IReadOnlyList<CaptionCue> Cues { get; init; }
}

public sealed record CaptionCue
{
    public required string Text { get; init; }

    public required TimeRange Range { get; init; }

    public string? SegmentId { get; init; }

    /// O an vurgulanan kelime mi (karaoke altyazı).
    public bool Emphasis { get; init; }
}

public sealed record PersistentLayer
{
    public required AssetRef Asset { get; init; }

    public required string Role { get; init; }

    public Anchor Anchor { get; init; } = Anchor.TopRight;

    public int MarginX { get; init; } = 40;

    public int MarginY { get; init; } = 40;

    public double Opacity { get; init; } = 0.55;
}

public enum Anchor
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
    Center = 4,
    BottomCenter = 5,
}

/// Metin görünümü. Sahne içine gömülmez, referansla kullanılır:
/// bir kanalın altyazı stilini değiştirmek tek satır olsun (§11.3).
public sealed record TextStyle
{
    public required string FontFamily { get; init; }

    /// Tuval yüksekliğinin yüzdesi. Piksel verirsek 9:16 ve 16:9 arasında
    /// aynı stil bambaşka görünür.
    public required double SizePercent { get; init; }

    public string Color { get; init; } = "#FFFFFF";

    public string? HighlightColor { get; init; }

    public string? StrokeColor { get; init; }

    public int StrokeWidth { get; init; }

    public string? BoxColor { get; init; }

    public double BoxOpacity { get; init; }

    public Anchor Position { get; init; } = Anchor.BottomCenter;

    /// Konumun tuval kenarından uzaklığı, yükseklik yüzdesi olarak.
    public double OffsetPercent { get; init; } = 18;

    public int MaxLines { get; init; } = 2;

    public bool Bold { get; init; }
}

public sealed record OutputSpec
{
    public required string Preset { get; init; }

    public string Container { get; init; } = "mp4";

    public string VideoCodec { get; init; } = "libx264";

    public int Crf { get; init; } = 20;

    public string PresetSpeed { get; init; } = "medium";

    public string AudioCodec { get; init; } = "aac";

    public string AudioBitrate { get; init; } = "192k";

    /// yuv420p: yaygın oynatıcı uyumluluğu için zorunlu. Bunu değiştirmek
    /// bazı cihazlarda videonun hiç açılmamasına yol açar.
    public string PixelFormat { get; init; } = "yuv420p";

    /// Anahtar kareler arası EN ÇOK kaç saniye. `null` = kodlayıcı
    /// kendi bilir.
    ///
    /// NEDEN VAR (P3-02): oynatıcı yalnızca anahtar kareye
    /// atlayabiliyor. Sınır koymadığımızda x264 anahtar kareleri
    /// sahne değişimine göre seçiyor ve aralık içeriğe bağlı oluyor —
    /// on dakikalık, bölüm işaretli bir videoda "3. bölüme atla"
    /// saniyelerce sapabiliyor. Bölüm işaretlerini üretip atlamanın
    /// nereye düşeceğini şansa bırakmak, işaretlerin yarısını
    /// vermekti.
    ///
    /// KISA VİDEODA GEREKMİYOR: 48 saniyelik bir Shorts'ta kimse
    /// atlamıyor ve daha uzun GOP daha iyi sıkıştırma demek. Sınırı
    /// her yere koymak, hiçbir faydası olmayan bir yerde dosyayı
    /// büyütürdü.
    ///
    /// SINIR, HEDEF DEĞİL: `-g` en çok bu kadar diyor; sahne
    /// değişimi daha sık anahtar kare ekleyebiliyor ve bu iyi —
    /// atlama daha da isabetli oluyor.
    public double? KeyframeIntervalSeconds { get; init; }
}

public sealed record Provenance
{
    public string? ScriptId { get; init; }

    public IReadOnlyDictionary<string, string> PromptVersions { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? EngineMinVersion { get; init; }
}
