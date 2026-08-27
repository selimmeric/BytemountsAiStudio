using System.Text;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Persistence.Storage;

namespace BytemountsAiStudio.Persistence.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class AssetStoreTests(DatabaseFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmai-cas-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly AssetMetadata PngMeta = new() { Kind = AssetKind.Image, MimeType = "image/png" };

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erisilemiyor ({fixture.UnavailableReason}).");

    [Fact]
    public async Task AyniIcerikIkiKez_TekDosyaTekKayit()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = new FileSystemAssetStore(db, _root);

        using var a = new MemoryStream(Encoding.UTF8.GetBytes("ayni icerik"));
        using var b = new MemoryStream(Encoding.UTF8.GetBytes("ayni icerik"));

        var first = await store.PutAsync(a, PngMeta, CancellationToken.None);
        var second = await store.PutAsync(b, PngMeta, CancellationToken.None);

        Assert.False(first.Value.AlreadyExisted);
        Assert.True(second.Value.AlreadyExisted);
        Assert.Equal(first.Value.Ref, second.Value.Ref);
        Assert.Single(Directory.GetFiles(_root, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Sha256_IceriktenTuruyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = new FileSystemAssetStore(db, _root);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("merhaba"));
        var stored = await store.PutAsync(stream, PngMeta, CancellationToken.None);

        // "merhaba" icin bilinen sha256
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("merhaba")));

        Assert.Equal(expected, stored.Value.Ref.Sha256);
    }

    [Fact]
    public async Task DizinShardlanir()
    {
        // Tek dizinde yuz binlerce dosya dosya sistemini yavaslatir.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = new FileSystemAssetStore(db, _root);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("shard testi"));
        var stored = await store.PutAsync(stream, PngMeta, CancellationToken.None);
        var path = await store.GetLocalPathAsync(stored.Value.Ref, CancellationToken.None);

        var relative = Path.GetRelativePath(_root, path.Value).Replace(Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var sha = stored.Value.Ref.Sha256;

        Assert.Equal($"{sha[..2]}/{sha[2..4]}/{sha}.png", relative);
    }

    [Fact]
    public async Task YazilanDosya_GercektenOkunabilir()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = new FileSystemAssetStore(db, _root);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("render bunu okuyacak"));
        var stored = await store.PutAsync(stream, PngMeta, CancellationToken.None);

        var opened = await store.OpenAsync(stored.Value.Ref, CancellationToken.None);
        using var reader = new StreamReader(opened.Value);

        Assert.Equal("render bunu okuyacak", await reader.ReadToEndAsync(CancellationToken.None));
    }

    [Fact]
    public async Task KayitVarDosyaYok_AcikHataVerir()
    {
        // Sessizce gecmek, render'in "varlik var" deyip patlamasina yol acardi.
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = new FileSystemAssetStore(db, _root);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("silinecek"));
        var stored = await store.PutAsync(stream, PngMeta, CancellationToken.None);
        var path = await store.GetLocalPathAsync(stored.Value.Ref, CancellationToken.None);
        File.Delete(path.Value);

        var result = await store.GetLocalPathAsync(stored.Value.Ref, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.file_missing", result.Error.Code);
    }

    [Fact]
    public async Task OlmayanVarlik_NotFound()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();
        var store = new FileSystemAssetStore(db, _root);

        var result = await store.GetLocalPathAsync(AssetRef.Create(new string('b', 64)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.not_found", result.Error.Code);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }
}
