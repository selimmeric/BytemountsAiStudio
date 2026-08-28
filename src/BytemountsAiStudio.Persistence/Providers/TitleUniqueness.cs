using System.Globalization;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Providers;

/// Başlık üzerinden tekillik kontrolü (ADR-003'ün anahtarsız yolu).
///
/// NE YAPIYOR: yayınlanmış konuların başlıklarıyla karşılaştırıyor —
/// normalize edilmiş tam eşleşme ve kelime örtüşmesi.
///
/// NE YAPMIYOR: anlam karşılaştırması. "Göbeklitepe neden önemli" ile
/// "Dünyanın en eski tapınağı" aynı videodur ama bu kontrol ikisini
/// farklı görür. Gerçek çözüm gömme vektörü (pgvector zaten kurulu,
/// `TopicPool.SimilarPublishedAsync` hazır) ve o da bir gömme modeli
/// istiyor.
///
/// PEKİ NEDEN VAR: alternatifi hiç ölçmemekti. Ölçülmeyen bir kontrol
/// QC'de bloklayıcı olarak düşüyor ve HİÇBİR video otomatik geçemiyor —
/// yani Faz 2'nin kabul kriteri tek bir eksik yüzünden hiç
/// sağlanamıyordu. Zayıf ama gerçek bir ölçüm, ölçüm yokluğundan iyi;
/// yeter ki ne kadar zayıf olduğu KAYITLI olsun. `Method` alanı tam
/// bunun için var.
public sealed class TitleUniqueness(StudioDbContext db) : ITopicUniqueness
{
    /// Kelime örtüşmesi bu oranın üstündeyse "aynı konu" sayılıyor.
    ///
    /// 0,8: "Göbeklitepe neden önemli" ile "Göbeklitepe neden çok
    /// önemli" aynı konu (0,75+); "Göbeklitepe kazıları" farklı bir
    /// açı ve geçmeli. Eşiği düşürmek meşru açı farklarını
    /// engellerdi — bir konunun iki farklı yönü iki ayrı video olabilir.
    public const double OverlapThreshold = 0.8;

    public const string MethodName = "baslik-ortusme";

    public async Task<Result<UniquenessVerdict>> CheckAsync(
        Guid? channelId,
        string language,
        string title,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var published = await db.Topics.AsNoTracking()
            .Where(t => t.State == TopicState.Published
                        && t.Language == language
                        && (channelId == null || t.ChannelId == channelId))
            .Select(t => t.Title)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (published.Count == 0)
        {
            // HİÇ YAYIN YOKSA KONU GERÇEKTEN TEKİL. Bu bir varsayım
            // değil, bir ölçüm: karşılaştırılacak bir şey olmadığı
            // için çakışma da olamaz.
            return Result.Success(new UniquenessVerdict
            {
                IsUnique = true,
                Similarity = 0,
                Method = MethodName,
            });
        }

        var candidate = Tokenize(title);

        string? closestTitle = null;
        double closest = 0;

        foreach (var other in published)
        {
            var overlap = Overlap(candidate, Tokenize(other));

            if (overlap > closest)
            {
                closest = overlap;
                closestTitle = other;
            }
        }

        return Result.Success(new UniquenessVerdict
        {
            IsUnique = closest < OverlapThreshold,
            Similarity = Math.Round(closest, 3),
            ConflictingTitle = closest >= OverlapThreshold ? closestTitle : null,
            Method = MethodName,
        });
    }

    /// Başlığı karşılaştırılabilir kelimelere ayırır.
    ///
    /// Küçük harfe indiriliyor ve noktalama atılıyor: "Göbeklitepe:
    /// neden önemli?" ile "göbeklitepe neden önemli" aynı başlık.
    /// Türkçe için `InvariantCulture` DEĞİL, çünkü `I`/`ı` dönüşümü
    /// kültüre bağlı ve yanlışı "İSTANBUL" ile "istanbul"u farklı
    /// göstermek olurdu.
    internal static HashSet<string> Tokenize(string title)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();

        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLower(ch, CultureInfo.GetCultureInfo("tr-TR")));
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// Jaccard örtüşmesi: ortak kelimeler / toplam farklı kelimeler.
    ///
    /// Sadece "kaç ortak kelime var" saymak uzun başlıkları haksız
    /// biçimde benzer gösterirdi — on kelimelik iki başlığın üç ortak
    /// kelimesi olması normal.
    internal static double Overlap(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var shared = left.Count(right.Contains);
        var total = left.Count + right.Count - shared;

        return total == 0 ? 0 : (double)shared / total;
    }
}
