using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Providers.Llm;

namespace BytemountsAiStudio.Providers.Llm.Tests;

/// Model barındırmanın yapılandırılabilirliği.
///
/// Asıl sınanan şey şu: aynı ikili, güçlü makinede yerel modeli,
/// zayıf makinede ağdaki bir Ollama'yı, model hiç yoksa dışarıdaki bir
/// servisi kullanabiliyor — ve üçü arasında geçiş KOD DEĞİŞİKLİĞİ
/// GEREKTİRMİYOR.
///
/// Testler süreç ortamına dokunmuyor: okuma işlevi enjekte ediliyor.
public sealed class OllamaOptionsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void OrtamBos_VarsayilanYerelAdres()
    {
        var options = OllamaOptions.From(Env());

        Assert.Equal(new Uri("http://localhost:11434"), options.BaseAddress);
    }

    /// Zayıf makine (2 GB ekran kartı) hiç model koşturamıyor; güçlü
    /// makinedeki Ollama'yı gösteriyor.
    [Fact]
    public void UzakAdres_Ayarlanabilir()
    {
        var options = OllamaOptions.From(Env(("BMAI_OLLAMA_URL", "http://192.168.1.40:11434")));

        Assert.Equal(new Uri("http://192.168.1.40:11434"), options.BaseAddress);
    }

    /// Bozuk bir adres yüzünden süreç HİÇ başlamamaktansa yerel adrese
    /// düşmesi yeğ: yanlış yazılmış bir ortam değişkeni, çalışan bir
    /// makineyi tamamen durdurmamalı. Yerel Ollama yoksa hata zaten
    /// ilk çağrıda ve okunur biçimde geliyor.
    [Fact]
    public void BozukAdres_VarsayilanaDuser()
    {
        var options = OllamaOptions.From(Env(("BMAI_OLLAMA_URL", "bu bir adres degil")));

        Assert.Equal(new Uri("http://localhost:11434"), options.BaseAddress);
    }

    /// Varsayılan üç katman da 7B: 8 GB ekran kartına sığan en iyi
    /// seçenek bu (bkz. docs/DONANIM-VE-MODEL.md).
    [Fact]
    public void VarsayilanKatmanlar_SekizGigabaytaSigar()
    {
        var options = OllamaOptions.From(Env());

        Assert.Equal("qwen2.5:7b-instruct", options.Models[ModelTier.Cheap]);
        Assert.Equal("qwen2.5:7b-instruct", options.Models[ModelTier.Standard]);
        Assert.Equal("qwen2.5:7b-instruct", options.Models[ModelTier.Strong]);
    }

    /// Katman başına ezilebiliyor: 24 GB bir makinede Strong katmanı
    /// 14B olabilir, diğerleri 7B kalır.
    [Fact]
    public void TekKatman_Ezilince_DigerleriDegismez()
    {
        var options = OllamaOptions.From(Env(("BMAI_OLLAMA_MODEL_STRONG", "qwen2.5:14b-instruct")));

        Assert.Equal("qwen2.5:14b-instruct", options.Models[ModelTier.Strong]);
        Assert.Equal("qwen2.5:7b-instruct", options.Models[ModelTier.Cheap]);
    }

    /// Gömme modeli ÇOK DİLLİ ve 768 boyutlu olmak zorunda (§20.5,
    /// ADR-003). Varsayılanın değişmesi şema göçü demek.
    [Fact]
    public void VarsayilanGommeModeli_CokDilli()
    {
        Assert.Equal("paraphrase-multilingual", OllamaOptions.From(Env()).EmbeddingModel);
    }

    [Fact]
    public void GommeModeli_Ezilebilir()
    {
        var options = OllamaOptions.From(Env(("BMAI_OLLAMA_EMBEDDING", "nomic-embed-text")));

        Assert.Equal("nomic-embed-text", options.EmbeddingModel);
    }

    /// Boş bir değişken TANIMSIZ sayılıyor: bir kabuk betiğinde
    /// `BMAI_OLLAMA_MODEL_STRONG=` yazmak, modeli boş isimle çağırıp
    /// anlaşılmaz bir 404 almak demekti.
    [Fact]
    public void BosDeger_YoksayILir()
    {
        var options = OllamaOptions.From(Env(
            ("BMAI_OLLAMA_URL", ""),
            ("BMAI_OLLAMA_MODEL_STRONG", ""),
            ("BMAI_OLLAMA_EMBEDDING", "")));

        Assert.Equal(new Uri("http://localhost:11434"), options.BaseAddress);
        Assert.Equal("qwen2.5:7b-instruct", options.Models[ModelTier.Strong]);
        Assert.Equal("paraphrase-multilingual", options.EmbeddingModel);
    }

    /// Dışarıdan servis alındığında değişen tek şey adres: OpenAI
    /// uyumlu bir ağ geçidi ya da başka bir Ollama sunucusu.
    [Fact]
    public void DisServis_AdresVeModelBirlikte()
    {
        var options = OllamaOptions.From(Env(
            ("BMAI_OLLAMA_URL", "https://ollama.example.com"),
            ("BMAI_OLLAMA_MODEL_STANDARD", "llama3.1:70b")));

        Assert.Equal(new Uri("https://ollama.example.com"), options.BaseAddress);
        Assert.Equal("llama3.1:70b", options.Models[ModelTier.Standard]);
    }

    /// ***ZAMAN AŞIMI DA OKUNUYOR.***
    ///
    /// Adres ve model adları okunuyordu, süre okunmuyordu — ve ikisi
    /// birbirine bağlı: bu dosyanın sınadığı `BMAI_OLLAMA_MODEL_STRONG`
    /// ile 14B model açılabiliyor, 14B model ilk çağrıda beş dakikadan
    /// uzun sürede yükleniyor ve her istek zaman aşımına uğruyordu.
    /// Kullanıcı modeli ayarlayabiliyor ama o modelin çalışabilmesi
    /// için gereken süreyi ayarlayamıyordu.
    [Fact]
    public void ZamanAsimi_OrtamdanOkunuyor()
    {
        var options = OllamaOptions.From(name =>
            name == "BMAI_OLLAMA_TIMEOUT" ? "900" : null);

        Assert.Equal(TimeSpan.FromMinutes(15), options.Timeout);
    }

    /// SIFIR VE NEGATIF REDDEDILIYOR.
    ///
    /// Sıfır saniyelik zaman aşımı her isteği anında iptal ederdi ve
    /// bunu bir yazım hatasıyla elde etmek mümkün olurdu.
    [Theory]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("bes dakika")]
    public void GecersizZamanAsimi_VarsayilanKaliyor(string raw)
    {
        var options = OllamaOptions.From(name =>
            name == "BMAI_OLLAMA_TIMEOUT" ? raw : null);

        Assert.Equal(TimeSpan.FromMinutes(5), options.Timeout);
    }

    /* ---- CPU kipi ---- */

    /// ***`BMAI_OLLAMA_CPU` MODELİ KARTA HİÇ YÜKLEMİYOR.***
    ///
    /// Bu makinede ekran kartı, model yüklenirken sistemi düşürüyor —
    /// yani yerel modelin sorunu modelin kendisi değil, GPU yolu.
    /// `num_gpu: 0` katmanların tamamını CPU'da tutuyor: kart hiç
    /// açılmıyor. Bedeli hız ve bu kabul edilebilir bir takas —
    /// alternatif, senaryo üretiminin tamamen durması.
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("ON")]
    public void CpuKipi_Aciliyor(string raw)
        => Assert.True(OllamaOptions.From(n => n == "BMAI_OLLAMA_CPU" ? raw : null).CpuOnly);

    /// ***VARSAYILAN KAPALI.***
    ///
    /// Çalışan bir kartı olan makinede modeli CPU'ya hapsetmek, on kat
    /// yavaşlatmak demek. Bu bir DONANIM yapılandırması ve donanımı
    /// bilen taraf onu açmalı.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("hayir")]
    public void VarsayilanVeAnlamsizDeger_Kapali(string? raw)
        => Assert.False(OllamaOptions.From(n => n == "BMAI_OLLAMA_CPU" ? raw : null).CpuOnly);
}
