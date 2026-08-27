namespace BytemountsAiStudio.Core.Errors;

/// Hata sinifi. Retry politikasini bu belirler - mesaj metni degil.
///
/// Mimari §8.4: yanlis siniflandirma iki yonde de pahali. Kalici hatayi
/// gecici sanmak parayi bosa harcatir; gecici hatayi kalici sanmak run'i
/// gereksiz oldurur.
public enum ErrorKind
{
    /// Gecici: 429, 502, timeout, ag kesintisi. Backoff ile tekrar denenir.
    Transient = 0,

    /// Kalici: 400, gecersiz girdi, politika reddi. Tekrar denenmez, node duser.
    Permanent = 1,

    /// Zehirli: her denemede ayni sekilde cokuyor. DLQ'ya gider.
    Poison = 2,

    /// Kaynak: kota bitti, butce doldu, rate limit. Hata degil - ERTELEME.
    /// Run basarisiz olmaz, WaitingResource durumuna gecer.
    Resource = 3,

    /// Iptal: kill-switch, kullanici iptali, kapanma. Hata sayilmaz.
    Cancelled = 4,
}
