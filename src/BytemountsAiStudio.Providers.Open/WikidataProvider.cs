using System.Globalization;
using System.Text.Json;
using BytemountsAiStudio.Contracts.Providers;
using BytemountsAiStudio.Core;
using BytemountsAiStudio.Core.Content;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Providers.Open;

/// Wikidata: YAPILANDIRILMIŞ olgular (P1-05).
///
/// Wikipedia'dan farkı, ve bu farkın neden önemli olduğu: Wikipedia
/// metin veriyor, model o metinden tarih ve sayı ÇIKARIYOR ve bu
/// çıkarım hata yapabiliyor. Wikidata ise tarihi bir alan olarak
/// veriyor — çıkarım yok, dolayısıyla o adımın hatası da yok.
///
/// "Göbeklitepe kaç yılında inşa edildi" sorusunun cevabı burada
/// `P571 = -9999` olarak duruyor. Modelin bunu metinden okumasına gerek
/// kalmıyor; sayılar ve tarihler kısa videoda en çok yanlış çıkan ve
/// yanlışlığı en görünür olan şeyler.
///
/// Anahtar gerektirmiyor. Tanımlayıcı User-Agent zorunlu.
public sealed class WikidataProvider(HttpClient http, Uri? endpoint = null) : ISearchProvider
{
    /// Varsayılan adres — `config/providers.json` ile AYNI olmak
    /// zorunda; `ProviderEndpointTests` ikisini karşılaştırıyor.
    ///
    /// Kodda sabit DEĞİL, VARSAYILAN: `BMAI_WIKIDATA_URL` ile eziliyor.
    public static Uri DefaultEndpoint { get; } = new("https://www.wikidata.org/w/api.php");

    /// Adresin okunduğu ortam değişkeni.
    public const string EndpointVariable = "BMAI_WIKIDATA_URL";

    private readonly string Endpoint =
        (endpoint ?? Endpoints.Resolve(EndpointVariable, "https://www.wikidata.org/w/api.php")).ToString();

    /// Wikimedia'nın açıkça istediği tanımlayıcı ajan. Vermemek
    /// engellenme sebebi.
    private const string UserAgent =
        "BytemountsAiStudio/0.1 (icerik arastirma; +https://github.com/selimmeric/BytemountsAiStudio)";

    /// İçerik üretiminde işe yarayan özellikler.
    ///
    /// Wikidata bir varlıkta 50'den fazla özellik tutabiliyor; hepsini
    /// isteme koymak bağlamı doldurup asıl bilgiyi gömerdi. Liste
    /// KISA ve seçilmiş: bir kısa videoda geçebilecek olgular.
    ///
    /// Özellik ADLARI koda gömülü. Etiketlerini Wikidata'dan çekmek her
    /// sorguda ikinci bir tur demekti; oysa bu tanımlayıcılar sabit ve
    /// çevirileri değişmiyor.
    private static readonly Dictionary<string, PropertyLabel> Interesting =
        new(StringComparer.Ordinal)
        {
            ["P31"] = new("türü", "instance of"),
            ["P17"] = new("ülke", "country"),
            ["P571"] = new("kuruluş/inşa tarihi", "inception"),
            ["P576"] = new("sona erme tarihi", "dissolved"),
            ["P625"] = new("koordinatlar", "coordinates"),
            ["P131"] = new("bulunduğu idari birim", "located in"),
            ["P2044"] = new("rakım", "elevation"),
            ["P2046"] = new("alan", "area"),
            ["P1082"] = new("nüfus", "population"),
            ["P569"] = new("doğum tarihi", "date of birth"),
            ["P570"] = new("ölüm tarihi", "date of death"),
            ["P106"] = new("meslek", "occupation"),
            ["P27"] = new("vatandaşlık", "citizenship"),
            ["P61"] = new("kâşif", "discoverer"),
            ["P170"] = new("yaratıcı", "creator"),
            ["P1435"] = new("koruma statüsü", "heritage status"),
            ["P580"] = new("başlangıç", "start time"),
            ["P582"] = new("bitiş", "end time"),
        };

