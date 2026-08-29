using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Contracts.Providers;

/// Kota havuzu (P4-04).
///
/// ARAYÜZ, ÇÜNKÜ SAHTE HATTIN KOTASI YOK. Sahte yayıncı gerçek bir
/// API'ye gitmiyor ve onu bir kota defterine bağlamak, olmayan bir
/// sınırı taklit etmek olurdu. Ama node'un kotayı OPSİYONEL olarak
/// alması da yanlış: geçirmeyi unutan bir kurulum, kota kontrolünü
/// sessizce kapatırdı — bu depodaki en pahalı hata sınıfı.
///
/// Çözüm: zorunlu bir arayüz ve sahte hat için AÇIKÇA sınırsız bir
/// gerçekleme. "Kota yok" bir karar olarak yazılı duruyor.
public interface IQuotaPool
{
    /// Havuzdan kota rezerve eder ve seçilen hesabı döner.
    Task<Result<PoolDecision>> ReserveAsync(
        string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken);
}
