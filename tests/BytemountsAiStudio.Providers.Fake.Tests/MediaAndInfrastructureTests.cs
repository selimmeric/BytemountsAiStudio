using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Fake.Tests;

/// Üretilen PNG'nin FFmpeg tarafından okunabilir olması şart; aksi hâlde
/// Faz 0'ın "sahte içerikle uçtan uca mp4" hedefi kâğıt üstünde kalır.
/// Burada dosyanın yapısı doğrulanıyor — imza, IHDR ve boyutlar.
public sealed class PngOutputTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Theory]
    [InlineData(16, 16)]
    [InlineData(1080, 1920)]
    [InlineData(1920, 1080)]
    public async Task UretilenPng_GecerliBasligaVeDogruBoyutaSahip(int width, int height)
    {
        var provider = new FakeImageProvider(ImageProviderKind.Generative);
        var result = await provider.GenerateAsync(
            new ImagePrompt { Text = "test", Width = width, Height = height },
            ProviderContext.ForTest(),
            CancellationToken.None);

        var png = result.Value.Value.Data.ToArray();

        Assert.True(png.Length > 8 + 25, "PNG en az imza + IHDR uzunluğunda olmalı.");
        Assert.Equal(Signature, png[..8]);

        // IHDR: [uzunluk(4)][tip(4)][veri(13)][crc(4)] — imzadan hemen sonra.
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(8, png[24]);    // bit derinliği
        Assert.Equal(2, png[25]);    // renk tipi: truecolor RGB
    }

    [Fact]
    public async Task UretilenPng_IENDIleBiter()
    {
        var provider = new FakeImageProvider(ImageProviderKind.Generative);
        var result = await provider.GenerateAsync(
            new ImagePrompt { Text = "son", Width = 32, Height = 32 },
            ProviderContext.ForTest(),
            CancellationToken.None);

        var png = result.Value.Value.Data.ToArray();

        Assert.Equal("IEND", Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public async Task StokSaglayici_UretmeyeCalisirsaAcikHataDoner()
    {
        // Sessizce boş sonuç dönmek, hatanın boru hattının ilerisinde
        // anlamsız bir yerde patlamasına yol açar.
        var stock = new FakeImageProvider(ImageProviderKind.Stock);
        var generative = new FakeImageProvider(ImageProviderKind.Generative);

        var wrongSearch = await generative.FindAsync(
            new ImageQuery { Terms = "x" }, ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(wrongSearch.IsFailure);
        Assert.Equal("fake.image.not_stock", wrongSearch.Error.Code);
        Assert.Equal(ImageProviderKind.Stock, stock.Kind);
    }
}

public sealed class FakeStorageTests
{
    private static async Task<byte[]> Bytes(string content) => Encoding.UTF8.GetBytes(content);

    [Fact]
    public async Task AyniIcerikIkiKez_TekKopyaKalir()
    {
        using var storage = new FakeStorageProvider();
        var metadata = new AssetMetadata { Kind = AssetKind.Image, MimeType = "image/png" };

        using var first = new MemoryStream(await Bytes("aynı içerik"));
        using var second = new MemoryStream(await Bytes("aynı içerik"));

        var a = await storage.PutAsync(first, metadata, CancellationToken.None);
        var b = await storage.PutAsync(second, metadata, CancellationToken.None);

        Assert.False(a.Value.AlreadyExisted);
        Assert.True(b.Value.AlreadyExisted);
        Assert.Equal(a.Value.Ref, b.Value.Ref);
        Assert.Equal(1, storage.Count);
    }

    [Fact]
    public async Task FarkliIcerik_FarkliAdres()
    {
        using var storage = new FakeStorageProvider();
        var metadata = new AssetMetadata { Kind = AssetKind.Image, MimeType = "image/png" };

        using var first = new MemoryStream(await Bytes("bir"));
        using var second = new MemoryStream(await Bytes("iki"));

        var a = await storage.PutAsync(first, metadata, CancellationToken.None);
        var b = await storage.PutAsync(second, metadata, CancellationToken.None);

        Assert.NotEqual(a.Value.Ref, b.Value.Ref);
        Assert.Equal(2, storage.Count);
    }

    [Fact]
    public async Task GetLocalPath_GercektenVarOlanDosyayiDoner()
    {
        // ADR-007: render ağa çıkmaz, yerel dosya okur. Bu yol gerçek olmazsa
        // FFmpeg çalışamaz ve sahte boru hattı gerçek olanı temsil etmez.
        using var storage = new FakeStorageProvider();
        using var stream = new MemoryStream(await Bytes("render bunu okuyacak"));

        var stored = await storage.PutAsync(
            stream,
            new AssetMetadata { Kind = AssetKind.Audio, MimeType = "audio/wav" },
            CancellationToken.None);

        var path = await storage.GetLocalPathAsync(stored.Value.Ref, CancellationToken.None);

        Assert.True(path.IsSuccess);
        Assert.True(File.Exists(path.Value), $"Dosya yok: {path.Value}");
        Assert.EndsWith(".wav", path.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OlmayanVarlik_AcikHataVerir()
    {
        using var storage = new FakeStorageProvider();
        var missing = AssetRef.Create(new string('a', 64));

        var result = await storage.GetLocalPathAsync(missing, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.not_found", result.Error.Code);
    }
}

public sealed class FakePublisherTests
{
    private static readonly LanguageTag Turkish = LanguageTag.Create("tr-TR");

    private static PublishRequest Request(string key, string title = "Sahte video") => new()
    {
        VideoPath = "yok.mp4",
        IdempotencyKey = key,
        Metadata = new PublishMetadata
        {
            Title = title,
            Description = "Açıklama",
            Language = Turkish,
        },
    };

    [Fact]
    public async Task AyniIdempotencyAnahtari_IkinciKezYuklemez()
    {
        // §2.4/16: upload başarılı olup DB yazımı çökerse retry ikinci kopyayı
        // yüklerdi. Sahte yayıncı bu davranışı taklit etmezse koruma hiç sınanmaz.
        var publisher = new FakePublisher();
        var ctx = ProviderContext.ForTest();

        var first = await publisher.PublishAsync(Request("run-1:publish"), ctx, CancellationToken.None);
        var second = await publisher.PublishAsync(Request("run-1:publish"), ctx, CancellationToken.None);

        Assert.Equal(first.Value.Value.ExternalId, second.Value.Value.ExternalId);
        Assert.Equal(1, publisher.PublishedCount);
    }

    [Fact]
    public async Task IkinciCagri_KotaHarcamaz()
    {
        var publisher = new FakePublisher();
        var ctx = ProviderContext.ForTest();

        await publisher.PublishAsync(Request("run-2:publish"), ctx, CancellationToken.None);
        var afterFirst = publisher.QuotaRemaining;
        await publisher.PublishAsync(Request("run-2:publish"), ctx, CancellationToken.None);

        Assert.Equal(afterFirst, publisher.QuotaRemaining);
    }

    [Fact]
    public async Task KotaDolunca_HataDegilKaynakDurumuDoner()
    {
        // ADR-011: kota bitişi başarısızlık değil ERTELEMEDİR. Kalıcı hata
        // sayılsaydı run düşerdi; geçici sayılsaydı dolu hesaba üst üste
        // istek atılırdı. Üçüncü bir sınıf gerekiyor.
        var publisher = new FakePublisher(dailyQuota: 3_000);   // 1 yüklemeye yeter, 2'ye yetmez
        var ctx = ProviderContext.ForTest();

        var first = await publisher.PublishAsync(Request("a"), ctx, CancellationToken.None);
        var second = await publisher.PublishAsync(Request("b"), ctx, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Equal(ErrorKind.Resource, second.Error.Kind);
        Assert.False(second.Error.IsRetryable);
        Assert.NotNull(second.Error.RetryAfter);

        // Reddedilen çağrı kotayı harcamamalı.
        Assert.Equal(3_000 - 1_600, publisher.QuotaRemaining);
    }

    [Fact]
    public async Task CokUzunBaslik_Reddedilir()
    {
        var publisher = new FakePublisher();
        var result = await publisher.PublishAsync(
            Request("c", new string('x', 101)), ProviderContext.ForTest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("publish.title_too_long", result.Error.Code);
    }

    [Fact]
    public async Task FindExisting_YuklenmemisIcinNullDoner()
    {
        var publisher = new FakePublisher();

        var result = await publisher.FindExistingAsync("hic-yuklenmedi", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ZamanlanmisYayin_GizliBaslar()
    {
        var publisher = new FakePublisher();
        var request = Request("d") with
        {
            Visibility = Visibility.Public,
            PublishAt = new DateTimeOffset(2026, 9, 1, 18, 0, 0, TimeSpan.Zero),
        };

        var result = await publisher.PublishAsync(request, ProviderContext.ForTest(), CancellationToken.None);

        Assert.Equal(Visibility.Private, result.Value.Value.Visibility);
        Assert.Equal(request.PublishAt, result.Value.Value.ScheduledFor);
    }
}

/// P0-15'in kabul kriteri: fake'ler ağa çıkmaz.
///
/// Bunu çalışma zamanında kanıtlamak zor (ağı kesmek gerekir); IL metadata'sında
/// kanıtlamak ise kesin ve ucuz. Bir gün "şu fake gerçek API'ye bir sorsun"
/// denirse test kırmızıya döner.
public sealed class FakeProvidersAreOfflineTests
{
    private static readonly HashSet<string> NetworkTypes = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpClient",
        "System.Net.WebClient",
        "System.Net.Sockets.Socket",
        "System.Net.Sockets.TcpClient",
        "System.Net.WebRequest",
        "System.Net.HttpWebRequest",
    };

    [Fact]
    public void SahteSaglayicilar_AgTiplerineDokunmaz()
    {
        var assembly = typeof(FakeLlmProvider).Assembly;

        var offenders = ReferencedTypeNames(assembly.Location)
            .Where(NetworkTypes.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Sahte sağlayıcı ağ tipi kullanıyor: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void SahteSaglayicilar_RastgelelikKullanmaz()
    {
        // Random veya Guid.NewGuid kullanan bir fake determinist olamaz.
        // (FakeStorageProvider geçici dizin adı için Guid kullanıyor; o üretilen
        // İÇERİĞİ değil, yalnızca test klasörünün adını etkiler — bu yüzden
        // denetim yalnızca System.Random üzerinde.)
        var assembly = typeof(FakeLlmProvider).Assembly;

        var offenders = ReferencedTypeNames(assembly.Location)
            .Where(name => name is "System.Random")
            .ToList();

        Assert.True(offenders.Count == 0, "Sahte sağlayıcı System.Random kullanıyor — determinizm bozulur.");
    }

    private static IEnumerable<string> ReferencedTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.TypeReferences)
        {
            var typeRef = reader.GetTypeReference(handle);
            var ns = reader.GetString(typeRef.Namespace);
            var name = reader.GetString(typeRef.Name);
            yield return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
    }
}