    public string Key => "wikidata";

    public async Task<Result<ProviderResponse<IReadOnlyList<SearchHit>>>> SearchAsync(
        SearchQuery query, ProviderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var language = query.Language?.Primary ?? "en";

        var url = new Uri(
            $"{Endpoint}?action=wbsearchentities&format=json"
            + $"&search={Uri.EscapeDataString(query.Text)}"
            + $"&language={language}&uselang={language}"
            + $"&limit={Math.Clamp(query.MaxResults, 1, 50).ToString(CultureInfo.InvariantCulture)}");

        var document = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (document.IsFailure)
        {
            return Result.Failure<ProviderResponse<IReadOnlyList<SearchHit>>>(document.Error);
        }

        using var json = document.Value;

        if (!json.RootElement.TryGetProperty("search", out var results))
        {
            return Result.Success(new ProviderResponse<IReadOnlyList<SearchHit>>([], new UsageUnits()));
        }

        var hits = new List<SearchHit>();
        var rank = 0;

        foreach (var element in results.EnumerateArray())
        {
            if (!element.TryGetProperty("id", out var id) || id.GetString() is not { } entityId)
            {
                continue;
            }

            hits.Add(new SearchHit
            {
                Url = new Uri($"https://www.wikidata.org/wiki/{entityId}"),
                Title = element.TryGetProperty("label", out var label)
                    ? label.GetString() ?? entityId
                    : entityId,
                Snippet = element.TryGetProperty("description", out var description)
                    ? description.GetString()
                    : null,
                SourceType = SourceType.Encyclopedia,
                Rank = rank++,
            });
        }

        return Result.Success(new ProviderResponse<IReadOnlyList<SearchHit>>(hits, new UsageUnits()));
    }

    /// Bir varlığın seçilmiş olguları, insan okunur hâlde.
    ///
    /// Öğe referansları (`Q43` gibi) TEK bir ek çağrıyla çözülüyor.
    /// Çözmeseydik modele "ülke: Q43" giderdi ve bu hiçbir işe yaramaz;
    /// her biri için ayrı çağrı yapsaydık bir varlık on istek ederdi.
    public async Task<Result<IReadOnlyList<WikidataFact>>> FactsAsync(
        string entityId, LanguageTag language, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var url = new Uri(
            $"{Endpoint}?action=wbgetentities&format=json&props=labels%7Cdescriptions%7Cclaims"
            + $"&ids={Uri.EscapeDataString(entityId)}"
            + $"&languages={language.Primary}%7Cen");

        var document = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        if (document.IsFailure)
        {
            return Result.Failure<IReadOnlyList<WikidataFact>>(document.Error);
        }

        using var json = document.Value;

        if (!json.RootElement.TryGetProperty("entities", out var entities)
            || !entities.TryGetProperty(entityId, out var entity)
            || !entity.TryGetProperty("claims", out var claims))
        {
            return Error.Permanent("wikidata.not_found", $"'{entityId}' bulunamadi.");
        }

        var raw = new List<(string Property, JsonElement Snak)>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in claims.EnumerateObject())
        {
            if (!Interesting.ContainsKey(property.Name))
            {
                continue;
            }

            // Bir özelliğin birden çok değeri olabiliyor (bir kişinin
            // iki mesleği gibi). İlki alınıyor: Wikidata sıralamayı
            // tercih edilene göre yapıyor ve hepsini almak modele
            // gereksiz uzunlukta bir liste verirdi.
            var first = property.Value.EnumerateArray().FirstOrDefault();

            if (first.ValueKind != JsonValueKind.Object
                || !first.TryGetProperty("mainsnak", out var snak))
            {
                continue;
            }

            raw.Add((property.Name, snak));

            if (ItemReference(snak) is { } reference)
            {
                referenced.Add(reference);
            }
        }

        var labels = await ResolveLabelsAsync(referenced, language, cancellationToken).ConfigureAwait(false);
        var facts = new List<WikidataFact>(raw.Count);

