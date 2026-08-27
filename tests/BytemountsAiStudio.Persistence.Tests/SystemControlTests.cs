using BytemountsAiStudio.Persistence.Entities;
using BytemountsAiStudio.Persistence.Providers;
using BytemountsAiStudio.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Persistence.Tests;

/// Acil durdurma ve kanal duraklatmanın testleri (P2-04).
///
/// Kabul kriteri: **tek tıkla tüm kuyruklar duruyor.** Bunun için
/// durum veritabanında olmak zorunda — statik bir alan yalnızca o
/// süreci durduruyordu ve filodaki diğer worker'lar hiçbir şey
/// görmüyordu.
[Collection(DatabaseCollection.Name)]
public sealed class SystemControlTests(DatabaseFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!fixture.Available)
        {
            return;
        }

        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM settings");

        SystemControl.Invalidate();
    }

    public Task DisposeAsync()
    {
        SystemControl.Invalidate();
        return Task.CompletedTask;
    }

    private void RequireDatabase()
        => Assert.True(fixture.Available, $"PostgreSQL erişilemiyor ({fixture.UnavailableReason}).");

    [Fact]
    public async Task VarsayilanDurum_Kapali()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var state = await new SystemControl(db).KillSwitchAsync(CancellationToken.None);

        Assert.False(state.Engaged);
    }

    /// DURUM VERİTABANINDA: ikinci bir bağlam (başka bir süreç gibi)
    /// de görüyor. Statik bir alanla bu test geçmezdi ve filodaki
    /// diğer worker'lar durmazdı.
    [Fact]
    public async Task Durdurma_BaskaBaglamdanDaGorunuyor()
    {
        RequireDatabase();

        await using (var writer = fixture.CreateContext())
        {
            await new SystemControl(writer)
                .SetKillSwitchAsync(true, "selim", "maliyet fırladı", CancellationToken.None);
        }

        SystemControl.Invalidate();

        await using var reader = fixture.CreateContext();
        var state = await new SystemControl(reader).KillSwitchAsync(CancellationToken.None);

        Assert.True(state.Engaged);
        Assert.Equal("selim", state.By);
        Assert.Equal("maliyet fırladı", state.Reason);
        Assert.NotNull(state.Since);
    }

    /// Önbellek HEMEN geçersiz oluyor: durdurmayı basan kişinin kendi
    /// ekranında beş saniye "kapalı" görmesi, düğmenin çalışmadığı
    /// izlenimi verirdi.
    [Fact]
    public async Task Durdurma_AyniBaglamdaAninda()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var control = new SystemControl(db);

        await control.KillSwitchAsync(CancellationToken.None);
        await control.SetKillSwitchAsync(true, "selim", "test", CancellationToken.None);

        Assert.True((await control.KillSwitchAsync(CancellationToken.None)).Engaged);
    }

    [Fact]
    public async Task Durdurma_Kaldirilabiliyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var control = new SystemControl(db);

        await control.SetKillSwitchAsync(true, "selim", "test", CancellationToken.None);
        await control.SetKillSwitchAsync(false, "selim", null, CancellationToken.None);

        Assert.False((await control.KillSwitchAsync(CancellationToken.None)).Engaged);
    }

    /// KANAL DURAKLATMA acil durdurmadan AYRI: biri her şeyi, diğeri
    /// yalnızca o kanalın yeni işlerini durduruyor. Tek bayrağa
    /// indirmek, bir kanalı susturmak için bütün sistemi durdurmak
    /// demekti.
    [Fact]
    public async Task KanalDuraklatma_AcilDurdurmadanBagimsiz()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel { Name = "Test kanalı", Language = "tr-TR" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var control = new SystemControl(db);

        await control.SetChannelPausedAsync(channel.Id, true, CancellationToken.None);

        Assert.True(await control.IsChannelPausedAsync(channel.Id, CancellationToken.None));

        // Acil durdurma HÂLÂ kapalı: kanal duraklatmak sistemi
        // durdurmuyor.
        Assert.False((await control.KillSwitchAsync(CancellationToken.None)).Engaged);

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// Duraklatılmış kanal ÜCRETLİ ÇAĞRI yapamıyor.
    [Fact]
    public async Task DuraklatilmisKanal_CagriYapamiyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var channel = new Channel { Name = "Duraklatılmış", Language = "tr-TR", IsPaused = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync(CancellationToken.None);

        var gate = new BudgetGate(db, new CostLedger(db), new SystemControl(db));

        var result = await gate.AuthorizeAsync(channel.Id, 0.01m, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("budget.channel_paused", result.Error.Code);

        // KAYNAK hatası: kanal devam ettirilince aynı iş çalışacak.
        Assert.Equal(Core.Errors.ErrorKind.Resource, result.Error.Kind);

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OlmayanKanal_Cokmuyor()
    {
        RequireDatabase();
        await using var db = fixture.CreateContext();

        var control = new SystemControl(db);
        var missing = Guid.CreateVersion7();

        await control.SetChannelPausedAsync(missing, true, CancellationToken.None);

        Assert.False(await control.IsChannelPausedAsync(missing, CancellationToken.None));
    }
}
