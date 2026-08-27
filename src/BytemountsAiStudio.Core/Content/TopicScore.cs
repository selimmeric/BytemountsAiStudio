using System.Globalization;

namespace BytemountsAiStudio.Core.Content;

/// Aday konunun altı boyutlu skoru (P1-08, §7.3).
///
/// TEK SAYI YETMEZ ve mimari bunu açıkça söylüyor: "skor açıklanabilir
/// olmalı". 72 puan almış bir konunun neden 72 aldığını bilmeden
/// eşiği ayarlamak mümkün değil — eşiği düşürünce hangi tür konuların
/// gireceğini kimse söyleyemez.
///
/// Her boyut 0–100. Ağırlıklar kanal ayarından gelebilecek şekilde
/// ayrı duruyor; şimdilik sabit ve gerekçeli.
public sealed record TopicScore
{
    /// İzleyicinin bu konuyu arayıp aramadığı. Kimsenin merak etmediği
    /// bir konu kusursuz üretilse de izlenmiyor.
    public required int Demand { get; init; }

    /// Konunun kısa videoya UYGUNLUĞU. Bazı konular 40 saniyede
    /// anlatılamıyor; anlatılmaya çalışılınca yüzeysel çıkıyor.
    public required int Fit { get; init; }

    /// Kaynak BULUNABİLİRLİĞİ. Doğrulanabilir kaynağı olmayan bir konu
    /// bizim hattımızda üretilemez — iddia doğrulama adımı onu keser.
    public required int Sourceability { get; init; }

    /// GÖRSELLEŞTİRİLEBİLİRLİK. Soyut bir konu için ne stok görsel var
    /// ne de anlamlı bir AI istemi kurulabiliyor.
    public required int Visualizability { get; init; }

    /// TAZELİK: konu güncel mi, yoksa çok işlenmiş mi.
    public required int Freshness { get; init; }

    /// RİSK — ters yönde çalışan tek boyut. Politika ihlali, hassas
    /// içerik, telif sorunu ihtimali. Yüksek risk skoru DÜŞÜK toplam
    /// demek.
    public required int Risk { get; init; }

    /// Modelin gerekçesi. Skorun kendisinden daha çok işe yarıyor:
    /// eşiği ayarlarken bakılan şey bu.
    public string? Rationale { get; init; }

    /// Ağırlıklı toplam (0–100).
    ///
    /// Kaynak bulunabilirliği en ağır boyut, çünkü hattımızın kırılma
    /// noktası orası: kaynağı olmayan konu senaryo aşamasında değil
    /// iddia doğrulama aşamasında düşüyor ve o noktaya kadar harcanan
    /// her şey boşa gidiyor.
    public double Overall
    {
        get
        {
            var positive =
                (Demand * 0.20)
                + (Fit * 0.15)
                + (Sourceability * 0.30)
                + (Visualizability * 0.20)
                + (Freshness * 0.15);

            // Risk CEZA olarak uygulanıyor, boyut olarak değil.
            //
            // Ağırlıklı ortalamaya katsaydık, yüksek riskli bir konu
            // diğer boyutlardan telafi edebilirdi. Oysa politika
            // ihlali riski telafi edilebilir bir şey değil.
            return Math.Clamp(positive - (Risk * 0.5), 0, 100);
        }
    }

    /// Boyutların hepsi geçerli aralıkta mı.
    ///
    /// Model uydurma değer verebiliyor (120, -5). Sıkıştırmak yerine
    /// REDDETMEK doğru: 120 veren bir model muhtemelen boyutu da yanlış
    /// anlamış demektir ve sessizce 100'e çekmek o hatayı gizler.
    public bool IsValid =>
        InRange(Demand) && InRange(Fit) && InRange(Sourceability)
        && InRange(Visualizability) && InRange(Freshness) && InRange(Risk);

    private static bool InRange(int value) => value is >= 0 and <= 100;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Overall:0.#} (talep {Demand}, uygunluk {Fit}, kaynak {Sourceability}, "
            + $"görsel {Visualizability}, tazelik {Freshness}, risk {Risk})");
}

/// Konu havuzunun kabul kararı.
public enum TopicDecision
{
    /// Üretim kuyruğuna girsin.
    Accept = 0,

    /// Skor düşük ama yasak değil; havuzda beklesin. Daha iyi aday
    /// yoksa sonra değerlendirilir.
    Hold = 1,

    /// Reddedilsin. Tekrar denemenin anlamı yok.
    Reject = 2,
}

/// Konu havuzu kararları (P1-08).
///
/// Saf: eşikler ve tekillik kararı model çağırmadan sınanabilsin.
public static class TopicPolicy
{
    /// Bu skorun üstü doğrudan kuyruğa.
    public const double AcceptThreshold = 65.0;

    /// Bu skorun altı reddediliyor.
    public const double RejectThreshold = 40.0;

    /// Bu benzerliğin üstündeki konu TEKRAR sayılıyor.
    ///
    /// 0.88 deneyle ayarlanacak bir sayı ama yönü belli: çok düşük
    /// tutmak farklı konuları birleştirir ("Göbeklitepe" ile
    /// "Çatalhöyük" ikisi de neolitik alan), çok yüksek tutmak aynı
    /// konunun yeniden üretilmesine izin verir. Yanlış birleştirmenin
    /// bedeli daha yüksek: üretilmemiş bir video geri gelmiyor.
    public const double SimilarityThreshold = 0.88;

    /// Riskin tek başına reddettirdiği eşik.
    ///
    /// Diğer boyutlardan bağımsız: politika ihlali riski yüksek bir
    /// konu, ne kadar iyi olursa olsun üretilmemeli.
    public const int RiskVeto = 70;

    public static TopicDecision Decide(TopicScore score, double? highestSimilarity = null)
    {
        ArgumentNullException.ThrowIfNull(score);

        if (!score.IsValid)
        {
            return TopicDecision.Reject;
        }

        if (score.Risk >= RiskVeto)
        {
            return TopicDecision.Reject;
        }

        // Tekrar REDDEDILIYOR, beklemeye alınmıyor: bir konu daha önce
        // yayınlandıysa bekleyerek tekrar olmaktan çıkmıyor.
        if (highestSimilarity >= SimilarityThreshold)
        {
            return TopicDecision.Reject;
        }

        return score.Overall >= AcceptThreshold
            ? TopicDecision.Accept
            : score.Overall >= RejectThreshold
                ? TopicDecision.Hold
                : TopicDecision.Reject;
    }

    /// İki gömme vektörü arasındaki kosinüs benzerliği.
    ///
    /// pgvector aynı hesabı veritabanında yapıyor; bu, aday listesi
    /// bellekteyken kullanılan karşılığı. İkisinin aynı sonucu vermesi
    /// gerekiyor, o yüzden formül tek yerde ve testli.
    public static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Count == 0 || a.Count != b.Count)
        {
            // Farklı boyutlu vektörler karşılaştırılamaz. Sıfır dönmek
            // "hiç benzemiyor" demek olurdu ve bu YANLIŞ bir güvence;
            // -1 "karşılaştırılamadı" anlamında ve eşiğin altında.
            return -1.0;
        }

        double dot = 0, normA = 0, normB = 0;

        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        if (normA == 0 || normB == 0)
        {
            return -1.0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
