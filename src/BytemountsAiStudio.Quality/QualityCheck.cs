using System.Globalization;

namespace BytemountsAiStudio.Quality;

/// Bir kontrolün ağırlığı (§14.1).
public enum CheckSeverity
{
    /// Düşerse video YAYINLANMAZ. Skoru hesaplamanın anlamı kalmaz.
    Blocking = 0,

    /// Düşerse skordan puan gider ama yayın durmaz.
    Warning = 1,
}

/// QC düştüğünde HANGİ NODE'a dönüleceği.
///
/// §14.3'ün kritik noktası: QC'nin çıktısı "kötü" değil, "hangi node'a
/// dön" olmalı. Aksi hâlde tüm boru hattı baştan koşar ve para yanar —
/// senaryo iyiyken render bozuksa senaryoyu yeniden üretmenin bedeli
/// var, faydası yok.
public enum RetryTarget
{
    /// Hedef yok: ya her şey yolunda ya da yeniden denemek düzeltmez.
    None = 0,

    // Sayilar BORU HATTINDAKI SIRAYI temsil ediyor, onem derecesini
    // degil. Kucuk olan once kosuyor ve kendinden sonraki her seyi
    // yeniden uretiyor. Siralama bu yuzden anlamli: hedef secimi
    // en kucugu almak.

    /// Senaryo: metin, iddia, uzunluk. Buradan koşmak her şeyi yeniler.
    Script = 1,

    /// Görseller: eksik ya da alakasız kare.
    Visuals = 2,

    /// Timeline: sahne/altyazı zamanlaması.
    Timeline = 3,

    /// Render: süre, çözünürlük, ses seviyesi.
    Render = 4,

    /// Metadata: başlık, etiket, thumbnail. Senaryodan türüyor,
    /// dolayısıyla senaryo yeniden koşarsa bu da yenilenir.
    Metadata = 5,
}

/// Tek bir kontrolün sonucu.
public sealed record CheckResult
{
    public required string Code { get; init; }

    public required string Name { get; init; }

    public required bool Passed { get; init; }

    public required CheckSeverity Severity { get; init; }

    /// Skora katkısı. Bloklayıcı kontroller de puan taşıyor: bir video
    /// bloklayıcıyı geçtiyse skorunun buna göre yükselmesi doğru.
    public required int Weight { get; init; }

    /// Düştüyse NEDEN düştüğü — ölçülen değerle birlikte.
    ///
    /// "Süre uyumsuz" yeterli değil; "13.2 sn beklendi, 9.8 sn ölçüldü"
    /// sorunun nerede olduğunu söylüyor.
    public string? Detail { get; init; }

    /// Düştüyse hangi node'a dönülmeli.
    public RetryTarget Target { get; init; } = RetryTarget.None;

    /// Kontrol GERÇEKTEN ÖLÇÜLDÜ mü.
    ///
    /// "Ölçüldü ve düştü" ile "ölçülemedi" ikisi de `Passed == false`
    /// ama tamamen farklı şeyler — ve ayrımı yapmamak gerçek bir kayba
    /// yol açtı.
    ///
    /// İlk uçtan uca koşuda beş kontrol "ölçülmedi" diye düştü (ses
    /// seviyesi, kırpılma, konuşma oranı, kapak, tekillik) çünkü hat o
    /// ölçümleri hiç üretmiyor. QC bunu bir kalite sorunu sanıp
    /// senaryodan yeniden koşma istedi; sistem üç tur boyunca aynı
    /// videoyu yeniden render etti (her tur ~4 dakika) ve hiçbir şey
    /// değişmedi.
    ///
    /// Yeniden koşmak eksik bir ÖLÇÜM ADIMINI eklemiyor. Ölçülemeyen
    /// bir kontrol insanın çözeceği bir eksik; retry'ın çözeceği bir
    /// kusur değil.
    public bool Measured { get; init; } = true;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"[{(Passed ? "GECTI" : "KALDI")}] {Code}: {Name}{(Detail is null ? "" : $" — {Detail}")}");
}

/// Bütün mekanik kontrollerin toplu sonucu.
public sealed record QualityReport
{
    public required IReadOnlyList<CheckResult> Checks { get; init; }

    /// Geçen kontrollerin ağırlık toplamı, yüzde olarak.
    ///
    /// Bloklayıcı bir kontrol düştüyse skor ANLAMSIZ (§14.3) ve sıfır
    /// dönüyor. Yüksek bir skorla birlikte "ama bloklayıcı düştü"
    /// demek, ikisinden birinin gözden kaçmasına davetiye olurdu.
    public int Score => HasBlockingFailure
        ? 0
        : Checks.Count == 0
            ? 0
            : (int)Math.Round(
                100.0 * Checks.Where(c => c.Passed).Sum(c => c.Weight) / Checks.Sum(c => c.Weight));

    public bool HasBlockingFailure
        => Checks.Any(c => !c.Passed && c.Severity == CheckSeverity.Blocking);

    public IReadOnlyList<CheckResult> Failures => [.. Checks.Where(c => !c.Passed)];

    /// Hangi node'a dönülmeli.
    ///
    /// Birden çok hedef varsa BORU HATTINDA EN ERKEN olan seçiliyor —
    /// yani en KÜÇÜK numaralı. Senaryo bozuksa görseli yeniden üretmenin
    /// anlamı yok: senaryo yeniden koşunca görsel de zaten yenileniyor.
    /// Ters seçim (en geç olanı almak) iki tur harcar ve ikinci turda
    /// yine aynı senaryo hatasına düşerdi.
    ///
    /// Yalnızca DÜŞEN kontrollerin hedefine bakılıyor: geçen bir
    /// kontrolün hedefi bir niyet beyanı değil.
    public RetryTarget Target
    {
        get
        {
            var targets = Checks
                .Where(c => !c.Passed && c.Target != RetryTarget.None)
                .Select(c => c.Target)
                .ToList();

            return targets.Count == 0 ? RetryTarget.None : targets.Min();
        }
    }

    /// §14.3'ün eşikleri.
    public QualityDecision Decision => HasBlockingFailure
        ? QualityDecision.Retry
        : Score >= 85
            ? QualityDecision.Publish
            : Score >= 70
                ? QualityDecision.NeedsApproval
                : QualityDecision.Retry;
}

public enum QualityDecision
{
    /// Otomatik yayın (skor ≥ 85).
    Publish = 0,

    /// İnsan onayı kuyruğuna (70–85). Seçici onay modunun anlamı bu:
    /// her video değil, yalnızca sınırdakiler insana düşüyor.
    NeedsApproval = 1,

    /// `retry_target`'a dön (< 70 ya da bloklayıcı düştü).
    Retry = 2,
}
