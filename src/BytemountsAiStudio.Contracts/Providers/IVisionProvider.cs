using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;

namespace BytemountsAiStudio.Contracts.Providers;

/// Tek bir karenin, tek bir cümleye göre değerlendirilmesi (P2-06).
public sealed record VisionQuery
{
    /// Karenin ham baytları (PNG ya da JPEG).
    public required ReadOnlyMemory<byte> Image { get; init; }

    /// Bu karenin altında duyulan cümle. Model "bu görsel bu cümleyi
    /// destekliyor mu" sorusuna cevap veriyor.
    public required string Sentence { get; init; }

    public LanguageTag? Language { get; init; }
}

/// Modelin yargısı.
public sealed record VisionVerdict
{
    /// 0–1 arası alaka. Modelin ürettiği tek sayı bu.
    public required double Relevance { get; init; }

    /// Gerekçe. Skordan daha çok işe yarıyor: eşiği ayarlarken
    /// bakılan şey bu, çünkü "0,4" tek başına neyin eksik olduğunu
    /// söylemiyor.
    public string? Reason { get; init; }

    /// Modelin karede gördüğü şey. Alakasız bir kareyi düzeltmek için
    /// "ne var" bilgisi, "alakasız" bilgisinden değerli.
    public string? Description { get; init; }
}

/// Görme modeli sağlayıcısı (P2-06).
///
/// AYRI BİR ARAYÜZ, `ILlmProvider`'a eklenmedi. Görme modelleri metin
/// modellerinden ayrı kurulup ayrı ölçekleniyor: metin modeli çalışırken
/// görme modeli kapalı olabiliyor ve olması da gerekiyor — görme modeli
/// hattın en yavaş ve en pahalı adımı. Tek arayüzde birleştirmek, metin
/// çağrısı yapan her yerin görme yeteneğini de varsayması demekti.
///
/// SAĞLAYICI YOKSA SEMANTİK QC "ÖLÇÜLEMEDİ" DİYOR, "geçti" demiyor
/// (`SemanticQc`). Bu ayrım bu depoda tekrar eden bir kural: ölçülmeyen
/// bir kontrol geçmiş sayılmaz.
///
/// Uygulaması yerel bir model de olabilir, dışarıdan bir API de. Karar
/// bu arayüzün arkasında ve QC mantığı hangisi olduğunu bilmiyor.
public interface IVisionProvider : IProvider
{
    Task<Result<ProviderResponse<VisionVerdict>>> JudgeAsync(
        VisionQuery query,
        ProviderContext context,
        CancellationToken cancellationToken);
}
