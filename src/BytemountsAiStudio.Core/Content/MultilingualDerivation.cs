using System.Text.Json;
using System.Text.Json.Nodes;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core.Content;

/// Tek bilgi tabanından N dilde içerik (P6-06, §20.7).
///
/// ***ÇEVİRİ TÜREV DEĞİL — ve bütün mesele bu.***
///
/// Türkçe senaryoyu İngilizceye çevirmek, İngilizce kelimelerle Türkçe
/// cümle ritmi üretiyor: açılış cümlesi Türk izleyici için kurulmuş,
/// örnekler Türkiye'den, esprinin çevirisi espri değil. Metin
/// "İngilizce" oluyor ama İngilizce konuşan biri için yazılmamış
/// oluyor — ve bunu ancak izlenme oranı söylüyor.
///
/// DOĞRUSU: araştırmayı (olgular ve kaynaklar) yeniden kullanmak,
/// senaryoyu hedef dilde SIFIRDAN yazdırmak. Pahalı olan araştırma
/// zaten yapılmış; ucuz olan senaryo yeniden yazılıyor.
///
/// NE TAŞINIYOR: konu kimliği, araştırma kaynakları ve olgular.
/// NE TAŞINMIYOR: senaryo, ses, altyazı, timeline, görseller, render,
/// SEO ve **doğrulanmış iddialar**.
///
/// İDDİALARIN TAŞINMAMASI BİLİNÇLİ. Doğrulama bir CÜMLEYE yapılıyor,
/// bir olguya değil: "1453'te fethedildi" cümlesi doğrulandıysa,
/// İngilizce karşılığı henüz doğrulanmadı. Taşımak, hiç kimsenin
/// okumadığı bir cümleyi "kaynakla desteklendi" diye işaretlemek
/// olurdu.
public static class MultilingualDerivation
{
    /// Türev koşunun başlangıç bağlamı.
    ///
    /// Yeni run BOŞTAN başlamıyor ama BİTMİŞ de başlamıyor: araştırma
    /// hazır, senaryodan sonrası yeniden üretilecek.
    public static Result<string> InitialContext(string? sourceContextJson, LanguageTag target)
    {
        JsonNode? parsed;

        try
        {
            parsed = JsonNode.Parse(
                string.IsNullOrWhiteSpace(sourceContextJson) ? "{}" : sourceContextJson);
        }
        catch (JsonException ex)
        {
            return Error.Permanent("derivation.bad_context", ex.Message);
        }

        if (parsed is not JsonObject source)
        {
            return Error.Permanent("derivation.bad_context", "Kaynak run bağlamı bir nesne değil.");
        }

        if (source["research"] is not JsonObject research)
        {
            // ARAŞTIRMASIZ TÜREV YOK. Türetmenin tek kazancı araştırmayı
            // yeniden kullanmak; o yoksa yapılacak şey yeni bir koşu
            // başlatmak, "türev" demek değil.
            return Error.Permanent("derivation.no_research",
                "Kaynak koşuda araştırma yok; türetilecek bir bilgi tabanı da yok.");
        }

        var topic = source["topic"] as JsonObject;
        var sourceLanguage = topic?["language"]?.GetValue<string>();

        if (sourceLanguage is not null
            && string.Equals(sourceLanguage, target.Value, StringComparison.OrdinalIgnoreCase))
        {
            // AYNI DİLE TÜREV, TÜREV DEĞİL — AYNI VİDEONUN İKİNCİSİ.
            // Tekillik kontrolü kanal+dil kapsamında (§20.5), yani bu
            // ikinci koşu tekrar sayılmaz ve sessizce aynı videoyu
            // ikinci kez üretirdi.
            return Error.Permanent("derivation.same_language",
                $"Kaynak koşu zaten {target.Value} dilinde; türev başka bir dile yapılır.");
        }

        var derived = new JsonObject
        {
            // ARAŞTIRMA AYNEN TAŞINIYOR: kaynaklar dilden bağımsız.
            // Bir Wikipedia sayfası Türkçe koşuda da İngilizce koşuda
            // da aynı sayfa ve yeniden çekmek hem para hem zaman.
            ["research"] = research.DeepClone(),
        };

        if (topic is not null)
        {
            var copy = topic.DeepClone()!.AsObject();

            // DİL DEĞİŞİYOR, KONU AYNI KALIYOR: konu kimliği
            // taşınmazsa iki koşu birbirinin türevi olduğunu
            // bilmez ve öğrenme döngüsü onları bağımsız sanardı.
            copy["language"] = target.Value;

            derived["topic"] = copy;
        }
        else
        {
            derived["topic"] = new JsonObject { ["language"] = target.Value };
        }

        // TÜREV OLDUĞU KAYDA GİRİYOR.
        //
        // "Bu video neden araştırma adımı koşmadan başladı" sorusunun
        // cevabı burada; olmadan, atlanan bir adım hata gibi görünürdü.
        derived["derivation"] = new JsonObject
        {
            ["from_language"] = sourceLanguage,
            ["to_language"] = target.Value,

            // ÇEVİRİ DEĞİL: senaryo hedef dilde sıfırdan yazılıyor ve
            // bunun kayıtta durması, sonradan "çeviri mi" sorusunu
            // cevaplıyor.
            ["method"] = "regenerated",
        };

        return Result.Success(derived.ToJsonString());
    }

    /// Türev bağlamda taşınmayan node çıktıları.
    ///
    /// Liste BELGE DEĞİL, KONTROL: `Carries` bu listeyi kullanarak
    /// karar veriyor. Ayrı bir yerde tutmak, listenin koddan
    /// ayrışması ve "taşınmıyor" denen bir alanın sessizce taşınması
    /// demekti.
    public static IReadOnlySet<string> Dropped { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "script",
        "tts",
        "visuals",
        "music",
        "timeline",
        "render",
        "seo",
        "thumbnail",
        "claims",
        "qc",
        "qcs",
        "onay",
        "experiments",
    };

    /// Bir node çıktısı türeve taşınıyor mu.
    public static bool Carries(string nodeId)
        => !Dropped.Contains(nodeId);
}
