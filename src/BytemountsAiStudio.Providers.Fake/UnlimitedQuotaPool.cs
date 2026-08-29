using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Providers.Fake;

/// Sınırsız kota havuzu — sahte hat için (P4-04).
///
/// SAHTE HATTIN KOTASI YOK VE BU AÇIKÇA YAZILI. Sahte yayıncı gerçek
/// bir API'ye gitmiyor; ona bir kota defteri uydurmak, olmayan bir
/// sınırı taklit etmek ve gerçek kota mantığını sınamak yerine
/// taklidini sınamak olurdu.
///
/// Node'un kotayı opsiyonel almasındansa bu sınıf var: "kota yok"
/// bir karar olarak duruyor, unutulmuş bir bağımlılık olarak değil.
public sealed class UnlimitedQuotaPool : IQuotaPool
{
    public const string AccountName = "sahte";

    public Task<Result<PoolDecision>> ReserveAsync(
        string providerKey, Guid? channelId, int cost, CancellationToken cancellationToken)
        => Task.FromResult(Result.Success(new PoolDecision(
            PoolOutcome.Selected,
            AccountName,
            cost,
            int.MaxValue,
            int.MaxValue,
            "Sahte hat: kota defteri yok, sınır uygulanmıyor.")));
}
