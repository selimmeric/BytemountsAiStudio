using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Providers.Fake;

namespace BytemountsAiStudio.Providers.Fake.Tests;

/// ADR-009'un asıl vaadi: aynı girdi her zaman aynı çıktı.
///
/// Bu bozulursa boru hattı testleri "bazen geçen" testlere döner ve hata
/// ayıklarken neyin değiştiğini anlamak imkânsızlaşır — fake'lerin varlık
/// sebebi de ortadan kalkar.
public sealed class DeterminismTests
{
    private static readonly ProviderContext Ctx = ProviderContext.ForTest();

    [Fact]
    public async Task AramaSonuclari_IkiCagridaAyni()
    {
        var provider = new FakeSearchProvider();
        var query = new SearchQuery { Text = "Dünyanın En Tehlikeli 10 Yeri" };

        var first = await provider.SearchAsync(query, Ctx, CancellationToken.None);
        var second = await provider.SearchAsync(query, Ctx, CancellationToken.None);

        Assert.Equal(
            first.Value.Value.Select(h => h.Url.ToString()),
            second.Value.Value.Select(h => h.Url.ToString()));
    }

    [Fact]
    public async Task FarkliSorgu_FarkliSonuc()
    {
        var provider = new FakeSearchProvider();

        var a = await provider.SearchAsync(new SearchQuery { Text = "uzay" }, Ctx, CancellationToken.None);
        var b = await provider.SearchAsync(new SearchQuery { Text = "tarih" }, Ctx, CancellationToken.None);

        Assert.NotEqual(a.Value.Value[0].Url, b.Value.Value[0].Url);
    }

    [Fact]
    public async Task UretilenGorsel_AyniPromptaBaytBaytAyni()
    {
        var provider = new FakeImageProvider(ImageProviderKind.Generative);
        var prompt = new ImagePrompt { Text = "kayıp şehir", Width = 320, Height = 240 };

        var first = await provider.GenerateAsync(prompt, Ctx, CancellationToken.None);
        var second = await provider.GenerateAsync(prompt, Ctx, CancellationToken.None);

        Assert.Equal(first.Value.Value.Data.ToArray(), second.Value.Value.Data.ToArray());
    }

    [Fact]
    public async Task Embedding_BenzerMetinleriBenzerVektorleEsler()
    {
        // Konu tekilliğinin (ADR-003) sahte karşılığı. Rastgele vektör
        // üretseydik "En Tehlikeli 10 Yer" ile "En Tehlikeli 10 Bölge"
        // asla yakın çıkmaz, tekillik testi anlamsızlaşırdı.
        var llm = new FakeLlmProvider();

        var a = await Embed(llm, "Dünyanın En Tehlikeli 10 Yeri");
        var b = await Embed(llm, "Dünyanın En Tehlikeli 10 Bölgesi");
        var c = await Embed(llm, "En Lezzetli 10 Tatlı Tarifi");

        var similar = Cosine(a, b);
        var different = Cosine(a, c);

        Assert.True(similar > different,
            $"Benzer konular daha yakın olmalıydı: benzer={similar:F3}, farklı={different:F3}");
    }

    private static async Task<IReadOnlyList<float>> Embed(FakeLlmProvider llm, string text)
    {
        var result = await llm.EmbedAsync(text, Ctx, CancellationToken.None);
        return result.Value.Value;
    }

    private static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double dot = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;   // vektörler zaten normalize
    }
}

public sealed class FakeSearchFilterTests
{
    private static readonly ProviderContext Ctx = ProviderContext.ForTest();

    [Fact]
    public async Task IzinliAlanListesi_DigerlerinElerler()
    {
        var provider = new FakeSearchProvider();
        var query = new SearchQuery
        {
            Text = "test",
            AllowedDomains = ["tr.wikipedia.org", "nasa.gov"],
        };

        var result = await provider.SearchAsync(query, Ctx, CancellationToken.None);

        Assert.All(result.Value.Value, hit =>
            Assert.Contains(hit.Url.Host, new[] { "tr.wikipedia.org", "nasa.gov" }));
    }

    [Fact]
    public async Task YasakliAlan_SonuclardaCikmaz()
    {
        var provider = new FakeSearchProvider();
        var query = new SearchQuery { Text = "test", BlockedDomains = ["reddit.com"] };

        var result = await provider.SearchAsync(query, Ctx, CancellationToken.None);

        Assert.DoesNotContain(result.Value.Value, hit => hit.Url.Host == "reddit.com");
    }

    [Fact]
    public async Task JokerDesen_AltAlanlariYakalar()
    {
        var provider = new FakeSearchProvider();
        var query = new SearchQuery { Text = "test", AllowedDomains = ["*.wikipedia.org"] };

        var result = await provider.SearchAsync(query, Ctx, CancellationToken.None);

        Assert.NotEmpty(result.Value.Value);
        Assert.All(result.Value.Value, hit =>
            Assert.EndsWith(".wikipedia.org", hit.Url.Host, StringComparison.Ordinal));
    }
}

