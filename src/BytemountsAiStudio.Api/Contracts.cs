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

/// Bir sağlayıcının GÖZLENEN sağlığı (P2-04).
///
/// Devre kesicinin kendi durumu SÜREÇ İÇİ ve öyle kalıyor: bayrağı her
/// çağrıda veritabanına yazmak, para harcamayan bir kontrolü hattın en
/// sık sorgusuna çevirirdi. Panelde gösterilen şey o yüzden bir
/// worker'ın özel sayacı değil, FİLONUN TAMAMININ gözlemi —
/// `provider_calls` zaten yazılıyor ve doğru soruya cevap veren de bu:
/// "bu sağlayıcı şu an sağlıklı mı", "şu worker'da devre açık mı"
/// değil.
internal sealed record ProviderHealthEntry(
    string ProviderKey,
    int Calls,
    int Failures,
    /// SONDAKİ ART ARDA HATA SAYISI.
    ///
    /// Toplam hata oranından farklı ve daha keskin: sabah beş hata alıp
    /// sonra düzelmiş bir sağlayıcı ile şu an art arda beş hata veren
    /// sağlayıcı aynı orana sahip olabiliyor, ama biri sağlıklı diğeri
    /// ölü. Devre kesicinin baktığı sayı da bu.
    int ConsecutiveFailures,
    bool Unhealthy,
    DateTimeOffset? LastCallAt,
    DateTimeOffset? LastSuccessAt,
    int AverageLatencyMs,
    decimal Cost);

internal sealed record ProviderHealthSummary(
    int WindowMinutes,
    int FailureThreshold,
    IReadOnlyList<ProviderHealthEntry> Providers);

/// Bir hata kodunun kaç kez görüldüğü.
public sealed record FailureCount(string Code, int Count);

/// Gece raporu (P2-13).
///
/// KABUL KRİTERİ `UnattendedVideos` alanında ölçülüyor: insan
/// müdahalesi olmadan hazır olan video sayısı. "5 video üretildi" ile
/// "5 video üretildi ama 4'ü onay bekliyor" farklı sonuçlar ve ikisini
/// aynı sayıya sıkıştırmak, kriterin sağlandığı izlenimi verirdi.
public sealed record MorningSummary(
    int WindowHours,
    int Runs,
    int Completed,
    int Failed,
    int WaitingApproval,
    int WaitingResource,
    int StillRunning,
    /// Pencereden BAĞIMSIZ: dün geceden kalmış bir onay bugünün
    /// penceresine girmiyor ama hâlâ insanın işi.
    int PendingApprovals,
    int DeadLettered,
    int RetryLoops,
    decimal Cost,
    decimal CostPerRun,
    /// Ortalama koşu süresi (dakika). Hiç biten koşu yoksa null —
    /// sıfır göstermek "anında bitti" gibi okunurdu.
    double? AverageMinutes,
    /// Ortalama QC skoru (0–1). Hiç ölçüm yoksa null.
    double? AverageScore,
    int ScoredRuns,
    int UnattendedVideos,
    IReadOnlyList<FailureCount> Failures)
{
    /// Kabul kriteri sağlandı mı: gecede en az 3 video, insan
    /// müdahalesi olmadan.
    public bool AcceptanceMet => UnattendedVideos >= 3;
}

/// Varlık gezginindeki tek bir kayıt (P3-08).
public sealed record AssetEntry(
    string Sha256,
    string Kind,
    string MimeType,
    long Bytes,
    int? Width,
    int? Height,
    string? SourceProvider,
    string? SourceUrl,
    string? LicenseName,
    string? LicenseAuthor,
    bool RequiresAttribution,
    /// Uyum riski. `null` = sorun yok.
    ///
    /// METİN, BAYRAK DEĞİL: "riskli" tek başına ne yapılacağını
    /// söylemiyor ve üç ayrı sebep üç ayrı düzeltme gerektiriyor.
    string? Risk,
    DateTimeOffset CreatedAt);

public sealed record LicenseCount(string License, int Count);

/// Varlık envanteri ve lisans raporu (P3-08).
public sealed record AssetReport(
    int Total,
    int Risky,
    long TotalBytes,
    IReadOnlyList<LicenseCount> ByLicense,
    IReadOnlyList<AssetEntry> Assets);

/// Bir iş akışı sürümü (P3-06).
public sealed record WorkflowVersionSummary(
    int Version,
    bool IsCurrent,
    int NodeCount,
    int EdgeCount,
    int RunCount,
    /// KOŞAN RUN SAYISI AYRI: eski bir sürümü silmenin güvenli olup
    /// olmadığı buna bağlı ve "toplam run" bu soruya cevap vermiyor.
    int ActiveRunCount,
    DateTimeOffset CreatedAt);

public sealed record WorkflowSummary(
    string Key,
    string Name,
    string ContentKind,
    int CurrentVersion,
    IReadOnlyList<WorkflowVersionSummary> Versions);

public sealed record GraphNodeView(string Id, string Type);

public sealed record GraphEdgeView(string From, string To, string? When);

public sealed record WorkflowGraphView(
    string Key,
    int Version,
    IReadOnlyList<GraphNodeView> Nodes,
    IReadOnlyList<GraphEdgeView> Edges,
    /// Graf okunamadıysa sebebi. `null` = sorun yok.
    string? Error);

/// Bir istem şablonu (P3-07).
public sealed record PromptSummary(
    string Key,
    int Version,
    string Stamp,
    string? Description,
    int SystemLength,
    int UserLength,
    IReadOnlyList<string> Variables);

/// Tek bir fixture'ın sonucu (P3-07).
public sealed record EvalRow(
    string Name,
    string PromptKey,
    /// Hangi istem SÜRÜMÜ sınandı. Fixture sürüm sabitlemiyorsa en
    /// yüksek sürüm koşuyor, yani damga koşudan koşuya değişebiliyor
    /// — bunu yazmadan "geçti" hangi metin için geçti belirsiz kalır.
    string? Stamp,
    bool Passed,
    int RenderedChars,
    IReadOnlyList<string> Failures);

/// Değerlendirme ekranı (P3-07).
public sealed record EvalScreen(
    /// Değerlendirme KOŞTU mu. `false` iken sayılar anlamsız.
    bool Ran,
    string Directory,
    int Passed,
    int Failed,
    IReadOnlyList<EvalRow> Rows,
    /// Koşamadıysa sebebi. `null` = koştu.
    ///
    /// AYRI BİR ALAN, çünkü "koşmadı" ile "koştu, hepsi geçti"
    /// ikisi de sıfır düşüş gösteriyor ve ikisini aynı ekranda
    /// göstermek, hiç sınanmamış bir istem setini sınanmış gibi
    /// okuturdu.
    string? Problem);

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

/// Kanalın kontrol durumu (P2-04).
internal sealed record ChannelControl(Guid Id, string Name, string Language, bool Paused, string Mode);

/// Sistem kontrol paneli.
internal sealed record ControlState(
    bool KillSwitchEngaged,
    string? By,
    string? Reason,
    DateTimeOffset? Since,
    IReadOnlyList<ChannelControl> Channels);

internal sealed record KillSwitchRequest(bool Engaged, string DecidedBy, string? Reason);

internal sealed record ChannelPauseRequest(bool Paused);
