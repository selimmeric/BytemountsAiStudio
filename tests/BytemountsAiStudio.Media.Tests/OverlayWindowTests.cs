using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Media.Ir;
using BytemountsAiStudio.Media.Planning;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Tests;

/// Altyazı katmanları yalnızca kendi penceresi kadar üretiliyor
/// (P4-09).
///
/// GERÇEK BİR ÖLÇÜMDEN ÇIKTI. Her katman VİDEONUN TAMAMI boyunca
/// döngüye alınıyor ve ne zaman görüneceğini yalnızca `enable`
/// belirliyordu. 48 saniyelik bir videoda 97 altyazı için
/// 97 × 1.440 = 140.000 kare üretiliyordu — her biri bir saniyeden
/// kısa görünen katmanlar için.
///
/// Ölçüldü:
///   önce : render 280 sn, ffmpeg zirve belleği 31,5 GB
///   sonra: render  44 sn, zirve bellek 23,6 GB
///
/// Üç render aynı makinede koşunca 64 GB RAM tükeniyor ve sistem
/// takasa giriyordu — yirmi üç dakikada tek bir video bitmedi.
///
/// Eski yorum "girdiyi kendi aralığına kırpmak overlay'in zaman
/// eksenini kaydırırdı" diyordu ve DOĞRUYDU; eksik olan şey
/// `-itsoffset` idi.
public sealed class OverlayWindowTests
{
    private static Dictionary<string, string> Paths(TimelineDocument t)
        => t.Scenes.Select(s => s.Visual.Asset.Sha256)
            .Concat(t.Audio.VoiceSegments.Select(s => s.Asset.Sha256))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(sha => sha, sha => $"/tmp/{sha[..8]}.bin", StringComparer.Ordinal);

    private static RenderPlanner.Result PlanWith(params RenderPlanner.TimedLayer[] overlays)
    {
        var timeline = TimelineFactory.Valid();

        return RenderPlanner.Plan(timeline, Paths(timeline), overlays);
    }

    /// KATMAN GİRDİSİ KENDİ PENCERESİ KADAR, VİDEO KADAR DEĞİL.
    ///
    /// Testin ölçtüğü şey bir süre değil bir ORAN: katmanın süresi
    /// videonun süresine eşitse, düzeltme geri alınmış demektir.
    [Fact]
    public void KatmanGirdisi_KendiPenceresiKadar()
    {
        var plan = PlanWith(new RenderPlanner.TimedLayer(
            "/tmp/altyazi.png", new TimeRange(new Ms(4_000), new Ms(4_500))));

        Assert.True(plan.IsSuccess, string.Join(" | ", plan.Issues));

        var layer = plan.Plan!.Graph.Inputs.Single(i => i.Path == "/tmp/altyazi.png");

        // 500 ms'lik bir altyazı, 12 saniyelik videonun tamamı kadar
        // değil.
        Assert.NotNull(layer.DurationSeconds);
        Assert.InRange(layer.DurationSeconds!.Value, 0.4, 0.6);
    }

    /// VE DOĞRU ANA KAYDIRILIYOR.
    ///
    /// Kırpma tek başına yapılsaydı bütün altyazılar videonun
    /// başında görünürdü: girdiyi kırpmak zaman eksenini kaydırıyor.
    /// `-itsoffset` bunu geri alıyor.
    [Fact]
    public void KatmanGirdisi_DogruAnaKaydiriliyor()
    {
        var plan = PlanWith(new RenderPlanner.TimedLayer(
            "/tmp/altyazi.png", new TimeRange(new Ms(7_250), new Ms(7_800))));

        var layer = plan.Plan!.Graph.Inputs.Single(i => i.Path == "/tmp/altyazi.png");

        Assert.NotNull(layer.OffsetSeconds);
        Assert.Equal(7.25, layer.OffsetSeconds!.Value, 3);
    }

