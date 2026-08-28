using BytemountsAiStudio.Core.Assets;

namespace BytemountsAiStudio.Persistence.Storage;

/// Varlık saklama kuralı (P4-02).
///
/// SORUN ŞU: günde yüz video, video başına onlarca varlık. Hiçbir şey
/// silinmezse depo sınırsız büyüyor ve maliyet üretimle değil
/// GEÇMİŞLE orantılı hale geliyor — bir yıl önce üretilmiş bir
/// videonun ara dosyaları için her ay para ödemek.
///
/// AMA KÖRÜ KÖRÜNE SİLMEK DAHA KÖTÜ. Silinen bir varlık geri
/// gelmiyor ve bazıları geri gelemez:
///
///   - YAYINLANMIŞ VİDEO: platformdaki kopya bizim değil. Bir telif
///     itirazında ya da yeniden yüklemede elimizde kalan tek şey bu.
///   - LİSANSLI DIŞ VARLIK: lisans kaydı hangi dosyaya ait olduğunu
///     söylüyor. Dosya gidince kayıt bir şeyi ispatlamıyor (§2.3/14).
///
/// Bu ikisi HİÇBİR ZAMAN silinmiyor; ara ürünler yaşlarına göre
/// siliniyor ve içerik-adresli olduğu için tekrar üretilebiliyorlar.
public static class RetentionPolicy
{
    /// Ara ürünler bu süreden sonra silinebilir.
    ///
    /// Otuz gün: bir videonun performansı ilk haftalarda belli
    /// oluyor ve "bunu yeniden render edelim" kararı o pencerede
    /// veriliyor. Daha kısası, düzeltilebilir bir videoyu sıfırdan
    /// üretmeye zorlardı.
    public static readonly TimeSpan IntermediateAge = TimeSpan.FromDays(30);

    /// Bir varlık silinebilir mi.
    ///
    /// KARAR SAF: yaş ve tür dışarıdan veriliyor, hiçbir şey
    /// sorgulanmıyor. Depoya ya da veritabanına bağlı olsaydı "bu
    /// silinir miydi" sorusu ancak gerçek veriyle cevaplanabilirdi —
    /// ve yanlış cevabın bedeli geri alınamaz.
    public static RetentionDecision Decide(
        AssetKind kind, TimeSpan age, bool published, bool externallyLicensed)
    {
        // YAYINLANMIŞ VİDEO HİÇ SİLİNMİYOR.
        //
        // Platformdaki kopya bizim değil: kaldırılabiliyor, yeniden
        // kodlanıyor ve indirilemiyor. Bir telif itirazında ya da
        // yeniden yüklemede elimizde kalan tek şey bu.
        if (published)
        {
            return new RetentionDecision(false, "yayınlanmış içerik");
        }

        // LİSANSLI DIŞ VARLIK HİÇ SİLİNMİYOR.
        //
        // Lisans kaydı hangi dosyaya ait olduğunu söylüyor; dosya
        // gidince kayıt bir şeyi ispatlamıyor. Uyum kaydı, kanıtı
        // olmayan bir beyana dönüşürdü (§2.3/14).
        if (externallyLicensed)
        {
            return new RetentionDecision(false, "lisans kanıtı");
        }

        // NİHAİ VİDEO YAYINLANMAMIŞ OLSA DA SAKLANIYOR.
        //
        // Onay bekleyen ya da reddedilmiş bir video, insanın hâlâ
        // bakabileceği bir şey. Ara ürünlerden ayıran fark: bu
        // yeniden üretilemez — üreten model, istem ve rastgelelik
        // aynı çıktıyı vermiyor.
        if (kind == AssetKind.Video)
        {
            return new RetentionDecision(false, "nihai video");
        }

        // SINIR DIŞARIDA: tam otuz günlük bir varlık "otuz günden
        // eski" DEĞİL. `<` yazmak, kuralın kendi adını yalan
        // çıkarırdı — ve silme kararında adı ile davranışı ayrışan
        // bir kural, en kötü kural türü.
        if (age <= IntermediateAge)
        {
            return new RetentionDecision(false, "yeterince eski değil");
        }

        // ARA ÜRÜN VE ESKİ: silinebilir.
        //
        // İçerik-adresli olduğu için tekrar üretilebiliyor ve aynı
        // sha256'ya düşüyor: silme kararı geri alınabilir bir karar.
        return new RetentionDecision(true, "ara ürün, 30 günden eski");
    }
}

/// Silme kararı ve GEREKÇESİ.
///
/// Yalnızca `bool` dönseydi, bir varlığın neden silindiği (ya da
/// neden silinmediği) hiçbir yerde yazılı olmazdı — ve depo
/// beklenmedik şekilde büyüdüğünde ya da bir dosya kaybolduğunda
/// cevap aranacak yer kalmazdı.
public readonly record struct RetentionDecision(bool CanDelete, string Reason);
