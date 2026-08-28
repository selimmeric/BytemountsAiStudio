using BytemountsAiStudio.Nodes;
using BytemountsAiStudio.Persistence;
using BytemountsAiStudio.Quality;
using BytemountsAiStudio.Workflow.Definition;

namespace BytemountsAiStudio.Nodes.Tests;

/// Depodaki GERÇEK tohum grafını sınar.
///
/// Doğrulama testi bunu iddia ediyordu ama grafın kendisini değil,
/// test dosyasına elle yazılmış üç node'luk bir kopyasını
/// doğruluyordu: gerçek graf bozulsa test yine geçerdi. Şimdi
/// `DatabaseSeeder.FakeGraphJson` sabitinin kendisi okunuyor —
/// zaten `public` olmasının sebebi buydu.
public sealed class SeedGraphTests
{
    private static WorkflowGraph Seed()
    {
        var graph = WorkflowGraph.Parse(DatabaseSeeder.FakeGraphJson);

        Assert.NotNull(graph);

        return graph;
    }

    [Fact]
    public void TohumGrafi_DogrulamadanGeciyor()
        => Assert.Empty(WorkflowValidator.Validate(Seed(), NodeHandlerRegistration.KnownNodeTypes));

    /// KALİTE DÖNGÜSÜ GRAFTA OLMALI.
    ///
    /// QC ve onay kapısı yazılmış, kayıtlı ve testliydi — ama hiçbir
    /// grafta yoktu. Yani gerçek bir koşuda QC hiç çalışmıyor, skor
    /// hiç üretilmiyor, seçici onay hiç devreye girmiyor ve hedefli
    /// retry hiç tetiklenmiyordu. Faz 2'nin tamamı yazılıp
    /// erişilemez durumdaydı.
    [Fact]
    public void TohumGrafi_KaliteDongusunuIceriyor()
    {
        var types = Seed().Nodes.Select(n => n.Type).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("qc.mechanical", types);
        Assert.Contains("qc.semantic", types);
        Assert.Contains("human.approval", types);
    }

    /// ONAY EN SONDA VE HER İKİ QC'DEN SONRA.
    ///
    /// Kendisinden sonra hiçbir node yoksa "onaylandı" kararının
    /// ardından koşacak bir şey de yok demektir. Onayı ortada
    /// bırakmak, insanı henüz üretilmemiş bir videoya baktırmak
    /// olurdu.
    ///
    /// İnsana giden skorun HER İKİ QC'yi de içermesi gerekiyor:
    /// yalnızca mekanik QC'den sonra sorulsaydı, semantik kontrollerin
    /// sonucu karara hiç girmezdi.
    [Fact]
    public void OnayKapisi_HerIkiQcdenSonra()
    {
        var graph = Seed();
        var approval = graph.Nodes.Single(n => n.Type == "human.approval");

        // Onaydan SONRA hiçbir şey yok.
        Assert.DoesNotContain(graph.Edges, e => e.From == approval.Id);

        // Onaya giden zincir geriye doğru izlendiğinde iki QC de
        // görülüyor.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(graph.Predecessors(approval.Id));

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();

            if (!seen.Add(id))
            {
                continue;
            }

            foreach (var predecessor in graph.Predecessors(id))
            {
                queue.Enqueue(predecessor);
            }
        }

