using System.Globalization;

namespace BytemountsAiStudio.Core.Errors;

/// Bir basarisizligin tasinabilir tanimi.
///
/// Exception yerine deger olarak tasinir: node ciktilari, kuyruk kayitlari ve
/// API cevaplari ayni hatayi seri hale getirmek zorunda. Exception'i
/// serilestirmek yigin izini de tasir; bunu DB'ye yazmak istemiyoruz.
public sealed record Error(
    string Code,
    string Message,
    ErrorKind Kind = ErrorKind.Permanent,
    string? Detail = null,
    TimeSpan? RetryAfter = null)
{
    public static Error Transient(string code, string message, TimeSpan? retryAfter = null)
        => new(code, message, ErrorKind.Transient, RetryAfter: retryAfter);

    public static Error Permanent(string code, string message, string? detail = null)
        => new(code, message, ErrorKind.Permanent, detail);

    /// Kaynak tukendi. `retryAfter` ne zaman yeniden denenebilecegini soyler;
    /// YouTube kotasi icin bu genellikle ertesi gunun basidir.
    public static Error Resource(string code, string message, TimeSpan retryAfter)
        => new(code, message, ErrorKind.Resource, RetryAfter: retryAfter);

    public static Error Cancelled(string message = "Islem iptal edildi.")
        => new("cancelled", message, ErrorKind.Cancelled);

    public bool IsRetryable => Kind is ErrorKind.Transient;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"[{Kind}] {Code}: {Message}");
}
