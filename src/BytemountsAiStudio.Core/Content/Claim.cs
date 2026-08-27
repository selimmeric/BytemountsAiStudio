using System.Globalization;

namespace BytemountsAiStudio.Core.Content;

/// Bir iddianın kaynak karşısındaki durumu (§2.2/8, P1-10).
public enum ClaimVerdict
{
    /// Kaynak metni bu iddiayı destekliyor.
    Supported = 0,

    /// Kaynakta bu iddiaya dair bir şey yok. YANLIŞ demek DEĞİL —
    /// yalnızca "biz doğrulayamadık". Ayrım önemli: doğrulanamayan bir
    /// iddiayı "yanlış" saymak, kaynağın eksik olduğu her durumda
    /// senaryoyu çöpe atardı.
    Unsupported = 1,

    /// Kaynak metni bu iddiayla ÇELİŞİYOR. Bu ciddi: desteklenmemekten
    /// farklı, çünkü elimizde iddianın yanlış olduğuna dair kanıt var.
    Contradicted = 2,
}

/// Senaryodan çıkarılmış tek bir iddia.
///
/// ATOMİK olmak zorunda: "Göbeklitepe 11 bin yıllıktır ve dünyanın en
/// eski tapınağıdır" iki iddia. Birleşik bırakılırsa doğrulama ikisinden
/// birini kaçırıyor — biri doğru, diğeri yanlış olduğunda cevap
/// belirsizleşiyor.
public sealed record Claim
{
    public required string Text { get; init; }

    /// Bu iddianın geldiği cümlenin senaryodaki sırası. Düzeltme
    /// gerektiğinde hangi cümlenin değişeceğini söylüyor.
    public required int SentenceIndex { get; init; }

    /// Doğrulamada kullanılan kaynağın adresi. Null = hiç kaynak
    /// eşleştirilememiş, ki bu tek başına `Unsupported` demek.
    public string? SourceUrl { get; init; }

    public ClaimVerdict Verdict { get; init; } = ClaimVerdict.Unsupported;

    /// Modelin gerekçesi. Bir iddia neden desteklenmedi sorusunun
    /// cevabı; insan onayı ekranında gösteriliyor.
    public string? Reason { get; init; }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"[{Verdict}] {Text}");
}

/// İddia kümesinin toplu durumu.
///
/// Saf ve ayrı: skor ve karar hesabı model çağırmadan sınanabilsin.
public sealed record ClaimReport
{
    public required IReadOnlyList<Claim> Claims { get; init; }

    public int Total => Claims.Count;

    public int Supported => Claims.Count(c => c.Verdict == ClaimVerdict.Supported);

    public int Unsupported => Claims.Count(c => c.Verdict == ClaimVerdict.Unsupported);

    public int Contradicted => Claims.Count(c => c.Verdict == ClaimVerdict.Contradicted);

    /// İddiasız senaryo geçerli sayılıyor.
    ///
    /// "Bu konu bugün hâlâ tartışılıyor" gibi bir kapanış cümlesi olgu
    /// iddiası taşımıyor ve taşımaması normal. Sıfır iddiayı başarısız
    /// saymak, kanca ve kapanış cümlelerini yasaklamak olurdu.
    public bool AllSourced => Total == 0 || Supported == Total;

    /// ÇELİŞEN bir iddia varsa senaryo yayınlanamaz.
    ///
    /// Desteklenmemekten ayrı tutuluyor: desteklenmeyen bir iddia
    /// "kaynağımız yetersiz" demek, çelişen bir iddia "kaynağımız
    /// bunun yanlış olduğunu söylüyor" demek. İkincisi bir kalite
    /// sorunu değil, doğruluk sorunu.
    public bool HasContradiction => Contradicted > 0;

    /// Düzeltilmesi gereken cümlelerin sırası — tekrarsız ve sıralı.
    /// Hedefli düzeltme (P2-07) buna bakacak.
    public IReadOnlyList<int> ProblemSentences =>
    [
        .. Claims
            .Where(c => c.Verdict != ClaimVerdict.Supported)
            .Select(c => c.SentenceIndex)
            .Distinct()
            .Order(),
    ];

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Supported}/{Total} kaynakli, {Unsupported} desteksiz, {Contradicted} celiskili");
}
