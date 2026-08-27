using System.Globalization;

namespace BytemountsAiStudio.Core.Time;

/// Milisaniye cinsinden sure. Tam sayi, bilerek.
///
/// ADR-006 ve §11.3: float saniye kullanmak kare hesaplarinda yuvarlama hatasi
/// uretiyor - 30 fps'te 0.1 sn farki bir kare kaydirir, 500 sahnede birikir.
/// Timeline'daki her sure bu tipten gecer; birim karisikligi tip hatasi olur.
public readonly record struct Ms(int Value) : IComparable<Ms>
{
    public static readonly Ms Zero = new(0);

    public static Ms FromSeconds(double seconds) => new((int)Math.Round(seconds * 1000.0));

    public static Ms FromTimeSpan(TimeSpan span) => new((int)Math.Round(span.TotalMilliseconds));

    public double TotalSeconds => Value / 1000.0;

    public TimeSpan ToTimeSpan() => TimeSpan.FromMilliseconds(Value);

    /// Verilen kare hizinda bu ana denk gelen kare numarasi.
    public int ToFrame(int fps) => (int)Math.Round(Value / 1000.0 * fps);

    public static Ms operator +(Ms a, Ms b) => new(a.Value + b.Value);

    public static Ms operator -(Ms a, Ms b) => new(a.Value - b.Value);

    public static bool operator <(Ms a, Ms b) => a.Value < b.Value;

    public static bool operator >(Ms a, Ms b) => a.Value > b.Value;

    public static bool operator <=(Ms a, Ms b) => a.Value <= b.Value;

    public static bool operator >=(Ms a, Ms b) => a.Value >= b.Value;

    public int CompareTo(Ms other) => Value.CompareTo(other.Value);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Value}ms");
}