        var typesBefore = seen.Select(id => graph.Node(id)!.Type).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("qc.mechanical", typesBefore);
        Assert.Contains("qc.semantic", typesBefore);
        Assert.Contains("media.render", typesBefore);
    }

    /// NODE KİMLİKLERİ BİR ARAYÜZ.
    ///
    /// Run bağlamı node KİMLİĞİNE göre anahtarlanıyor ve tüketiciler
    /// sabit anahtarlar okuyor: `TimelineBuilder` → `music`,
    /// `QualityCheckHandler` → `thumbnail`/`render`/`seo`,
    /// `MediaRenderHandler` → `timeline`.
    ///
    /// GERÇEKTEN OLDU: node'lar `muzik` ve `kapak` adlandırılmıştı.
    /// İkisi de doğru çalışıp doğru çıktı üretti ve hiçbiri okunmadı —
    /// müzik hiçbir videoya girmedi (QC de sessiz kaldı, çünkü
    /// "müziksiz video geçerli") ve kapak bloklayıcı bir kontrolü
    /// "ölçülmedi" diye düşürdü. Yani iki node aylarca çalışıyor
    /// görünüp hiçbir işe yaramayabilirdi.
    [Theory]
    [InlineData("topic")]
    [InlineData("research")]
    [InlineData("script")]
    [InlineData("claims")]
    [InlineData("tts")]
    [InlineData("visuals")]
    [InlineData("music")]
    [InlineData("timeline")]
    [InlineData("render")]
    [InlineData("seo")]
    [InlineData("thumbnail")]
    [InlineData("qc")]
    public void TuketicilerinOkudugu_NodeKimlikleriGraftaVar(string contextKey)
        => Assert.Contains(Seed().Nodes, n => n.Id == contextKey);

    /* ---- Uzun video grafı (P3-02) ---- */

    private static WorkflowGraph LongSeed()
    {
        var graph = WorkflowGraph.Parse(DatabaseSeeder.LongGraphJson);

        Assert.NotNull(graph);

        return graph;
    }

    [Fact]
    public void UzunVideoGrafi_DogrulamadanGeciyor()
        => Assert.Empty(WorkflowValidator.Validate(LongSeed(), NodeHandlerRegistration.KnownNodeTypes));

    /// KISA VİDEODAN FARKI İKİ NODE.
    ///
    /// Yeni bir içerik türü yeni bir boru hattı değil, farklı bir GRAF
    /// olmalıydı (§34). Bu test o iddiayı sayıya çeviriyor: uzun video
    /// grafı kısa video grafından yalnızca senaryo adımında ayrılıyor,
    /// gerisi ortak.
    [Fact]
    public void UzunVideoGrafi_YalnizcaSenaryoAdimindaAyriliyor()
    {
        var shortTypes = Seed().Nodes.Select(n => n.Type).ToHashSet(StringComparer.Ordinal);
        var longTypes = LongSeed().Nodes.Select(n => n.Type).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(["chapter.plan", "script.long"], longTypes.Except(shortTypes).Order().ToArray());
        Assert.Equal(["script.generate"], shortTypes.Except(longTypes).ToArray());
    }

    /// UZUN VİDEODA DA node kimlikleri tüketicilerin okuduğu
    /// anahtarlar: aynı tüketiciler, aynı sözleşme.
    [Theory]
    [InlineData("topic")]
    [InlineData("research")]
    [InlineData("script")]
    [InlineData("tts")]
    [InlineData("visuals")]
    [InlineData("music")]
    [InlineData("timeline")]
    [InlineData("render")]
    [InlineData("seo")]
    [InlineData("thumbnail")]
    [InlineData("qc")]
    [InlineData("chapters")]
    public void UzunVideoGrafi_TuketiciAnahtarlariniKullaniyor(string contextKey)
        => Assert.Contains(LongSeed().Nodes, n => n.Id == contextKey);

    /// SENARYO BÖLÜM PLANINDAN SONRA: `script.long` plan olmadan
    /// düşüyor ve sıra yanlışsa bunu her koşuda yapardı.
    [Fact]
    public void UzunVideoGrafi_PlanSenaryodanOnce()
    {
        var graph = LongSeed();
        var script = graph.Nodes.Single(n => n.Type == "script.long");

        Assert.Contains(
            graph.Predecessors(script.Id),
            id => graph.Node(id)!.Type == "chapter.plan");
    }

    /// RETRY HEDEFLERİ GRAFA DENK GELİYOR.
    ///
    /// Planlayıcı boru hattı aşamalarını adlandırıyor
    /// (`media.render`), graf node'lara kendi kimliklerini veriyor
    /// (`render`). Bu test bu iki dünyanın buluştuğunu doğruluyor:
    /// buluşmadığında hedefli retry hiçbir node bulamıyor ve QC'nin
    /// düşürdüğü video düzeltilmeden kalıyor.
    [Theory]
    [InlineData(RetryTarget.Script)]
    [InlineData(RetryTarget.Visuals)]
    [InlineData(RetryTarget.Timeline)]
    [InlineData(RetryTarget.Render)]
    [InlineData(RetryTarget.Metadata)]
    public void HerRetryHedefi_TohumGrafindaKarsiligiVar(RetryTarget target)
    {
        var resolved = Seed().ResolveTargets(RetryPlanner.NodesFrom(target));

        Assert.NotEmpty(resolved);
    }

    /// PLAN HEDEFİ VE SONRASINI LİSTELİYOR; kuyruğa yalnızca GİRİŞ
    /// giriyor.
    ///
    /// Hepsini birden atmak sırayı yok saymak olurdu: yeni görseller
    /// daha üretilmeden timeline derlenir, derlenmemiş timeline
    /// render edilirdi — üstelik her biri kenar takibiyle bir kez
    /// daha kuyruğa girerdi.
    [Fact]
    public void GorselHedefi_YalnizcaGorseliKuyrugaAtiyor()
    {
        var graph = Seed();
        var targets = graph.ResolveTargets(RetryPlanner.NodesFrom(RetryTarget.Visuals));

        // Plan görselden sonraki her şeyi kapsıyor...
        Assert.Contains("visuals", targets);
        Assert.Contains("timeline", targets);
        Assert.Contains("render", targets);

        // ...ama kuyruğa yalnızca görsel giriyor.
        Assert.Equal(["visuals"], graph.EntryPointsOf(targets));
    }

    /// SENARYO YENİLENİYORSA SESLENDİRME DE YENİLENMELİ.
    ///
    /// Bu testi yazarken çıktı: senaryo hedefi seslendirmeyi
    /// kapsamıyordu. Yani senaryo yeniden üretilir, ses eski kalır ve
    /// video ESKİ metni okuyan bir sesle YENİ metnin altyazılarını
    /// taşırdı. Mekanik QC bunu yakalayamaz — her iki parça da tek
    /// başına geçerli.
    ///
    /// Kapsamadığı, `EntryPointsOf` iki giriş döndürünce görüldü:
    /// zincir seslendirmede kopuyordu.
    [Fact]
    public void SenaryoHedefi_SeslendirmeyiDeKapsiyor()
    {
        var graph = Seed();
        var targets = graph.ResolveTargets(RetryPlanner.NodesFrom(RetryTarget.Script));

        Assert.Contains("claims", targets);
        Assert.Contains("tts", targets);

        // Tek giriş: zincir kopuk değil.
        Assert.Equal(["script"], graph.EntryPointsOf(targets));
    }

    /// Hedefli retry'ın ANLAMI: önceki aşamalar korunuyor.
    [Fact]
    public void RenderHedefi_SenaryoyuKapsamiyor()
    {
        var resolved = Seed().ResolveTargets(RetryPlanner.NodesFrom(RetryTarget.Render));

        Assert.Contains("render", resolved);
        Assert.DoesNotContain("script", resolved);
        Assert.DoesNotContain("research", resolved);
    }
}
