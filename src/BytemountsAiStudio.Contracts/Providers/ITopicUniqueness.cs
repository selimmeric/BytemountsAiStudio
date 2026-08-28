using BytemountsAiStudio.Core;

namespace BytemountsAiStudio.Contracts.Providers;

/// Bir konunun daha önce yayınlanıp yayınlanmadığı (ADR-003, §20.5).
public sealed record UniquenessVerdict
{
    public required bool IsUnique { get; init; }

    /// En yakın yayının benzerliği (0–1). Karşılaştırma yapılabildiyse
    /// dolu.
    public double? Similarity { get; init; }

    /// Çakışan yayının başlığı. "Benzer bir şey var" demek yetmiyor;
    /// hangi video olduğunu bilmeden karar verilemiyor.
    public string? ConflictingTitle { get; init; }

    /// Karşılaştırma NASIL yapıldı.
    ///
    /// Kayıtlı olmak zorunda: başlık karşılaştırmasıyla anlam
    /// karşılaştırması aynı güvence değil ve "tekil" damgasının ne
    /// kadar güçlü olduğu buna bağlı. Belirtmeden ikisini eşitlemek,
    /// zayıf bir kontrolü güçlü göstermek olurdu.
    public required string Method { get; init; }
}

/// Konu tekilliği kontrolü.
///
/// AYRI BİR ARAYÜZ: uygulaması gömme vektörü kullanabilir (pgvector,
/// ADR-003) ya da yalnızca başlık karşılaştırması yapabilir. QC hangisi
/// olduğunu bilmiyor — ama `Method` alanı sayesinde raporda görünüyor.
///
/// SAĞLAYICI YOKSA QC "ÖLÇÜLMEDİ" DİYOR, "tekil" demiyor. Ölçülmeyen
/// bir kontrolü geçmiş saymak, aynı videoyu ikinci kez yayınlamanın en
/// sessiz yolu olurdu.
public interface ITopicUniqueness
{
    Task<Result<UniquenessVerdict>> CheckAsync(
        Guid? channelId,
        string language,
        string title,
        CancellationToken cancellationToken);
}
