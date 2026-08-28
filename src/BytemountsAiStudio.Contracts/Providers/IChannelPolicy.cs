using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Contracts.Providers;

/// Kanala özel çalışma politikası.
///
/// AYRI BİR ARAYÜZ: node'lar veritabanına doğrudan bağlanmıyor
/// (§6.1 — işleyici ince bir adaptör). Uygulaması Persistence'ta, node
/// yalnızca soruyor.
///
/// SAĞLAYICI YOKSA NODE KENDİ VARSAYILANINI KULLANIYOR. Kanalsız bir
/// koşu (CLI denemesi, bakım) da çalışmalı ve o durumda en güvenli
/// davranış geçerli olmalı — onay kapısı için bu "her videoyu sor".
public interface IChannelPolicy
{
    /// Kanalın onay modu. Kanal yoksa `null`.
    ///
    /// `null` ile `Approval` AYRI: biri "kanal bulunamadı", diğeri
    /// "kanal her videoyu sormak istiyor". Eşitlemek, silinmiş bir
    /// kanalın politikasını varmış gibi göstermekti.
    Task<ChannelMode?> ModeAsync(Guid channelId, CancellationToken cancellationToken);

    /// Kanalın tüm çalışma ayarları (P3-01): ses, yazı tipi, tempo,
    /// tür karışımı, bütçe eylemi.
    ///
    /// TEK ÇAĞRIDA HEPSİ: her ayar için ayrı sorgu, aynı belgeyi
    /// node başına birkaç kez okumak olurdu. Kanal yoksa `null`.
    Task<ChannelSettings?> SettingsAsync(Guid channelId, CancellationToken cancellationToken);
}
