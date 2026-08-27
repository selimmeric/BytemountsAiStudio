using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Api;

/// API sözleşmeleri (P1-28).
///
/// Entity'ler DOĞRUDAN dönülmüyor. İki sebep:
///   - Entity'de olan ama dışarı çıkmaması gereken alanlar var
///     (şifreli kimlik bilgileri, ham bağlam belgesi).
///   - Entity bir veritabanı şeması; API bir sözleşme. Birini
///     değiştirmek diğerini kırmamalı — yoksa her kolon ekleme bir
///     istemci güncellemesi gerektirirdi.
internal sealed record RunSummary(
    Guid Id,
    RunState State,
    Guid? ChannelId,
    Guid? TopicId,
    decimal ActualCost,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int NodeCount,
    int FailedNodes);

/// Bir node'un çalışma kaydı — zaman çizelgesinin satırı.
internal sealed record NodeTimelineEntry(
    string NodeId,
    string NodeType,
    NodeState State,
    int Attempt,
    int DurationMs,
    DateTimeOffset StartedAt,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed record RunEventEntry(
    DateTimeOffset At,
    string Level,
    string? NodeId,
    string Message);

/// Sağlayıcı başına maliyet. "Bu video neden pahalı" sorusunun cevabı.
internal sealed record ProviderCostEntry(
    string ProviderKey,
    string Operation,
    int Calls,
    decimal Cost,
    int TotalLatencyMs,
    int Failures);

/// "Bu video neden böyle oldu" sorusunun cevabı tek yerde (P1-29'un
/// kabul kriteri): zaman çizelgesi + loglar + maliyet.
internal sealed record RunDetail(
    RunSummary Run,
    IReadOnlyList<NodeTimelineEntry> Timeline,
    IReadOnlyList<RunEventEntry> Events,
    IReadOnlyList<ProviderCostEntry> Costs);

internal sealed record TopicSummary(
    Guid Id,
    string Title,
    string Language,
    double OverallScore,
    string State,
    Guid? ChannelId,
    DateTimeOffset CreatedAt);

internal sealed record ApprovalSummary(
    Guid Id,
    Guid RunId,
    string NodeId,
    string Reason,
    DateTimeOffset RequestedAt,
    Guid? ChannelId);

internal sealed record ApprovalDecisionRequest(string DecidedBy, string? Note);

/// Maliyet özeti. Video başına maliyet ÖLÇÜLEN çağrılardan geliyor,
/// tahminden değil (ADR-006'nın maliyet karşılığı).
internal sealed record CostSummary(
    decimal TotalCost,
    int RunCount,
    decimal AveragePerRun,
    IReadOnlyList<ProviderCostEntry> ByProvider);

/// SSE ile yayılan ilerleme.
///
/// Panonun yenilemeden ilerleme görmesi için gereken en küçük belge.
/// Tam `RunDetail` göndermek, her adımda bütün logları tekrar
/// göndermek olurdu.
internal sealed record RunProgress(
    Guid RunId,
    RunState State,
    int Completed,
    int Failed,
    int Pending,
    string? CurrentNode,
    decimal Cost);

/// Ölü mektup kuyruğundaki bir iş.
///
/// DLQ panelde ayrı bir bölüm çünkü ayrı bir soru: "hangi işler
/// kalıcı olarak düştü ve neden". Run listesinde göstermek, bu
/// soruyu binlerce başarılı koşunun arasına gömerdi.
internal sealed record DeadLetterEntry(
    Guid Id,
    string Queue,
    Guid? RunId,
    string? NodeId,
    int Attempt,
    int MaxAttempts,
    string? LastError,
    DateTimeOffset CreatedAt);
