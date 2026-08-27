using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Core.Execution;
using BytemountsAiStudio.Persistence;

namespace BytemountsAiStudio.Api;

/// Canlı ilerleme yayını (SSE, P1-28).
///
/// Kabul kriteri: **pano yenilemeden ilerleme görüyor.**
///
/// Neden SSE, WebSocket değil: akış TEK YÖNLÜ. Panonun sunucuya
/// söyleyeceği bir şey yok, yalnızca dinliyor. WebSocket iki yönlü bir
/// kanal için el sıkışma, ping/pong ve yeniden bağlanma mantığı
/// getiriyor — SSE'de yeniden bağlanmayı tarayıcı kendisi yapıyor.
internal static class ProgressStream
{
    /// Veritabanına ne sıklıkla bakılacağı.
    ///
    /// Bir saniye: node'lar saniyeler süren işler, daha sık bakmak
    /// aynı cevabı almak demek. Daha seyrek bakmak ise panoyu "donmuş"
    /// gösterirdi.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// Değişiklik olmasa da bu aralıkta bir yorum satırı gidiyor.
    ///
    /// GEREKLİ: aradaki vekil sunucular ve tarayıcılar sessiz bir
    /// bağlantıyı kapatıyor. Yorum satırı (`:`) SSE'de yok sayılıyor,
    /// yani istemciye sahte bir olay göndermeden bağlantı ayakta
    /// kalıyor.
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task WriteAsync(
        HttpContext http, StudioDbContext db, Guid runId, TimeProvider time, CancellationToken cancellationToken)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        // Vekil sunucularda ara belleklemeyi kapatıyor: aksi hâlde
        // olaylar toplanıp bir arada gönderiliyor ve "canlı" olma
        // özelliği tamamen kayboluyor.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        string? last = null;
        var lastWrite = time.GetUtcNow();

        while (!cancellationToken.IsCancellationRequested)
        {
            var progress = await RunQueries.ProgressAsync(db, runId, cancellationToken).ConfigureAwait(false);

            if (progress is null)
            {
                await WriteEventAsync(http, "error", """{"message":"run yok"}""", cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            var payload = JsonSerializer.Serialize(progress, Json);

            // YALNIZCA DEĞİŞİNCE gönderiliyor.
            //
            // Her saniye aynı belgeyi göndermek, istemcinin "bir şey
            // oldu" ile "bağlantı ayakta" arasındaki farkı görmesini
            // engellerdi — ve panoyu her saniye yeniden çizdirirdi.
            if (payload != last)
            {
                await WriteEventAsync(http, "progress", payload, cancellationToken).ConfigureAwait(false);
                last = payload;
                lastWrite = time.GetUtcNow();
            }
            else if (time.GetUtcNow() - lastWrite >= KeepAlive)
            {
                await WriteCommentAsync(http, cancellationToken).ConfigureAwait(false);
                lastWrite = time.GetUtcNow();
            }

            // BİTMİŞ RUN'IN AKIŞI KAPANIYOR.
            //
            // Açık bırakmak, tamamlanmış her run için bir bağlantıyı
            // sonsuza kadar tutmak demekti; panoyu bir gün açık
            // bırakan biri sunucudaki bağlantıları tüketirdi.
            if (IsTerminal(progress.State))
            {
                await WriteEventAsync(http, "done", payload, cancellationToken).ConfigureAwait(false);

                return;
            }

            await Task.Delay(PollInterval, time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// Run bitti mi.
    ///
    /// `WaitingApproval` ve `WaitingResource` BİTMİŞ SAYILMIYOR: run
    /// devam edecek, yalnızca bekliyor. Akışı kapatmak, onay verildiği
    /// anda panonun bunu görememesi demekti — oysa panonun asıl işi o.
    internal static bool IsTerminal(RunState state)
        => state is RunState.Completed or RunState.Failed or RunState.Cancelled;

    private static async Task WriteEventAsync(
        HttpContext http, string name, string data, CancellationToken cancellationToken)
    {
        await http.Response
            .WriteAsync(string.Create(CultureInfo.InvariantCulture, $"event: {name}\ndata: {data}\n\n"), cancellationToken)
            .ConfigureAwait(false);

        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCommentAsync(HttpContext http, CancellationToken cancellationToken)
    {
        await http.Response.WriteAsync(": ping\n\n", cancellationToken).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
