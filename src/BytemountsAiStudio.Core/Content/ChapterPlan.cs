using System.Globalization;
using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Core.Content;

/// Uzun videonun tek bir bölümü (P3-02).
public sealed record Chapter
{
    /// Sıra numarası, sıfırdan.
    public required int Index { get; init; }

    public required string Title { get; init; }

    /// Bu bölümün hedef süresi.
    ///
    /// HEDEF, TAAHHÜT DEĞİL: senaryo yazıldıktan sonra gerçek süre
    /// ölçülüyor (ADR-006). Bu sayı yalnızca senaryo yazarına "bu
    /// bölüm ne kadar olmalı" diyor.
    public required Ms TargetDuration { get; init; }

    /// Bölümün başladığı an — chapter işaretleri (P3-04) buradan
    /// üretiliyor.
    public required Ms Start { get; init; }

    /// Bu bölümün cevaplaması gereken soru.
    ///
    /// BAŞLIK YETMİYOR: "Göbeklitepe'nin keşfi" bir başlık, "kim
    /// buldu ve neden yıllarca önemsenmedi" bir soru. Soru olmadan
    /// model başlığı tekrar eden bir paragraf yazıyor.
    public string? Question { get; init; }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Index + 1}. {Title} ({TargetDuration.TotalSeconds:0} sn)");
}

/// Uzun video bölüm planı (P3-02).
public sealed record ChapterPlanResult(IReadOnlyList<Chapter> Chapters, Ms TotalDuration)
{
    public int Count => Chapters.Count;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"{Count} bölüm, {TotalDuration.TotalSeconds / 60:0.#} dk");
}

/// Uzun video için bölüm planı (P3-02, §34).
///
/// SAF: model yok, veritabanı yok. "Kaç bölüm olmalı ve her biri ne
/// kadar sürmeli" kararı, on beş dakikalık bir video üretilerek
/// öğrenilecek bir şey olmamalı — o video kırk dakikalık render ve
/// gerçek para demek.
///
/// KISA VİDEODAN FARKI YAPISAL, ölçek değil. Bir Short tek bir fikri
/// anlatıyor; sekiz dakikalık bir video BÖLÜMLERE ayrılmak zorunda,
/// yoksa izleyici nerede olduğunu kaybediyor ve bırakıyor. Aynı
/// senaryo üreticisini "daha uzun yaz" diye çağırmak, yapı olmadan
/// uzunluk üretmek olurdu.
public static class ChapterPlanner
{
    /// Uzun video alt ve üst sınırı.
    ///
    /// SEKİZ–ON BEŞ DAKİKA (§34): sekizin altı "uzun video" sayılmıyor
    /// ve reklam yerleşimi için de yetmiyor; on beşin üstünde
    /// tamamlanma oranı belirgin düşüyor ve bir bölümü daha eklemek
    /// izlenme süresini artırmıyor, azaltıyor.
    public static readonly Ms MinimumDuration = new(8 * 60 * 1000);

    public static readonly Ms MaximumDuration = new(15 * 60 * 1000);

    /// Bir bölümün en kısa hâli.
    ///
    /// DOKSAN SANİYE: daha kısası bir bölüm değil, bir paragraf.
    /// Chapter işareti koymak izleyiciye "burada ayrı bir konu var"
    /// diyor ve doksan saniyeden kısa bir parçada bu söz yalan
    /// oluyor.
    public static readonly Ms MinimumChapter = new(90 * 1000);

    /// Bir bölümün en uzun hâli.
    ///
    /// ÜÇ DAKİKA: daha uzun bir bölüm kendi içinde bölünmeli. Bölüm
    /// listesinin işlevi videoda gezinmek ve üç dakikalık bir sıçrama
    /// gezinmeyi işe yaramaz kılıyor.
    public static readonly Ms MaximumChapter = new(3 * 60 * 1000);

    /// Giriş ve kapanışın payı.
    ///
    /// Giriş %6, kapanış %4: giriş izleyiciyi tutmak zorunda ve o iş
    /// kapanıştan uzun sürüyor. Sabit saniye vermek yerine oran
    /// kullanmak, sekiz dakikalık ve on beş dakikalık videoda aynı
    /// dengeyi koruyor.
    public const double IntroShare = 0.06;

    public const double OutroShare = 0.04;

    /// Bölüm planı üretir.
    ///
    /// `sections` modelden geliyor: her biri bir başlık ve bir soru.
    /// Planlayıcı içeriği ÜRETMİYOR, yalnızca zamanı paylaştırıyor —
    /// içerik üretimi modelin işi, zaman aritmetiği kodun.
    public static Result<ChapterPlanResult> Plan(
        IReadOnlyList<(string Title, string? Question)> sections, Ms targetDuration)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (sections.Count == 0)
        {
            return Errors.Error.Permanent("chapter.no_sections",
                "Bölüm başlığı yok; plan üretilemez.");
        }

