using Microsoft.EntityFrameworkCore;

namespace BytemountsAiStudio.Queue;

/// İş koşarken kiralamayı uzatan arka plan atışı (§8.1).
///
/// ***BU SINIF UZUN SÜRE YOKTU VE `HeartbeatAsync` HİÇBİR YERDEN
/// ÇAĞRILMIYORDU.***
///
/// Kuyruk atışı destekliyordu, kiralama süreleri iş sınıfına göre
/// ayarlanmıştı ve `ReclaimExpiredAsync` süresi dolan işleri geri
/// alıyordu — ama süreyi uzatan kimse yoktu. Sonucu şu: 60 dakikayı
/// aşan bir render (uzun video, yavaş makine) HÂLÂ KOŞARKEN geri
/// alınıyor ve ikinci bir worker aynı işi paralel başlatıyor. İki
/// FFmpeg süreci aynı çıktı dosyasına yazıyor, `node_executions` çift
/// kayıt alıyor ve maliyet iki katına çıkıyor. Aynı şekilde 3 dakikayı
/// aşan bir LLM node'u da geri alınabiliyordu.
///
/// ***ATIŞ KENDİ BAĞLANTISINI AÇIYOR.***
///
/// İşin kullandığı `DbContext` ile atmak mümkün değil: `DbContext`
/// iş parçacığı güvenli değil ve node koşarken aynı bağlam üzerinden
/// maliyet defterine de yazılıyor. İkisi aynı anda olsaydı EF
/// "ikinci bir işlem başlatıldı" diyerek düşerdi — ve bu, kurtarmaya
/// çalıştığımız işi kaybetmenin yeni bir yolu olurdu.
///
/// ***KİRALAMA KAYBEDİLİRSE İŞ DURDURULUYOR.***
///
/// Atış "bu iş artık senin değil" cevabı alırsa (başka bir worker
/// devraldı, ya da iş iptal edildi) devam etmek İKİ WORKER'IN AYNI İŞİ
/// KOŞMASI demek — atışın önlemeye çalıştığı şeyin ta kendisi. `Token`
/// o anda iptal ediliyor ve node'un kendisi iptal olarak sonlanıyor.
public sealed class LeaseKeeper : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _linked;
    private Task _loop = Task.CompletedTask;

    private LeaseKeeper(CancellationToken cancellationToken)
        => _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);

    /// İş bu belirteçle koşuyor: kiralama kaybedilirse iptal oluyor.
    public CancellationToken Token => _linked.Token;

    /// Kiralama kaybedildi mi (iş bittikten sonra bakılıyor).
    public bool LeaseLost { get; private set; }

    /// ATIŞ ARALIĞI KİRALAMANIN ÜÇTE BİRİ.
    ///
    /// Yarısı da çalışırdı ama tek bir kaçırılan atışta iş geri
    /// alınırdı: ağ yavaşladığında ya da veritabanı bir saniye
    /// takıldığında sistem işi kaybederdi. Üçte birde iki atış üst
    /// üste kaçırılmadan kayıp olmuyor. Beş saniyenin altına
    /// inmiyor — kısa kiralamalı işler (3 dakika) için saniyede bir
    /// UPDATE, kazandırdığından fazlasını götürürdü.
    public static TimeSpan IntervalFor(TimeSpan lease)
    {
        var third = lease / 3;

        return third < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : third;
    }

    /// Atışı başlatır.
    ///
    /// `connectionString` YOKSA ATIŞ DA YOK ve bu açıkça yazılı: bağlantı
    /// dizgesi olmayan bir kurulumda (bellek içi test) atacak bir yer
    /// yok. Sessizce atıyormuş gibi davranmak yerine hiç kurulmuyor —
    /// ve `Token` yine çalışıyor, yani çağıran taraf iki hâli ayrı ayrı
    /// ele almak zorunda kalmıyor.
    public static LeaseKeeper Start(
        string? connectionString,
        Guid jobId,
        string workerId,
        TimeSpan lease,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        Action<string>? onLost = null)
    {
        var keeper = new LeaseKeeper(cancellationToken);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            keeper._loop = keeper.BeatAsync(
                connectionString, jobId, workerId, lease,
                timeProvider ?? TimeProvider.System, onLost, keeper._stop.Token);
        }

        return keeper;
    }

    private async Task BeatAsync(
        string connectionString,
        Guid jobId,
        string workerId,
        TimeSpan lease,
        TimeProvider time,
        Action<string>? onLost,
        CancellationToken stopToken)
    {
        var interval = IntervalFor(lease);

        while (!stopToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, time, stopToken).ConfigureAwait(false);

                await using var db = new Persistence.StudioDbContext(
                    Persistence.StudioDbContextFactory.Build(connectionString).Options);

                // UZATMA TAM KİRALAMA SÜRESİ KADAR: kalan süreye
                // eklemek değil, "şimdiden itibaren bir kiralama daha"
                // demek. Eklemeli olsaydı uzun bir iş kiralamayı
                // sınırsız büyütür ve gerçekten çöktüğünde saatlerce
                // takılı kalırdı.
                var extended = await new JobQueue(db, time)
                    .HeartbeatAsync(jobId, workerId, lease, stopToken)
                    .ConfigureAwait(false);

                if (extended)
                {
                    continue;
                }

                // KİRALAMA BİZDE DEĞİL: iş başka bir worker'a geçmiş ya
                // da tamamlanmış. Devam etmek iki worker'ın aynı işi
                // koşması demek.
                LeaseLost = true;
                onLost?.Invoke($"Kiralama kaybedildi (is {jobId}, worker {workerId}); node iptal ediliyor.");

                await _linked.CancelAsync().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Normal duruş: iş bitti ve atış durduruldu.
                return;
            }
#pragma warning disable CA1031 // Atis hatasi isi dusurmemeli.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // ***ATIŞ HATASI İŞİ DÜŞÜRMÜYOR AMA DÖNGÜYÜ DE
                // BİTİRMİYOR.***
                //
                // Veritabanı bir an erişilemez olduğunda doğru davranış
                // koşan render'ı öldürmek değil: kiralama süresi hâlâ
                // dolmadı ve bir SONRAKİ atış başarılı olabilir. İlk
                // yazımda `catch` döngünün dışındaydı ve tek bir geçici
                // hata atışı KALICI olarak susturuyordu — yani
                // düzeltmenin kendisi, düzeltmeye çalıştığı hatayı
                // geri getiriyordu.
                //
                // Sessiz de değil: görünmez kalırsa, kiralamanın hiç
                // uzatılmadığı bir kurulum ancak işler ikişer kez
                // koştuğunda fark edilirdi.
                onLost?.Invoke($"Kiralama uzatilamadi (is {jobId}): {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Durus hatasi isin sonucunu degistirmemeli.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Atış zaten durduruluyor.
        }

        _linked.Dispose();
        _stop.Dispose();
    }
}
