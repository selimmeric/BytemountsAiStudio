using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Core.Tests;

/// Kanal adaletinin testleri (P2-05).
///
/// Kabul kriteri: **3 kanallı yük testinde hiçbiri aç kalmıyor.**
/// Burada o test, gerçek bir yük koşturmadan yapılıyor — adalet
/// kararı saf olduğu için doğrudan sınanabiliyor.
public sealed class FairSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid C = new("00000000-0000-0000-0000-0000000000c3");

    private static ChannelLoad Load(Guid id, int running, int waiting, int waitingMinutes = 5)
        => new(id, running, waiting, waiting > 0 ? Now.AddMinutes(-waitingMinutes) : null);

    [Fact]
    public void BekleyenIsYoksa_NullDonuyor()
    {
        Assert.Null(FairScheduler.NextChannel([Load(A, running: 2, waiting: 0)]));
        Assert.Null(FairScheduler.NextChannel([]));
    }

    /// ÖNCE EN AZ KOŞAN: hakkaniyetin tanımı bu.
    [Fact]
    public void EnAzKosan_OnceSecILiyor()
    {
        var next = FairScheduler.NextChannel(
        [
            Load(A, running: 3, waiting: 5),
            Load(B, running: 0, waiting: 1),
            Load(C, running: 2, waiting: 9),
        ]);

        Assert.Equal(B, next);
    }

    /// Eşit sayıda koşanı olan kanallar arasında EN UZUN BEKLEYEN.
    /// Bu ikinci ölçüt olmadan seçim rastgele olur ve bir kanal
    /// şanssızlık yüzünden sürekli sona kalabilirdi.
    [Fact]
    public void EsitlikteEnUzunBekleyen()
    {
        var next = FairScheduler.NextChannel(
        [
            Load(A, running: 1, waiting: 2, waitingMinutes: 3),
            Load(B, running: 1, waiting: 2, waitingMinutes: 40),
        ]);

        Assert.Equal(B, next);
    }

    /// Karar KARARLI olmak zorunda: aynı sorgu iki kez farklı cevap
    /// verirse teşhis imkânsızlaşır.
    [Fact]
    public void Karar_Kararli()
    {
        var loads = new[]
        {
            Load(C, running: 1, waiting: 1, waitingMinutes: 10),
            Load(A, running: 1, waiting: 1, waitingMinutes: 10),
            Load(B, running: 1, waiting: 1, waitingMinutes: 10),
        };

        var first = FairScheduler.NextChannel(loads);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, FairScheduler.NextChannel(loads));
        }
    }

    /// KANAL BAŞINA TAVAN: bir kanal bütün worker'ları kaplayamıyor.
    [Fact]
    public void Tavan_KanaliDisariBirakiyor()
    {
        var next = FairScheduler.NextChannel(
        [
            Load(A, running: 2, waiting: 20),
            Load(B, running: 1, waiting: 1),
        ],
        maxPerChannel: 2);

        Assert.Equal(B, next);
    }

    [Fact]
    public void TumKanallarTavanda_NullDonuyor()
    {
        Assert.Null(FairScheduler.NextChannel(
            [Load(A, running: 2, waiting: 5), Load(B, running: 2, waiting: 5)],
            maxPerChannel: 2));
    }

    /// KABUL KRİTERİ: üç kanallı yükte hiçbiri aç kalmıyor.
    ///
    /// Yirmi işlik bir kampanya başlatan kanal varken, günde bir video
    /// üreten iki kanalın da sıra alması gerekiyor.
    [Fact]
    public void UcKanalliYuk_HicbiriAcKalmiyor()
    {
        var waiting = new Dictionary<Guid, int>(3) { [A] = 20, [B] = 2, [C] = 2 };
        var served = new Dictionary<Guid, int>(3) { [A] = 0, [B] = 0, [C] = 0 };

        // Dört worker, üç kanal: tavan 1.
        var cap = FairScheduler.CapFor(workerCount: 4, activeChannels: 3);

        // İŞLER ANINDA BİTİYOR: koşan sayısı hep sıfır kalıyor. En zor
        // durum bu — anlık yük ölçütü hiçbir şey ayırt etmiyor ve
        // adalet tamamen geçmiş paya kalıyor.
        for (var step = 0; step < 12; step++)
        {
            var loads = waiting.Keys
                .Select(id => new ChannelLoad(id, Running: 0, waiting[id],
                    waiting[id] > 0 ? Now.AddMinutes(-60) : null)
                {
                    RecentlyServed = served[id],
                })
                .ToList();

            var next = FairScheduler.NextChannel(loads, cap);

            if (next is not { } channel)
            {
                break;
            }

            waiting[channel]--;
            served[channel]++;
        }

        // B ve C, A'nın yirmi işinin arkasında beklemedi.
        Assert.True(served[B] >= 2, $"B yalnizca {served[B]} kez sira aldi");
        Assert.True(served[C] >= 2, $"C yalnizca {served[C]} kez sira aldi");
    }

    /// Açlık "iş yok" ile karıştırılmamalı: bekleyen işi OLAN ama hiç
    /// koşanı olmayan ve uzun süredir bekleyen kanal aç demektir.
    [Fact]
    public void Aclik_OlculebiliyOr()
    {
        var starving = new ChannelLoad(A, Running: 0, Waiting: 3, Now.AddHours(-2));
        var busy = new ChannelLoad(B, Running: 2, Waiting: 3, Now.AddHours(-2));
        var idle = new ChannelLoad(C, Running: 0, Waiting: 0, null);
        var recent = new ChannelLoad(A, Running: 0, Waiting: 1, Now.AddMinutes(-1));

        var threshold = TimeSpan.FromMinutes(30);

        Assert.True(FairScheduler.IsStarving(starving, Now, threshold));
        Assert.False(FairScheduler.IsStarving(busy, Now, threshold));
        Assert.False(FairScheduler.IsStarving(idle, Now, threshold));
        Assert.False(FairScheduler.IsStarving(recent, Now, threshold));
    }

    /// Tek kanal varken tavan koymak sistemi boşuna yavaşlatırdı.
    [Fact]
    public void TekKanal_TavanYok()
    {
        Assert.Equal(8, FairScheduler.CapFor(workerCount: 8, activeChannels: 1));
    }

    /// Tavan EN AZ 1: sıfır olsaydı hiçbir kanal iş alamazdı ve sistem
    /// sessizce dururdu.
    [Fact]
    public void Tavan_EnAzBir()
    {
        Assert.Equal(1, FairScheduler.CapFor(workerCount: 2, activeChannels: 9));
        Assert.Equal(1, FairScheduler.CapFor(workerCount: 0, activeChannels: 0));
    }

    [Fact]
    public void Ozet_AcKanalSayisiniIceriyor()
    {
        var text = FairScheduler.Describe(
            [new ChannelLoad(A, 0, 3, Now.AddHours(-2)), new ChannelLoad(B, 1, 0, null)],
            Now,
            TimeSpan.FromMinutes(30));

        Assert.Contains("1 ac", text, StringComparison.Ordinal);
    }
}
