using System.Diagnostics.CodeAnalysis;
using BytemountsAiStudio.Core.Errors;

namespace BytemountsAiStudio.Core;

/// Deger dondurmeyen islemlerin sonucu.
///
/// Neden exception degil: bu sistemde basarisizlik beklenen bir durum
/// (kota doldu, kaynak bulunamadi, dogrulama gecmedi). Beklenen durumu
/// exception ile tasimak hem pahali hem de "hangi cagri patlayabilir"
/// sorusunu tip sisteminden gizler. Exception yalnizca gercekten beklenmeyen
/// durumlar icin kalir.
public readonly record struct Result
{
    private Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    /// `return Error.Permanent(...)` yazabilmek için. Sarmalayıcıyı elle
    /// yazmak zorunda kalmak, hata yolunu gereksiz yere gürültülü yapıyordu.
    public static implicit operator Result(Error error) => Failure(error);
}

/// Deger donduren islemlerin sonucu.
public readonly record struct Result<T>
{
    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    private readonly T? _value;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    /// Basarisiz sonucta deger okumak programlama hatasidir; sessizce
    /// default donmek yerine patlar.
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Basarisiz sonuctan deger okunamaz. Hata: {Error}");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public Result<TNext> Map<TNext>(Func<T, TNext> map)
        => IsSuccess ? Result<TNext>.Success(map(_value!)) : Result<TNext>.Failure(Error);

    public Result<TNext> Bind<TNext>(Func<T, Result<TNext>> bind)
        => IsSuccess ? bind(_value!) : Result<TNext>.Failure(Error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
        => IsSuccess ? onSuccess(_value!) : onFailure(Error);
}
