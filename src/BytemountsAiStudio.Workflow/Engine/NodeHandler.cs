using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BytemountsAiStudio.Core;

namespace BytemountsAiStudio.Workflow.Engine;

/// Bir node çalıştırmasının bağlamı.
public sealed record NodeContext
{
    public required Guid RunId { get; init; }

    public required string NodeId { get; init; }

    public required string NodeType { get; init; }

    public required int Attempt { get; init; }

    /// Node'un kendi ayarları (workflow tanımından).
    public required JsonElement Config { get; init; }

    /// Şimdiye kadarki tüm node çıktıları: `{"script": {...}, "tts": {...}}`.
    /// Node'lar birbirine bu belge üzerinden bağlanır.
    public required JsonElement RunContext { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string CorrelationId { get; init; }

    /// Bu run hangi kanala ait.
    ///
    /// `null` = kanalsız koşu (CLI denemesi, bakım). Node'lar bunu
    /// kanala özel ayarları okumak için kullanıyor: onay modu, ses,
    /// bütçe.
    ///
    /// EKSİKLİĞİ SESSİZ BİR HATAYA YOL AÇTI: `channels.mode` kolonu
    /// vardı, tohumlanıyordu, panelde görünüyordu ve HİÇBİR ŞEY onu
    /// okumuyordu. Onay kapısı modu yalnızca node ayarından alıyordu,
    /// yani bir kanalı "seçici onay"a almak hiçbir işe yaramıyor,
    /// her video insana gidiyordu — Faz 2'nin kabul kriteri tam
    /// buradan kırılıyordu.
    public Guid? ChannelId { get; init; }
}

/// Bir node tipini çalıştıran işleyici.
///
/// KRİTİK KURAL (§6.1): iş mantığı BURADA DEĞİL, domain servisinde. İşleyici
/// ince bir adaptördür — konfigürasyonu okur, domain servisini çağırır,
/// sonucu JSON'a çevirir. Bu kural gevşerse workflow motoru zamanla
/// uygulamanın kendisi hâline gelir ve hiçbir şey ondan bağımsız test edilemez.
public interface INodeHandler
{
    /// "script.generate", "media.render"…
    string NodeType { get; }

    /// Hangi kuyrukta çalışacağı. 2 saniyelik LLM çağrısıyla 25 dakikalık
    /// render aynı havuzda olamaz (§8.1).
    Core.Execution.QueueClass Queue { get; }

    Task<Result<JsonElement>> ExecuteAsync(NodeContext context, CancellationToken cancellationToken);
}

/// Node tipi → işleyici eşlemesi.
public sealed class NodeRegistry
{
    private readonly Dictionary<string, INodeHandler> _handlers = new(StringComparer.Ordinal);

    public NodeRegistry Register(INodeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.NodeType] = handler;
        return this;
    }

    public INodeHandler? Find(string nodeType)
        => _handlers.GetValueOrDefault(nodeType);

    /// Workflow doğrulaması bilinen tipleri buradan alıyor: kayıtlı olmayan
    /// bir tipe sahip graf hiç kaydedilemiyor.
    public IReadOnlySet<string> KnownTypes => _handlers.Keys.ToHashSet(StringComparer.Ordinal);
}

/// Idempotency anahtarı üretimi (ADR-010, §6.5).
///
/// `sha256(run_id | node_id | config | input)`. Deneme sayısı KASITLI OLARAK
/// dahil değil: retry'ın aynı anahtarı üretmesi gerekiyor, yoksa sağlayıcı
/// katmanı önceki başarılı sonucu tanıyamaz ve ikinci kez para harcanır.
public static class IdempotencyKey
{
    public static string Compute(Guid runId, string nodeId, JsonElement config, JsonElement input)
    {
        var payload = string.Join('|',
            runId.ToString("N"),
            nodeId,
            Canonical(config),
            Canonical(input));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..32];
    }

    /// JSON'u kanonik hâle getirir: alan sırası anahtarı değiştirmemeli.
    /// Aksi hâlde aynı konfigürasyon farklı serileştirmelerde farklı anahtar
    /// üretir ve idempotency sessizce çalışmaz.
    private static string Canonical(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "null";
        }

        var builder = new StringBuilder();
        Write(element, builder);
        return builder.ToString();
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(property.Name).Append(':');
                    Write(property.Value, builder);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    Write(item, builder);
                }

                builder.Append(']');
                break;

            default:
                builder.Append(element.ToString());
                break;
        }
    }
}
