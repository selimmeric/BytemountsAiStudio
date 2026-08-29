using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Llm;

/// OpenAI uyumlu görme modeli (P2-06).
///
/// ***BU SINIF, "ÖLÇÜLEMEYEN KONTROL GEÇMİŞ SAYILMIYOR" KURALININ
/// ÖDEDİĞİ BEDELİ KAPATIYOR.***
///
/// Semantik QC yazılmış ve doğru davranıyordu: görme modeli yokken
/// kontroller "ölçülemedi" diye DÜŞÜYOR ve video onay kapısından
/// insana gidiyordu. Doğru ama pahalı — hiçbir video otomatik
/// geçemiyordu, yani otonomi bu kontrolde duruyordu. Sebep de
/// yazılıydı: 6 GB'lık bir görme modelini karta yüklemek bu makinede
/// mümkün değil.
///
/// Anahtarsız bir görme modeli (Pollinations) bunu GPU'suz ve ücretsiz
/// çözüyor.
///
/// ***AYRI SINIF, `OpenAiCompatibleLlmProvider`'A EKLENMEDİ.*** Kablo
/// aynı ama `IVisionProvider` ayrı bir arayüz ve ayrılığın gerekçesi
/// o arayüzde yazılı: görme modeli hattın en yavaş adımı ve metin
/// modeli çalışırken kapalı olabilmeli. Tek sınıfta birleştirmek, metin
/// çağrısı yapan her yerin görme yeteneğini de varsayması demekti.
public sealed class OpenAiCompatibleVisionProvider(
    HttpClient http, OpenAiCompatibleOptions options, ICredentialSource? credentials = null)
    : IVisionProvider
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// Modelden istenen çıktı şeması.
    ///
    /// ***ZORLANMIŞ ARAÇ KULLANILMIYOR ve bu bilinçli:*** ücretsiz
    /// görme uçlarının çoğu `tool_choice` desteklemiyor ve
    /// desteklemeyende istek tümden reddediliyor. İstemde biçim
    /// dayatılıyor, cevap ayrıştırılamazsa GEÇİCİ hata dönüyor —
    /// ikinci deneme genellikle geçerli çıkıyor.
    private const string Instruction =
        "Bu görselin, altında duyulan cümleyi DESTEKLEYIP desteklemediğini değerlendir. "
        + "YALNIZCA şu biçimde JSON döndür, başka hiçbir şey yazma: "
        + "{\"relevance\": 0.0-1.0, \"reason\": \"kısa gerekçe\", \"description\": \"karede ne var\"}. "
        + "relevance 1.0 = görsel cümleyi doğrudan gösteriyor, "
        + "0.0 = görsel cümleyle hiç ilgisiz.";

    public string Key => options.Key;

    public async Task<Result<ProviderResponse<VisionVerdict>>> JudgeAsync(
        VisionQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (options.VisionModel is not { Length: > 0 } model)
        {
            // KALICI: yeniden denemek bu sağlayıcıya görme yeteneği
            // kazandırmıyor.
            return Error.Permanent($"{options.Key}.no_vision",
                $"{options.Key} görme modeli sunmuyor; yönlendirme başka bir sağlayıcıya gitmeli.");
        }

        var key = ResolveKey();

        if (key.IsFailure)
        {
            return Result.Failure<ProviderResponse<VisionVerdict>>(key.Error);
        }

        // ***GÖRSEL `data:` URI OLARAK GÖNDERİLİYOR, URL OLARAK
        // DEĞİL.*** Karelerin bir kısmı üretilmiş görseller ve hiçbir
        // yerde barındırılmıyor; bir URL vermek, önce yüklemek demekti.
        // Bedeli base64'ün %33 şişmesi ve bu kabul edilebilir: kare
        // örneklemeli, videonun tamamı gönderilmiyor.
        var dataUri = "data:" + Mime(query.Image.Span) + ";base64,"
            + Convert.ToBase64String(query.Image.Span);

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = Instruction + "\n\nCümle: " + query.Sentence },
                        new { type = "image_url", image_url = new { url = dataUri } },
                    },
                },
            },

            // SICAKLIK SIFIR: aynı kare ve aynı cümle için aynı yargı.
            // Değişken bir yargı, QC eşiğini ayarlamayı imkânsız
            // kılardı — geçen bir video ikinci koşuda düşerdi.
            ["temperature"] = 0.0,
            ["max_tokens"] = 300,
        };

        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(options.Timeout);

        using var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(options.BaseAddress, "chat/completions"))
        {
            Content = System.Net.Http.Json.JsonContent.Create(body, options: Json),
        };

        if (key.Value is { Length: > 0 } token)
        {
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", token);
        }

        foreach (var (name, value) in options.ExtraHeaders)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await http.SendAsync(message, source.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(source.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Classify(response.StatusCode, text);
            }

            return Parse(text, model);
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient($"{options.Key}.network", ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Transient($"{options.Key}.timeout",
                $"Görme modeli {options.Timeout.TotalSeconds:0} saniyede cevap vermedi.");
        }
    }

    /// Cevabı ayrıştırır.
    ///
    /// ***MODEL JSON'U METNİN İÇİNE GÖMEBİLİYOR*** (```json blokları,
    /// "İşte değerlendirme:" gibi önsözler). İlk `{` ile son `}`
    /// arasını almak, "biçime uy" diye ısrar etmekten daha dayanıklı:
    /// ücretsiz uçlar biçim talimatını sık sık kısmen uyguluyor ve
    /// her seferinde geçici hata dönmek, her karenin iki kez
    /// ölçülmesi demekti.
    private Result<ProviderResponse<VisionVerdict>> Parse(string raw, string model)
    {
        string? content;

        try
        {
            using var document = JsonDocument.Parse(raw);

            content = document.RootElement.TryGetProperty("choices", out var choices)
                      && choices.ValueKind == JsonValueKind.Array
                      && choices.GetArrayLength() > 0
                      && choices[0].TryGetProperty("message", out var msg)
                      && msg.TryGetProperty("content", out var c)
                ? c.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            return Error.Transient($"{options.Key}.bad_response", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Error.Transient($"{options.Key}.empty", "Görme modeli boş cevap döndürdü.");
        }

        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return Error.Transient($"{options.Key}.no_json",
                "Görme modeli JSON döndürmedi: " + Trim(content));
        }

        try
        {
            using var verdict = JsonDocument.Parse(content[start..(end + 1)]);
            var root = verdict.RootElement;

            if (!root.TryGetProperty("relevance", out var relevance)
                || !relevance.TryGetDouble(out var score))
            {
                return Error.Transient($"{options.Key}.no_relevance",
                    "Cevapta `relevance` yok: " + Trim(content));
            }

            return Result.Success(new ProviderResponse<VisionVerdict>(
                new VisionVerdict
                {
                    // ***SKOR SINIRA ÇEKİLİYOR.*** Model bazen 0–100
                    // ölçeğinde cevap veriyor; 85'i "çok alakalı"
                    // saymak yerine 1,0'a çekmek, eşiğin anlamını
                    // korumanın tek yolu. Ölçek karışıklığı sessizce
                    // geçseydi her kare "alakalı" çıkardı.
                    Relevance = Math.Clamp(score > 1.0 ? score / 100.0 : score, 0.0, 1.0),
                    Reason = Text(root, "reason"),
                    Description = Text(root, "description"),
                },
                new UsageUnits { Images = 1, Requests = 1 }));
        }
        catch (JsonException ex)
        {
            return Error.Transient($"{options.Key}.bad_verdict", ex.Message);
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Trim(string text)
        => text.Length <= 200 ? text : text[..200] + "…";

    /// Baytlardan MIME türü.
    ///
    /// UZANTIYA DEĞİL İÇERİĞE bakılıyor: `VisionQuery` yalnızca bayt
    /// taşıyor ve yanlış bir MIME, modelin görseli hiç açamaması
    /// demek.
    private static string Mime(ReadOnlySpan<byte> data)
        => data.Length >= 8
           && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            ? "image/png"
            : "image/jpeg";

    private Result<ProviderResponse<VisionVerdict>> Classify(
        System.Net.HttpStatusCode status, string body)
        => status switch
        {
            // 429 ve 5xx GEÇİCİ: ücretsiz uçta sınıra takılmak normal
            // ve iş düşmemeli.
            System.Net.HttpStatusCode.TooManyRequests
                => Error.Resource($"{options.Key}.rate_limited",
                    "Görme modeli hız sınırı: " + Trim(body), TimeSpan.FromMinutes(1)),

            >= System.Net.HttpStatusCode.InternalServerError
                => Error.Transient($"{options.Key}.server", Trim(body)),

            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                => Error.Permanent($"{options.Key}.unauthorized", Trim(body)),

            _ => Error.Permanent(
                string.Create(CultureInfo.InvariantCulture, $"{options.Key}.http_{(int)status}"),
                Trim(body)),
        };

    private Result<string?> ResolveKey()
    {
        var value = credentials is not null
            ? credentials.Get(options.KeyEnvironmentVariable)
            : Environment.GetEnvironmentVariable(options.KeyEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(value))
        {
            return Result.Success<string?>(value);
        }

        return options.KeyRequired
            ? Error.Permanent($"{options.Key}.no_key",
                $"{options.Key} için anahtar yok ({options.KeyEnvironmentVariable} tanımlı değil).")
            : Result.Success<string?>(null);
    }
}
