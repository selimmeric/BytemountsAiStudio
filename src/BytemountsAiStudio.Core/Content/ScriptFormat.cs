namespace BytemountsAiStudio.Core.Content;

/// Senaryo biçim şablonu (P1-12, §8).
///
/// Kısa video senaryosunun yapısı türe göre değişiyor ve bu yapı
/// izlenme süresini doğrudan belirliyor: ilk üç saniyede kancayı
/// kuramayan video izlenmiyor, kapanışı olmayan video paylaşılmıyor.
///
/// Biçim, İSTEME giren bir metin — koda gömülü bir dallanma değil.
/// Gerekçe: yeni bir biçim eklemek yeni bir kayıt yazmak olmalı,
/// `switch` koluna dokunmak değil. Ayrıca biçim metni istem
/// dosyasından okunduğu için `git diff`'te görünüyor ve fixture'larla
/// denetlenebiliyor (P1-07).
///
/// Cümle SAYISI da biçimin parçası: "en iyi 10" biçimi üç cümleye
/// sığmıyor, bir tanım videosu on cümleye yayılınca sulanıyor.
public sealed record ScriptFormat
{
    public required string Name { get; init; }

    /// İsteme olduğu gibi giren yapı tarifi.
    public required string Structure { get; init; }

    public required int MinSentences { get; init; }

    public required int MaxSentences { get; init; }

    /// İstemde "kaç cümle" diye söylenen sayı. Alt ve üst sınırın
    /// ortası değil, HEDEF: modele bir aralık vermek dağınık uzunluk
    /// üretiyor, tek bir sayı vermek tutarlı sonuç veriyor.
    public required int TargetSentences { get; init; }

    public bool Accepts(int sentenceCount)
        => sentenceCount >= MinSentences && sentenceCount <= MaxSentences;

    /// Kanca – gelişme – kapanış. Anlatı türü içeriğin varsayılanı.
    ///
    /// Üç bölüm bilinçli: kısa videoda dördüncü bir bölüm izleyiciyi
    /// kaybettiriyor, iki bölüm ise kapanışsız kalıyor.
    public static ScriptFormat HookPayoff { get; } = new()
    {
        Name = "hook-payoff",
        Structure =
            "1. KANCA: ilk cümle merak uyandırmalı. Soru sorma, iddia et.\n"
            + "2. GELİŞME: iddiayı kaynaklardaki bilgiyle destekle.\n"
            + "3. KAPANIŞ: akılda kalan tek bir cümleyle bitir.",
        MinSentences = 3,
        MaxSentences = 5,
        TargetSentences = 3,
    };

    /// Kanca – liste – kapanış. "En iyi 10" türü içerik.
    ///
    /// Liste biçimi üç cümleye sığmıyor: her madde kendi cümlesini
    /// istiyor, yoksa maddeler birbirine karışıyor ve sahne planlayıcı
    /// da onları ayıramıyor.
    public static ScriptFormat HookListPayoff { get; } = new()
    {
        Name = "hook-list-payoff",
        Structure =
            "1. KANCA: ilk cümle listenin neden ilginç olduğunu söylemeli.\n"
            + "2. LİSTE: her madde AYRI bir cümle olmalı; maddeleri birleştirme.\n"
            + "3. KAPANIŞ: son cümle listeyi toparlamalı.",
        MinSentences = 5,
        MaxSentences = 12,
        TargetSentences = 7,
    };

    /// Tanım – bağlam – önem. Ansiklopedik konular.
    public static ScriptFormat Explainer { get; } = new()
    {
        Name = "explainer",
        Structure =
            "1. TANIM: konunun ne olduğunu tek cümlede söyle.\n"
            + "2. BAĞLAM: nerede, ne zaman, kim tarafından.\n"
            + "3. ÖNEM: bugün neden konuşuluyor.",
        MinSentences = 3,
        MaxSentences = 6,
        TargetSentences = 4,
    };

    public static IReadOnlyList<ScriptFormat> All { get; } = [HookPayoff, HookListPayoff, Explainer];

    /// Bilinmeyen ad varsayılanı veriyor, hata DEĞİL.
    ///
    /// Kanal ayarındaki bir yazım hatası içerik üretimini durdurmamalı;
    /// biçim bir tercih, zorunluluk değil.
    public static ScriptFormat Get(string? name)
        => All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? HookPayoff;
}