public sealed class FakeTtsTests
{
    private static readonly ProviderContext Ctx = ProviderContext.ForTest();
    private static readonly LanguageTag Turkish = LanguageTag.Create("tr-TR");

    private static TtsRequest Request(string text, double speed = 1.0) => new()
    {
        SpeechText = text,
        VoiceId = "fake-tr-f1",
        Language = Turkish,
        Speed = speed,
    };

    [Fact]
    public async Task Sure_MetinUzunluguylaArtar()
    {
        // Sabit süre döndürseydik, timeline'ın "sahne süresini sesten al"
        // davranışı sınanamazdı: her sahne aynı uzunlukta çıkar, kayma görünmezdi.
        var tts = new FakeTtsProvider();

        var kisa = await tts.SynthesizeAsync(Request("Kısa."), Ctx, CancellationToken.None);
        var uzun = await tts.SynthesizeAsync(
            Request("Bu cümle belirgin biçimde daha uzundur ve daha çok sürmelidir."),
            Ctx, CancellationToken.None);

        Assert.True(uzun.Value.Value.ReportedDuration > kisa.Value.Value.ReportedDuration);
    }

    [Fact]
    public async Task HizliKonusma_DahaKisaSurer()
    {
        var tts = new FakeTtsProvider();
        const string text = "Aynı metin farklı hızlarda.";

        var normal = await tts.SynthesizeAsync(Request(text), Ctx, CancellationToken.None);
        var hizli = await tts.SynthesizeAsync(Request(text, 1.3), Ctx, CancellationToken.None);

        Assert.True(hizli.Value.Value.ReportedDuration < normal.Value.Value.ReportedDuration);
    }

    [Fact]
    public async Task KelimeZamanlari_ArtanVeCakismayan()
    {
        var tts = new FakeTtsProvider();
        var result = await tts.SynthesizeAsync(
            Request("Bir iki üç dört beş altı yedi."), Ctx, CancellationToken.None);

        var words = result.Value.Value.WordTimings;

        Assert.NotEmpty(words);
        for (var i = 1; i < words.Count; i++)
        {
            Assert.True(words[i].Start >= words[i - 1].End,
                $"{i}. kelime öncekiyle çakışıyor: {words[i - 1].End} -> {words[i].Start}");
        }
    }

    [Fact]
    public async Task SonKelimeninBitisi_SureyleTamOrtusur()
    {
        // Oransal dağıtımda biriken yuvarlama farkı bırakılırsa altyazı sesin
        // bir tık önünde ya da arkasında kalır; 50 sahnede gözle görülür.
        var tts = new FakeTtsProvider();
        var result = await tts.SynthesizeAsync(
            Request("Yuvarlama farkı burada birikmemeli."), Ctx, CancellationToken.None);

        var response = result.Value.Value;

        Assert.Equal(response.ReportedDuration, response.WordTimings[^1].End);
    }

    [Fact]
    public async Task UretilenSes_GercekVeIstenenSurede()
    {
        var tts = new FakeTtsProvider();
        var result = await tts.SynthesizeAsync(
            Request("Ses dosyası gerçekten çalınabilir olmalı."), Ctx, CancellationToken.None);

        var response = result.Value.Value;
        var wav = response.Audio.Span;

        Assert.True(wav.Length > 44, "WAV başlıktan uzun olmalı.");
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav[..4]));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav.Slice(8, 4)));

        // Başlıktaki veri boyutundan hesaplanan süre, bildirilen süreyle örtüşmeli.
        var byteRate = BitConverter.ToInt32(wav.Slice(28, 4));
        var dataBytes = BitConverter.ToInt32(wav.Slice(40, 4));
        var actual = new Ms((int)((long)dataBytes * 1000 / byteRate));

        Assert.InRange(actual.Value, response.ReportedDuration.Value - 2, response.ReportedDuration.Value + 2);
    }

    [Fact]
    public async Task BosMetin_Reddedilir()
    {
        var tts = new FakeTtsProvider();
        var result = await tts.SynthesizeAsync(Request("   "), Ctx, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("fake.tts.empty", result.Error.Code);
    }

    [Fact]
    public async Task AsrVeTts_AyniZamanlamayiUretir()
    {
        // "TTS timing veriyorsa ASR'ye gitme" kararı çıktıyı değiştirmemeli.
        const string text = "İki yol da aynı sonucu vermeli.";
        var tts = new FakeTtsProvider();
        var asr = new FakeAsrProvider();

        var synthesized = await tts.SynthesizeAsync(Request(text), Ctx, CancellationToken.None);
        var aligned = await asr.AlignAsync(
            new AlignRequest { AudioPath = "yok.wav", Transcript = text, Language = Turkish },
            Ctx, CancellationToken.None);

        Assert.Equal(
            synthesized.Value.Value.WordTimings.Select(w => (w.Text, w.Start, w.End)),
            aligned.Value.Value.Words.Select(w => (w.Text, w.Start, w.End)));
    }
}
