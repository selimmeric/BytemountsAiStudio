using BytemountsAiStudio.Core;

namespace BytemountsAiStudio.Contracts.Providers;

/// Model katmanı. Ajanlar modele değil KATMANA bağlanır (mimari §7.4).
///
/// Böylece "senaryo güçlü modelle, skorlama ucuz modelle" kararı kanal
/// ayarında tek satırdır ve sağlayıcı değişimi ajan kodunu etkilemez.
public enum ModelTier
{
    /// Hacimli ve basit işler: skorlama, sınıflandırma, çıkarım.
    /// ADR-015 gereği varsayılanı yerel model (Ollama).
    Cheap = 0,

    /// Araştırma planı, sahne planı, SEO.
    Standard = 1,

    /// Yalnızca senaryo. Video başına 1–2 çağrı — para burada harcanır.
    Strong = 2,
}

public enum ChatRole
{
    System = 0,
    User = 1,
    Assistant = 2,
}

public sealed record ChatMessage(ChatRole Role, string Content);

/// Modelin çağırmaya ZORLANACAĞI araç.
///
/// §7.2: model serbest metin yerine şemalı bir araç çağırır; cevap
/// ayrıştırmaya değil doğrulamaya tabi olur. Bu, Bytemounts-Studio'nun
/// metadata üretiminde işe yaramış deseni ev standardına çeviriyor.
public sealed record ToolSchema(string Name, string Description, string JsonSchema);

public sealed record LlmRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public required ModelTier Tier { get; init; }

    /// Verilirse model bu aracı çağırmak zorunda; cevap `ToolArguments` olarak döner.
    public ToolSchema? ForcedTool { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// 0 = mümkün olduğunca kararlı. Şema doldurma işlerinde düşük tutulur.
    public double Temperature { get; init; }

    /// Aynı girdiye aynı çıktı isteyen sağlayıcılar için. Desteklemeyen
    /// sağlayıcılarda yok sayılır — determinizm garanti değil, tercihtir.
    public int? Seed { get; init; }
}

public sealed record LlmResponse
{
    /// Serbest metin cevabı. `ForcedTool` verilmişse null olur.
    public string? Text { get; init; }

    /// Zorunlu araç çağrısının argümanları (ham JSON). Doğrulama çağıran tarafta.
    public string? ToolArguments { get; init; }

    public required string ModelId { get; init; }

    /// Model çıktıyı kendi kestiyse true. Sessizce kısalmış senaryoyu
    /// "başarılı" saymamak için gerekli.
    public bool Truncated { get; init; }
}

public sealed record LlmCapabilities
{
    public required bool SupportsToolUse { get; init; }

    public required bool SupportsVision { get; init; }

    public required int ContextWindowTokens { get; init; }

    /// Embedding üretebiliyor mu. Konu tekilliği buna bağlı (ADR-003).
    public required bool SupportsEmbeddings { get; init; }
}

public interface ILlmProvider : IProvider
{
    LlmCapabilities Capabilities { get; }

    Task<Result<ProviderResponse<LlmResponse>>> CompleteAsync(
        LlmRequest request,
        ProviderContext context,
        CancellationToken cancellationToken);

    /// Çok dilli embedding zorunlu: TR ve EN vektörleri aynı uzayda olmalı,
    /// yoksa diller arası tekillik karşılaştırması anlamsızlaşır (§20.5).
    Task<Result<ProviderResponse<IReadOnlyList<float>>>> EmbedAsync(
        string text,
        ProviderContext context,
        CancellationToken cancellationToken);
}
