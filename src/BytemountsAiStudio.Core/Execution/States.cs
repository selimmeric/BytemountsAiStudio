namespace BytemountsAiStudio.Core.Execution;

/// Bir icerik uretim kosusunun durumu (mimari §6.3).
public enum RunState
{
    Pending = 0,
    Running = 1,

    /// Insan onayi bekliyor. Worker kaynagi tuketmez.
    WaitingApproval = 2,

    /// Kota ya da butce bekliyor. Hata DEGIL - erteleme.
    WaitingResource = 3,

    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

/// Tek bir node calistirmasinin durumu.
public enum NodeState
{
    Pending = 0,
    Leased = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,

    /// Kosul saglanmadigi icin atlandi (`when` ifadesi false).
    Skipped = 5,
}

/// Kuyruktaki bir isin durumu.
public enum JobState
{
    Pending = 0,

    /// Bir worker kiralama aldi. Lease suresi dolarsa Pending'e doner.
    Leased = 1,

    Succeeded = 2,
    Failed = 3,
    DeadLettered = 4,
    Cancelled = 5,
}

/// Kanalin calisma modu (§22).
public enum ChannelMode
{
    /// Tam otonom.
    Auto = 0,

    /// Her kritik asamada insan onayi.
    Approval = 1,

    /// Yalnizca QC skoru esigin altinda kalanlar insana duser.
    Selective = 2,
}

/// Is kuyrugu sinifi. Her sinifin kendi esZamanlilik, timeout ve retry
/// politikasi var (mimari §8.1) - hepsi ayni havuzda olamaz.
public enum QueueClass
{
    Llm = 0,
    Search = 1,
    Asset = 2,
    ImageGeneration = 3,
    Tts = 4,
    Align = 5,
    Render = 6,
    Upload = 7,
}
