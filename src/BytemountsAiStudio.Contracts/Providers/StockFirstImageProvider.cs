using System.Globalization;
using System.Net.Http;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Contracts.Providers;

/// "Önce stok, bulunamazsa üret" yönlendirmesi (P1-18, §9.3).
///
/// Kendisi `IImageProvider` — çağıran taraf tek bir sağlayıcıyla mı
/// konuştuğunu bilmiyor. Sıra bir tercih değil, gerekçesi var:
///
///   - Stok görsel GERÇEK bir fotoğraf. Üretilen görselde eller, yazılar
///     ve mimari detaylar hâlâ güvenilmez ve belgesel anlatıda bir hata
///     içeriğin tamamını şüpheli gösteriyor.
///   - Stok arama ücretsiz ya da çok ucuz; üretim hem para hem 20–40
///     saniye demek.
///   - Ama stok her sahne için sonuç vermiyor: soyut bir cümlenin
///     ("kayıtlar bunu doğrulamıyor") stok karşılığı yok. Üretim tam da
///     orada devreye giriyor.
///
/// Stok BULAMAMAK bir hata değil, normal bir sonuç. Bu yüzden boş
/// sonuç sessizce üretime düşüyor; stok sağlayıcısının HATA vermesi ise
/// ayrı bir durum ve kayda geçiyor.
public sealed class StockFirstImageProvider(
    IImageProvider stock,
    IImageProvider generative,
    Func<Uri, CancellationToken, Task<Result<GeneratedImage>>> download) : IImageProvider
{
    /// Bu boyutun altındaki stok görseller reddediliyor.
    ///
    /// 1080×1920 tuvale küçük bir görseli büyütmek bulanık kare üretiyor
    /// ve bu videoda ilk göze çarpan şey. Üretime düşmek daha iyi.
    public int MinimumWidth { get; init; } = 900;

    public int MinimumHeight { get; init; } = 900;

    public string Key => "stock-first";

    /// Zincirin türü STOK: çağıran "önce stok denenecek" bilgisini
    /// buradan okuyor.
    public ImageProviderKind Kind => ImageProviderKind.Stock;

    /// Son çağrının hangi yoldan gittiği. Node çıktısına yazılıyor:
    /// stok hiç tutmuyorsa arama terimleri kötü demektir ve bunu ancak
    /// kayıttan görebiliriz.
    public string? LastRoute { get; private set; }

    public Task<Result<ProviderResponse<IReadOnlyList<ImageCandidate>>>> FindAsync(
        ImageQuery query, ProviderContext context, CancellationToken cancellationToken)
        => stock.FindAsync(query, context, cancellationToken);

    /// Görsel üretir: önce stokta arar, bulamazsa gerçekten üretir.
    ///
    /// İmza `GenerateAsync` çünkü çağıranın istediği şey "bana bu sahne
    /// için bir görsel ver" — nereden geldiği onun sorunu değil.
    public async Task<Result<ProviderResponse<GeneratedImage>>> GenerateAsync(
        ImagePrompt prompt, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var found = await stock.FindAsync(
            new ImageQuery
            {
                // Stok araması KISA terim istiyor; üretim istemi uzun ve
                // bağlamlı. Görsel yönetmen ikisini ayrı üretiyor ama
                // buraya yalnızca istem geliyor, o yüzden kısaltılıyor.
                Terms = ShortTerms(prompt.Text),
                PreferredAspectRatio = prompt.Height == 0 ? null : (double)prompt.Width / prompt.Height,
                MaxResults = 10,
                ExcludeTextInImage = true,
            },
            context,
            cancellationToken).ConfigureAwait(false);

        if (found.IsSuccess)
        {
            var usable = found.Value.Value
                .Where(c => c.Width >= MinimumWidth && c.Height >= MinimumHeight)
                .ToList();

            foreach (var candidate in usable)
            {
                var downloaded = await download(candidate.Url, cancellationToken).ConfigureAwait(false);

                if (downloaded.IsFailure)
                {
                    // Tek bir adayın indirilememesi zinciri düşürmüyor:
                    // sıradaki aday denenmeli. Stok servisleri sık sık
                    // ölü bağlantı döndürüyor.
                    continue;
                }

                LastRoute = "stock";

                // LİSANS ADAYDAN geliyor, indirilen bayttan değil.
                // İndiren taraf lisansı bilmiyor; aday biliyor ve §14
                // uyum kaydının kaynağı o.
                return Result.Success(new ProviderResponse<GeneratedImage>(
                    downloaded.Value with { License = candidate.License },
                    new UsageUnits { Images = 1 }));
            }
        }

        // Buraya düşmenin üç sebebi olabilir ve üçü de üretime gider:
        //   1. stok sağlayıcısı hata verdi
        //   2. sonuç döndü ama hepsi çok küçük
        //   3. hiç sonuç yok (soyut sahne — en yaygın durum)
        LastRoute = found.IsFailure ? "generative:stock_error" : "generative:no_match";

        var generated = await generative
            .GenerateAsync(prompt, context, cancellationToken)
            .ConfigureAwait(false);

        if (generated.IsFailure && found.IsFailure)
        {
            // İKİSİ DE düştü. Üretimin hatası dönüyor çünkü son deneme o,
            // ama stok hatası da mesaja giriyor: yalnızca sonuncuyu
            // vermek yanlış teşhise yol açardı.
            return Result.Failure<ProviderResponse<GeneratedImage>>(
                generated.Error with
                {
                    Detail = $"{generated.Error.Detail}\nStok da basarisiz: {found.Error}",
                });
        }

        return generated;
    }

    /// Uzun AI isteminden stok araması için kısa terim çıkarır.
    ///
    /// İstem "Göbeklitepe: tapınak, dikilitaş. cinematic documentary
    /// photography… no text" gibi geliyor; stok aramasına bunun tamamını
    /// vermek sıfır sonuç demek. İlk cümle alınıyor, üslup ve olumsuz
    /// yönergeler atılıyor.
    internal static string ShortTerms(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var stop = prompt.IndexOf('.', StringComparison.Ordinal);
        var head = stop > 0 ? prompt[..stop] : prompt;

        // "Konu: terim, terim" biçimindeki iki nokta üstündeki konuyu
        // da atıyoruz: terimler zaten konudan türedi ve ikisini birden
        // aramak sonucu daraltıyor.
        var colon = head.IndexOf(':', StringComparison.Ordinal);

        if (colon > 0 && colon < head.Length - 1)
        {
            head = head[(colon + 1)..];
        }

        return head.Trim().Length == 0 ? prompt.Trim() : head.Trim();
    }

    /// Basit HTTP indirici — çoğu kullanım için yeterli.
    ///
    /// Ayrı bir işlev olarak veriliyor ki testler ağa çıkmasın; ADR-007
    /// gereği indirme yan etkili bir iş ve saf katmandan uzak tutuluyor.
    public static Func<Uri, CancellationToken, Task<Result<GeneratedImage>>> HttpDownloader(
        HttpClient http, string userAgent = "BytemountsAiStudio/0.1")
    {
        ArgumentNullException.ThrowIfNull(http);

        return async (url, cancellationToken) =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(userAgent);

                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return Error.Transient("stock.download_failed",
                        string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode}"));
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

                if (bytes.Length < 1024 || !mime.StartsWith("image/", StringComparison.Ordinal))
                {
                    return Error.Transient("stock.not_image",
                        string.Create(CultureInfo.InvariantCulture, $"{bytes.Length} bayt, tip {mime}"));
                }

                return Result.Success(new GeneratedImage
                {
                    Data = bytes,
                    MimeType = mime,
                    // Gerçek ölçüler adaydan geliyor; burada yalnızca
                    // baytlar var ve dosyayı çözmek gereksiz iş.
                    Width = 0,
                    Height = 0,
                    License = new LicenseInfo
                    {
                        Name = "bilinmiyor",
                        RequiresAttribution = true,
                        CapturedAt = DateTimeOffset.UtcNow,
                    },
                });
            }
            catch (HttpRequestException ex)
            {
                return Error.Transient("stock.unreachable", ex.Message);
            }
        };
    }
}
