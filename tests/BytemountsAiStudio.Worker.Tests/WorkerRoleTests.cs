using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Worker.Tests;

/// Worker rolleri (P4-01).
///
/// Render bir makinenin bütün çekirdeklerini ve gigabaytlarca
/// belleğini yiyor; LLM ve varlık işleri ağ bekliyor ve ucuz bir
/// makinede sekiz tanesi rahatça koşuyor. İkisini aynı süreçte
/// tutmak, ağ bekleyen işleri render'ın bitmesini bekleyen bir
/// makineye hapsetmek demek.
public sealed class WorkerRoleTests : IDisposable
{
    private readonly List<string> _touched = [];

    public void Dispose()
    {
        foreach (var name in _touched)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    private void Set(string name, string? value)
    {
        _touched.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Theory]
    [InlineData(null, WorkerRole.All)]
    [InlineData("", WorkerRole.All)]
    [InlineData("all", WorkerRole.All)]
    [InlineData("hepsi", WorkerRole.All)]
    [InlineData("render", WorkerRole.Render)]
    [InlineData("RENDER", WorkerRole.Render)]
    [InlineData(" light ", WorkerRole.Light)]
    public void Rol_Okunuyor(string? value, WorkerRole expected)
        => Assert.Equal(expected, WorkerRoles.Parse(value).Role);

    /// TANINMAYAN ROL SESSİZ DÜŞMÜYOR.
    ///
    /// Yazım hatası olan bir rol, bütün kuyrukları dinleyen bir render
    /// makinesi demekti — ve bunu ancak o makinenin neden LLM işi
    /// aldığını merak eden biri fark ederdi. Davranış güvenli tarafa
    /// düşüyor (hepsini dinle) ama SESSİZCE değil.
    [Fact]
    public void TaninmayanRol_UyariVeriyor()
    {
        var result = WorkerRoles.Parse("renderr");

        Assert.Equal(WorkerRole.All, result.Role);
        Assert.NotNull(result.Warning);
        Assert.Contains("renderr", result.Warning, StringComparison.Ordinal);
    }

    /// RENDER ROLÜ YALNIZCA RENDER VE YÜKLEME DİNLİYOR.
    ///
    /// Yükleme de burada, çünkü yüklenecek dosya bu makinede duruyor:
    /// ayrı bir makineye almak, gigabaytlarca videoyu iki kez taşımak
    /// demekti.
    [Fact]
    public void RenderRolu_YalnizcaAgirKuyruklar()
    {
        var queues = WorkerRoles.ConcurrencyFor(WorkerRole.Render).Keys.Order().ToList();

        Assert.Equal([QueueClass.Render, QueueClass.Upload], queues.Order());
    }

    /// HAFİF ROL RENDER'A HİÇ DOKUNMUYOR.
    [Fact]
    public void HafifRol_RenderDinlemiyor()
    {
        var queues = WorkerRoles.ConcurrencyFor(WorkerRole.Light).Keys.ToList();

        Assert.DoesNotContain(QueueClass.Render, queues);
        Assert.DoesNotContain(QueueClass.Upload, queues);
        Assert.Contains(QueueClass.Llm, queues);
    }

    /// İKİ ROL BİRLİKTE BÜTÜN KUYRUKLARI KAPSIYOR.
    ///
    /// ASIL İDDİA BU: bir kuyruk ikisinde de yoksa o iş HİÇ
    /// koşmuyor ve run sessizce asılı kalıyor — kuyrukta iş var,
    /// dinleyen yok. Testin ölçtüğü şey bir liste değil, bir
    /// BOŞLUK OLMAMASI.
    [Fact]
    public void RenderVeHafif_BirlikteHepsiniKapsiyor()
    {
        var render = WorkerRoles.ConcurrencyFor(WorkerRole.Render).Keys;
        var light = WorkerRoles.ConcurrencyFor(WorkerRole.Light).Keys;
        var all = WorkerRoles.ConcurrencyFor(WorkerRole.All).Keys;

        Assert.Equal(all.Order(), render.Concat(light).Order());
    }

    /// VE ÇAKIŞMIYORLAR.
    ///
    /// Aynı kuyruğu iki rol de dinleseydi zarar olmazdı (kuyruk
    /// kiralama tabanlı) ama ayrımın anlamı kalmazdı: render
    /// makinesi yine LLM işi alırdı.
    [Fact]
    public void RenderVeHafif_Cakismiyor()
    {
        var render = WorkerRoles.ConcurrencyFor(WorkerRole.Render).Keys.ToHashSet();
        var light = WorkerRoles.ConcurrencyFor(WorkerRole.Light).Keys.ToHashSet();

        Assert.Empty(render.Intersect(light));
    }

    /// EŞZAMANLILIK ORTAM DEĞİŞKENİYLE AYARLANABİLİYOR.
    ///
    /// Makineler aynı değil; sayıları koda gömmek, on altı çekirdekli
    /// bir render makinesinde de tek render koşturmak demekti.
    [Fact]
    public void Eszamanlilik_OrtamdanAyarlanabiliyor()
    {
        Set("BMAI_CONCURRENCY_RENDER", "3");

        Assert.Equal(3, WorkerRoles.ConcurrencyFor(WorkerRole.Render)[QueueClass.Render]);
    }

    /// GEÇERSİZ DEĞER VARSAYILANI BOZMUYOR.
    ///
    /// Sıfır ya da metin yazan biri render'ı KAPATMIŞ olmaz —
    /// dinlenmeyen bir kuyruk, işlerin sessizce birikmesi demek.
    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("cok")]
    [InlineData("")]
    public void GecersizEszamanlilik_VarsayilaniKoruyor(string value)
    {
        Set("BMAI_CONCURRENCY_RENDER", value);

        Assert.Equal(1, WorkerRoles.ConcurrencyFor(WorkerRole.Render)[QueueClass.Render]);
    }
}
