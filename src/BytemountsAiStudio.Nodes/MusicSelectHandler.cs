using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Assets;
using BytemountsAiStudio.Core.Errors;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Core.Time;
using BytemountsAiStudio.Workflow.Engine;

namespace BytemountsAiStudio.Nodes;

/// Arka plan müziğini seçip indiren node (P2-09).
///
/// SEÇİM VE İNDİRME AYRI ADIMLAR DEĞİL, çünkü seçilip indirilemeyen
/// bir parça hiç seçilmemiş sayılmalı: timeline'a yazılmış ama dosyası
/// olmayan bir müzik, render'ı çalışma ortasında düşürürdü.
///
/// MÜZİK ZORUNLU DEĞİL. Bulunamazsa node BAŞARIYLA bitiyor ve müziksiz
/// devam ediyor — müziksiz video tamamen geçerli, müzik yüzünden koşuyu
/// düşürmek bir videoyu tamamen kaybetmek olurdu. Ama "müzik yok"
/// bilgisi çıktıya yazılıyor: sessizce atlanırsa "neden bu videoda
/// müzik yok" sorusunun cevabı hiçbir yerde olmaz.
/// İndirilen ses.
public sealed record DownloadedAudio(byte[] Bytes, string MimeType);

public sealed class MusicSelectHandler(
    IMusicProvider music,
    IStorageProvider storage,
    Func<Uri, CancellationToken, Task<Result<DownloadedAudio>>> download) : INodeHandler
{
    public string NodeType => "music.select";

    /// Ağa çıkıyor ama model çağırmıyor.
    public QueueClass Queue => QueueClass.Search;

    /// Varsayılan ruh hâli.
    ///
    /// `ambient`: en geniş sonuç kümesi ve konuşmanın altında en az
    /// rahatsız eden tür. Yanlış ruh hâli seçmenin bedeli, hiç müzik
    /// bulamamaktan düşük.
    public const string DefaultMood = "ambient";

    /// İndirilecek en büyük dosya.
    ///
    /// YİRMİ MEGABAYT: bir dakikalık kaliteli müzik en çok birkaç MB.
    /// Sınır olmasaydı, yanlış etiketlenmiş bir podcast bölümü
    /// (saatlerce, yüzlerce MB) diski ve zamanı yerdi — ve bunu ancak
    /// disk dolduğunda fark ederdik.
    public const long MaxBytes = 20 * 1024 * 1024;

    public async Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var mood = NodeJson.Text(context.Config, "mood") ?? DefaultMood;
        var minimum = MinimumDurationFrom(context.RunContext);

        var selected = await music.SelectAsync(
            new MusicQuery { Mood = mood, MinimumDuration = minimum },
            ScriptGenerateHandler.Context(context),
            cancellationToken).ConfigureAwait(false);

        if (selected.IsFailure)
        {
            return Skipped(mood, selected.Error.Message);
        }

        var track = selected.Value.Value;

        // LİSANS KANITI OLMAYAN PARÇA HİÇ İNDİRİLMİYOR.
        //
        // Görsellerde eksik atıf düzeltilebilir bir kusur; müzikte
        // düzeltilemez bir hasar: Content ID müziği otomatik tanıyor
        // ve bir talep, kanalın o videodan gelen gelirinin tamamını
        // götürüyor. Atıf gerekiyorsa yazar adı da şart — yoksa atıf
        // zaten yapılamaz.
        if (string.IsNullOrWhiteSpace(track.License.Name)
            || (track.License.RequiresAttribution && string.IsNullOrWhiteSpace(track.License.Author)))
        {
            return Skipped(mood, "lisans kanıtı eksik; parça kullanılmadı");
        }

        var stored = await DownloadAsync(track, cancellationToken).ConfigureAwait(false);

        if (stored.IsFailure)
        {
            // İNDİRME HATASI KOŞUYU DÜŞÜRMÜYOR ama sebebi yazılıyor.
            return Skipped(mood, stored.Error.Message);
        }

        return Result.Success(NodeJson.From(new
        {
            asset = stored.Value.Ref.ToString(),
            title = track.Title,
            duration_ms = track.Duration.Value,
            mood,
            source_url = track.Url.ToString(),
            license = new
            {
                name = track.License.Name,
                url = track.License.Url?.ToString(),
                author = track.License.Author,
                requires_attribution = track.License.RequiresAttribution,
                captured_at = track.License.CapturedAt,
            },
        }));
    }

    private async Task<Result<StoredAsset>> DownloadAsync(
        MusicTrack track, CancellationToken cancellationToken)
    {
        var audio = await download(track.Url, cancellationToken).ConfigureAwait(false);

        if (audio.IsFailure)
        {
            return Result.Failure<StoredAsset>(audio.Error);
        }

        using var buffer = new MemoryStream(audio.Value.Bytes);

        return await storage.PutAsync(
            buffer,
            new AssetMetadata
            {
                Kind = AssetKind.Audio,
                MimeType = audio.Value.MimeType,
                Duration = track.Duration,
                SourceProvider = music.Key,
                SourceUrl = track.Url,
                // LİSANS VARLIKLA BİRLİKTE SAKLANIYOR.
                //
                // Yalnızca timeline'a yazsaydık, aynı parça başka bir
                // videoda kullanıldığında lisans bilgisi yeniden
                // aranmak zorunda kalırdı — ve aranmazsa sessizce
                // kaybolurdu.
                License = track.License,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// Gerçek HTTP indirici.
    ///
    /// Ayrı bir işlev olarak veriliyor ki testler ağa çıkmasın ve
    /// sahte hat gerçek bir dosya üretebilsin — görsel tarafındaki
    /// (`StockFirstImageProvider.HttpDownloader`) desenin aynısı.
    public static Func<Uri, CancellationToken, Task<Result<DownloadedAudio>>> HttpDownloader(
        HttpClient http, string userAgent = "BytemountsAiStudio/0.1")
    {
        ArgumentNullException.ThrowIfNull(http);

        return async (url, cancellationToken) =>
        {
            HttpResponseMessage response;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(userAgent);

                response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return Error.Transient("music.download_failed", ex.Message);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                return Error.Transient("music.timeout", ex.Message);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Error.Transient("music.download_failed",
                        $"İndirme başarısız: HTTP {(int)response.StatusCode}");
                }

                // BOYUT ÖNCE BAŞLIKTAN: sunucu söylüyorsa indirmeye
                // hiç başlamamak, yirmi megabaytı okuyup atmaktan iyi.
                if (response.Content.Headers.ContentLength is { } declared && declared > MaxBytes)
                {
                    return Error.Permanent("music.too_large",
                        $"Parça çok büyük: {declared / 1024 / 1024} MB > {MaxBytes / 1024 / 1024} MB");
                }

                using var buffer = new MemoryStream();

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                if (!await CopyBoundedAsync(stream, buffer, cancellationToken).ConfigureAwait(false))
                {
                    // BAŞLIK YALAN SÖYLEYEBİLİYOR: `Content-Length`
                    // olmayan ya da yanlış olan bir cevap sınırı
                    // aşabiliyor. Okurken de bakmak gerekiyor.
                    return Error.Permanent("music.too_large",
                        $"Parça {MaxBytes / 1024 / 1024} MB sınırını aştı.");
                }

                return Result.Success(new DownloadedAudio(
                    buffer.ToArray(),
                    response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg"));
            }
        };
    }

    /// Sınırı aşarsa false döner ve okumayı bırakır.
    internal static async Task<bool> CopyBoundedAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return true;
            }

            total += read;

            if (total > MaxBytes)
            {
                return false;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    /// Müzik en az video kadar uzun olmalı.
    ///
    /// Kısa bir parça döngüye alınabiliyor ama her döngü duyulur bir
    /// dikiş bırakıyor; video süresini bilerek aramak, o dikişlerden
    /// tamamen kaçınmanın yolu. Süre bilinmiyorsa 60 saniye: Shorts
    /// üst sınırı ve "en kötü ihtimal" burada doğru varsayım.
    internal static Ms MinimumDurationFrom(JsonElement runContext)
    {
        if (runContext.TryGetProperty("tts", out var tts)
            && tts.ValueKind == JsonValueKind.Object
            && tts.TryGetProperty("total_ms", out var total)
            && total.ValueKind == JsonValueKind.Number
            && total.TryGetInt32(out var ms)
            && ms > 0)
        {
            return new Ms(ms);
        }

        return new Ms(60_000);
    }

    /// Müziksiz devam: node başarılı, sebep kayıtlı.
    private static Result<JsonElement> Skipped(string mood, string reason)
        => Result.Success(NodeJson.From(new
        {
            asset = (string?)null,
            mood,
            skipped = true,
            reason,
        }));
}