        foreach (var (property, snak) in raw)
        {
            var value = Format(snak, labels, language);

            if (value is not null)
            {
                facts.Add(new WikidataFact(property, Interesting[property].For(language), value));
            }
        }

        return Result.Success<IReadOnlyList<WikidataFact>>(facts);
    }

    /// Öğe kimliklerini etikete çevirir — hepsini TEK istekte.
    private async Task<IReadOnlyDictionary<string, string>> ResolveLabelsAsync(
        HashSet<string> ids, LanguageTag language, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // API tek çağrıda en fazla 50 kimlik kabul ediyor. Seçilmiş
        // özellik listemiz zaten bunun çok altında, ama sınırı burada
        // uygulamak ileride liste büyüdüğünde sessiz bir hata olmasını
        // engelliyor.
        var batch = ids.Take(50).ToList();

        var url = new Uri(
            $"{Endpoint}?action=wbgetentities&format=json&props=labels"
            + $"&ids={Uri.EscapeDataString(string.Join('|', batch))}"
            + $"&languages={language.Primary}%7Cen");

        var document = await GetAsync(url, cancellationToken).ConfigureAwait(false);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        if (document.IsFailure)
        {
            // Etiket çözülemezse olgular yine de dönüyor, ham kimlikle.
            // Bir ek isteğin başarısız olması bütün araştırmayı
            // düşürmemeli.
            return labels;
        }

        using var json = document.Value;

        if (!json.RootElement.TryGetProperty("entities", out var entities))
        {
            return labels;
        }

        foreach (var entity in entities.EnumerateObject())
        {
            if (!entity.Value.TryGetProperty("labels", out var entityLabels))
            {
                continue;
            }

            var label = Pick(entityLabels, language.Primary) ?? Pick(entityLabels, "en");

            if (label is not null)
            {
                labels[entity.Name] = label;
            }
        }

        return labels;
    }

    private static string? Pick(JsonElement labels, string language)
        => labels.TryGetProperty(language, out var entry)
           && entry.TryGetProperty("value", out var value)
            ? value.GetString()
            : null;

    private static string? ItemReference(JsonElement snak)
        => snak.TryGetProperty("datatype", out var type)
           && type.GetString() == "wikibase-item"
           && snak.TryGetProperty("datavalue", out var data)
           && data.TryGetProperty("value", out var value)
           && value.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;

    /// Bir değeri insan okunur hâle getirir.
    ///
    /// Desteklenmeyen tipler null dönüyor ve olgu listeye girmiyor:
    /// modele ham JSON vermek, hiçbir şey vermemekten kötü.
    private static string? Format(
        JsonElement snak, IReadOnlyDictionary<string, string> labels, LanguageTag language)
    {
        if (!snak.TryGetProperty("datavalue", out var data)
            || !data.TryGetProperty("value", out var value))
        {
            return null;
        }

        return snak.TryGetProperty("datatype", out var typeJson) ? typeJson.GetString() switch
        {
            "wikibase-item" => value.TryGetProperty("id", out var id) && id.GetString() is { } key
                ? labels.GetValueOrDefault(key, key)
                : null,

            "time" => FormatTime(value, language),

            "globe-coordinate" => value.TryGetProperty("latitude", out var lat)
                                  && value.TryGetProperty("longitude", out var lon)
                ? FormattableString.Invariant($"{lat.GetDouble():0.####}, {lon.GetDouble():0.####}")
                : null,

            "quantity" => value.TryGetProperty("amount", out var amount)
                ? amount.GetString()?.TrimStart('+')
                : null,

            "string" or "external-id" or "url" => value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null,

            "monolingualtext" => value.TryGetProperty("text", out var text) ? text.GetString() : null,

            _ => null,
        } : null;
    }

    /// Wikidata tarihleri ISO benzeri ama TAM DEĞİL: yıl negatif
    /// olabiliyor (MÖ) ve `precision` alanı ay/gün alanlarının anlamlı
    /// olup olmadığını söylüyor. Ham hâliyle vermek "-9999-01-01" gibi
    /// bir dizge üretir ve model bunu "1 Ocak" diye okur — oysa gün
    /// bilgisi YOK, yalnızca dolgu.
    private static string? FormatTime(JsonElement value, LanguageTag language)
    {
        if (!value.TryGetProperty("time", out var timeJson) || timeJson.GetString() is not { } time)
        {
            return null;
        }

        var precision = value.TryGetProperty("precision", out var p) ? p.GetInt32() : 11;
        var isBce = time.StartsWith('-');

        // Saat kismi ATILIYOR. Wikidata her tarihi tam ISO damgasi gibi
        // donduruyor ("-0011-11-10T00:00:00Z") ama saat bilgisi yok,
        // dolgu. Atmadan bolunce gun alani "10T00:00:00Z" cikiyordu -
        // canli sorguda gorulen bir hata.
        var body = time.TrimStart('+', '-').Split('T')[0];
        var parts = body.Split('-');

        if (parts.Length == 0 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        var turkish = language.Primary.Equals("tr", StringComparison.OrdinalIgnoreCase);
        var era = isBce ? (turkish ? " MÖ" : " BCE") : string.Empty;

        // precision: 6=binyıl, 7=yüzyıl, 8=on yıl, 9=yıl, 10=ay, 11=gün
        // Gun ve ay AYRI ele aliniyor.
        //
        // Tek dalda toplamak, gun hassasiyetli bir tarihin gununu
        // DUSURUYORDU: Ataturk'un olum tarihi "11/1938" cikiyordu, oysa
        // kayitta 10 Kasim 1938 var. Canli sorguda gorulen bir hata.
        return precision switch
        {
            <= 6 => FormattableString.Invariant($"~{year}{era}"),
            7 => Century(year, era, turkish),
            8 => turkish
                ? FormattableString.Invariant($"{year / 10 * 10}'lar{era}")
                : FormattableString.Invariant($"{year / 10 * 10}s{era}"),
            9 => FormattableString.Invariant($"{year}{era}"),
            10 when parts.Length >= 2 => FormattableString.Invariant($"{parts[1]}/{year}{era}"),
            >= 11 when parts.Length >= 3 =>
                FormattableString.Invariant($"{parts[2]}/{parts[1]}/{year}{era}"),
            _ when parts.Length >= 2 => FormattableString.Invariant($"{parts[1]}/{year}{era}"),
            _ => FormattableString.Invariant($"{year}{era}"),
        };
    }

    private static string Century(int year, string era, bool turkish)
    {
        var century = year / 100 + 1;

        return turkish
            ? FormattableString.Invariant($"{century}. yüzyıl{era}")
            : FormattableString.Invariant($"{century}{OrdinalSuffix(century)} century{era}");
    }

    /// Ingilizce sira eki: `2th` degil `2nd`. Canli sorguda gorulen bir
    /// hata; Turkce tarafta "2. yuzyil" dogru oldugu icin fark
    /// edilmiyordu.
    private static string OrdinalSuffix(int value)
        => (value % 100) is 11 or 12 or 13
            ? "th"
            : (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    private async Task<Result<JsonDocument>> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;

                return status is 429 or >= 500
                    ? Error.Transient("wikidata.unavailable", $"HTTP {status}")
                    : Error.Permanent("wikidata.rejected", $"HTTP {status}");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success(await JsonDocument
                .ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            return Error.Transient("wikidata.unreachable", ex.Message);
        }
        catch (JsonException ex)
        {
            return Error.Transient("wikidata.bad_json", ex.Message);
        }
    }

    private sealed record PropertyLabel(string Turkish, string English)
    {
        public string For(LanguageTag language)
            => language.Primary.Equals("tr", StringComparison.OrdinalIgnoreCase) ? Turkish : English;
    }
}

/// Wikidata'dan gelen tek bir olgu.
///
/// `PropertyId` saklanıyor çünkü etiket dile göre değişiyor ama
/// tanımlayıcı değişmiyor: "bu olgu nereden geldi" sorusunun kalıcı
/// cevabı o.
public sealed record WikidataFact(string PropertyId, string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}
