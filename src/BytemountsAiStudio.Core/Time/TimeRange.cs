using System.Globalization;

namespace BytemountsAiStudio.Core.Time;

/// Yari acik zaman araligi: [Start, End). Bitis disaridadir.
///
/// Yari acik olmasi kasitli: ardisik sahneler End == Start ile birlesir ve
/// aralarinda bir kare bosluk ya da bir kare cakisma olmaz. Kapali aralikta
/// "4000-9000" ve "9000-14000" 9000. milisaniyeyi iki kez cizerdi.
public readonly record struct TimeRange
{
    public TimeRange(Ms start, Ms end)
    {
        if (end < start)
        {
            throw new ArgumentException(
                $"Aralik bitisi baslangictan once olamaz: {start} -> {end}", nameof(end));
        }

        Start = start;
        End = end;
    }

    public Ms Start { get; }

    public Ms End { get; }

    public Ms Duration => End - Start;

    public bool IsEmpty => Duration.Value == 0;

    public static TimeRange FromDuration(Ms start, Ms duration) => new(start, start + duration);

    public bool Contains(Ms point) => point >= Start && point < End;

    /// Iki aralik cakisiyor mu. Yari acik oldugu icin uc uca eklenenler cakismaz.
    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public TimeRange Shift(Ms offset) => new(Start + offset, End + offset);

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"[{Start.Value}, {End.Value})");
}