    /// KAYDIRMA FFMPEG KOMUTUNA ULAŞIYOR.
    ///
    /// Bu depoda tekrar eden hata sınıfı: bir alan modele yazılıyor ve
    /// hiçbir şey okumuyor. `OffsetSeconds` grafikte dursa da
    /// `-itsoffset` argümanına dönüşmezse hiçbir kare değişmez.
    [Fact]
    public void Kaydirma_KomutaUlasiyor()
    {
        var plan = PlanWith(new RenderPlanner.TimedLayer(
            "/tmp/altyazi.png", new TimeRange(new Ms(3_000), new Ms(3_400))));

        var command = FilterGraphEmitter.Emit(
            plan.Plan!.Graph, "/tmp/filtre.txt", "/tmp/cikti.mp4", plan.Plan.Output);

        var args = command.Arguments.ToList();
        var index = args.IndexOf("-itsoffset");

        Assert.True(index >= 0, "Kaydırma komuta hiç girmemiş.");
        Assert.Equal("3", args[index + 1]);

        // VE `-i`'DEN ÖNCE: sonra gelseydi ffmpeg onu bir sonraki
        // girdiye uygular ve altyazı yanlış anda görünürdü.
        Assert.True(args.IndexOf("-i") > index || args.LastIndexOf("-i") > index);
    }

    /// SIFIR ANINDA BAŞLAYAN KATMANDA KAYDIRMA YAZILMIYOR.
    ///
    /// `-itsoffset 0` zararsız ama gereksiz; komutu okunmaz yapan
    /// her fazladan argüman, gerçek bir sorunu aramayı zorlaştırıyor.
    [Fact]
    public void SifirdanBaslayan_KaydirmaYazmiyor()
    {
        var plan = PlanWith(new RenderPlanner.TimedLayer(
            "/tmp/ilk.png", new TimeRange(Ms.Zero, new Ms(600))));

        var command = FilterGraphEmitter.Emit(
            plan.Plan!.Graph, "/tmp/filtre.txt", "/tmp/cikti.mp4", plan.Plan.Output);

        Assert.DoesNotContain("-itsoffset", command.Arguments);
    }

    /// ÇOK KISA KATMAN EN AZ BİR KARE SÜRÜYOR.
    ///
    /// Sıfır süreli bir girdi ffmpeg'e hiç kare üretmiyor ve altyazı
    /// sessizce kayboluyor. Hizalamadan gelen bir ipucu birkaç
    /// milisaniye olabiliyor.
    [Fact]
    public void CokKisaKatman_EnAzBirKare()
    {
        var plan = PlanWith(new RenderPlanner.TimedLayer(
            "/tmp/kisa.png", new TimeRange(new Ms(2_000), new Ms(2_001))));

        var layer = plan.Plan!.Graph.Inputs.Single(i => i.Path == "/tmp/kisa.png");

        // 30 fps'te bir kare = 33,3 ms.
        Assert.True(layer.DurationSeconds >= 1.0 / 31,
            $"Katman bir kareden kısa: {layer.DurationSeconds} sn");
    }

    /// ÇOK KATMANDA TOPLAM ÜRETİLEN KARE, VİDEO UZUNLUĞUYLA ORANTILI
    /// KALIYOR.
    ///
    /// ASIL İDDİA BU. Eskiden toplam kare sayısı
    /// `katman_sayısı × video_süresi` ile büyüyordu; şimdi
    /// `katmanların toplam süresi` kadar. Yirmi katmanlı bir videoda
    /// fark yirmi kat.
    [Fact]
    public void CokKatman_ToplamKareOrantiliKaliyor()
    {
        var overlays = Enumerable.Range(0, 20)
            .Select(i => new RenderPlanner.TimedLayer(
                $"/tmp/katman-{i}.png",
                new TimeRange(new Ms(i * 500), new Ms((i * 500) + 400))))
            .ToArray();

        var plan = PlanWith(overlays);
        var timeline = TimelineFactory.Valid();

        var layerSeconds = plan.Plan!.Graph.Inputs
            // Önek AYIRT EDİCİ olmak zorunda: ilk yazımda "/tmp/a"
            // yazmıştım ve sahne görselinin yolu da (`/tmp/aaaa….bin`)
            // eşleşti — test, ölçmediği bir girdiyi toplama katıyordu.
            .Where(i => i.Path.StartsWith("/tmp/katman-", StringComparison.Ordinal))
            .Sum(i => i.DurationSeconds ?? 0);

        // Yirmi katman × 0,4 sn = 8 sn; videonun tamamı 12 sn.
        // Eski davranışta bu 20 × 12 = 240 sn olurdu.
        Assert.InRange(layerSeconds, 7.0, 9.0);
        Assert.True(layerSeconds < timeline.Duration.TotalSeconds * overlays.Length / 4,
            "Katman süreleri hâlâ video uzunluğuyla çarpılıyor.");
    }
}