        var total = Clamp(targetDuration);

        // ---- BÖLÜM SAYISI SÜREYE YETMİYORSA VİDEO KISALIYOR ----
        //
        // Bunu ilk yazdığımda düşünmemiştim ve testler yakaladı: model
        // iki bölüm önerdiğinde on beş dakikayı doldurmak için üç
        // bölüm daha UYDURMAYA çalışıyordum. Planlayıcının işi zamanı
        // paylaştırmak, içerik icat etmek değil.
        //
        // Doğru davranış: elimizdeki bölümlerin taşıyabileceği kadar
        // uzun bir video yapmak. TOPLAM SÜREDEN geri hesaplanıyor ki
        // giriş/kapanış payları bozulmasın — ilk denememde gövdeyi
        // kısaltıp toplamı eski paylarla kurmuştum ve plan kendi
        // içinde tutarsız çıkmıştı (test bunu da yakaladı).
        var bodyShare = 1.0 - IntroShare - OutroShare;
        var capacity = (long)sections.Count * MaximumChapter.Value;

        if (capacity < total.Value * bodyShare)
        {
            var shrunk = (int)(capacity / bodyShare);

            if (shrunk < MinimumDuration.Value)
            {
                // KISALTMAK DA YETMİYORSA HATA.
                //
                // Sekiz dakikanın altı "uzun video" sayılmıyor. Bölüm
                // sayısını artırmak modelin işi ve ona bunu söylemek,
                // sessizce yedi dakikalık bir video üretmekten iyi.
                var carried = shrunk / 60000.0;
                var required = MinimumDuration.Value / 60000.0;

                return Errors.Error.Permanent("chapter.too_few_sections",
                    FormattableString.Invariant(
                        $"{sections.Count} bölüm en fazla {carried:0.#} dk taşıyor; uzun video için en az {required:0} dk gerekiyor. Daha fazla bölüm gerekiyor."));
            }

            total = new Ms(shrunk);
        }

        var intro = new Ms((int)(total.Value * IntroShare));
        var outro = new Ms((int)(total.Value * OutroShare));
        var body = total.Value - intro.Value - outro.Value;

        // KAÇ BÖLÜM SIĞAR: gövde / en kısa bölüm.
        //
        // Model sekiz bölüm önerebiliyor ama sekiz dakikalık bir
        // videoda gövde ~7,2 dakika: sekiz bölüm her birine 54 saniye
        // düşürüyor ve bu bir bölüm değil, bir paragraf.
        //
        // Fazla başlıklar KIRPILIYOR ve kırpıldığı `Dropped` ile
        // sayılıyor — sessizce almak, modelin planının uygulandığı
        // izlenimi verirdi.
        var count = Math.Min(sections.Count, Math.Max(body / MinimumChapter.Value, 1));

        var each = body / count;
        var remainder = body % count;
        var cursor = intro.Value;

        var chapters = new List<Chapter>();

        for (var i = 0; i < count; i++)
        {
            // ARTAN MİLİSANİYELER İLK BÖLÜMLERE DAĞITILIYOR.
            //
            // Hepsini sona atmak son bölümü belirgin uzatırdı; hiç
            // dağıtmamak toplamın hedeften sapması demekti ve o sapma
            // chapter işaretlerini videonun sonunda kaydırırdı.
            var duration = each + (i < remainder ? 1 : 0);

            chapters.Add(new Chapter
            {
                Index = i,
                Title = sections[i].Title,
                Question = sections[i].Question,
                Start = new Ms(cursor),
                TargetDuration = new Ms(duration),
            });

            cursor += duration;
        }

        return Result.Success(new ChapterPlanResult(chapters, total));
    }

    /// Hedef süreyi geçerli aralığa çeker.
    ///
    /// KIRPILIYOR, REDDEDİLMİYOR: yapılandırmada 30 dakika yazan biri
    /// hiç video alamamak yerine 15 dakikalık bir video almalı. Sınırın
    /// dışına çıkmak bir hata değil, bir tercih hatası.
    public static Ms Clamp(Ms target)
        => new(Math.Clamp(target.Value, MinimumDuration.Value, MaximumDuration.Value));

    /// Kaç bölüm istendi, kaçı plana girdi.
    ///
    /// Fark sıfır değilse çağıran bunu KAYDA GEÇİRMELİ: modelin
    /// planının aynen uygulandığını sanmak, videonun neden beklenenden
    /// farklı çıktığını açıklayamamak demek.
    public static int Dropped(IReadOnlyList<(string Title, string? Question)> sections, ChapterPlanResult plan)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(plan);

        return Math.Max(sections.Count - plan.Count, 0);
    }
}
