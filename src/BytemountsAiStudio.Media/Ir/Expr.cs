using System.Globalization;
using BytemountsAiStudio.Media.Timeline;

namespace BytemountsAiStudio.Media.Ir;

/// FFmpeg ifadesi.
///
/// Ayrı bir tip, çünkü "bu bir sabit mi yoksa kare başına hesaplanan bir
/// ifade mi" sorusu tip sisteminde cevaplanmalı, string içinde değil
/// (§12.1/L5'in karşılığı).
public readonly record struct Expr(string Text)
{
    public static Expr Constant(double value)
        => new(value.ToString("0.######", CultureInfo.InvariantCulture));

    public static Expr Raw(string text) => new(text);

    public override string ToString() => Text;
}

/// Keyframe'leri FFmpeg ifadelerine derler.
///
/// Studio'dan korunan fikir (§12.1): ifadeler İÇ İÇE DEĞİL, düz yazılır.
/// İç içe `if(...)` zincirleri keyframe sayısı arttıkça FFmpeg'in ayrıştırıcı
/// derinlik sınırına takılıyordu. Buradaki üretim, kaç keyframe olursa olsun
/// sabit derinlikte kalır.
public static class ExprCompiler
{
    /// Normalize edilmiş ilerlemeyi (`0..1`) kare numarasından üretir.
    /// `on` = zoompan'ın mevcut kare sayacı.
    public static Expr Progress(int frames)
    {
        var span = Math.Max(1, frames - 1);
        return new Expr($"min(1,on/{span.ToString(CultureInfo.InvariantCulture)})");
    }

    /// İki değer arasında yumuşatılmış geçiş.
    ///
    /// Easing'i ifade içinde uyguluyoruz; kare kare hesaplamak yerine FFmpeg'e
    /// bırakmak hem hızlı hem de önizleme ile render'ın aynı formülü
    /// kullanmasını sağlıyor.
    public static Expr Interpolate(double from, double to, int frames, Easing easing)
    {
        if (Math.Abs(to - from) < 1e-9)
        {
            return Expr.Constant(from);
        }

        var p = Progress(frames).Text;
        var eased = easing switch
        {
            Easing.Linear => p,
            Easing.EaseIn => $"pow({p},2)",
            Easing.EaseOut => $"(1-pow(1-{p},2))",
            Easing.EaseInOut => $"(if(lt({p},0.5),2*pow({p},2),1-pow(-2*{p}+2,2)/2))",
            _ => p,
        };

        var f = from.ToString("0.######", CultureInfo.InvariantCulture);
        var delta = (to - from).ToString("0.######", CultureInfo.InvariantCulture);

        return new Expr($"({f}+({delta})*{eased})");
    }

    /// Ken Burns'ün yatay kaydırma ifadesi.
    ///
    /// Pan değeri [-1, 1] aralığında normalize; -1 sol kenar, +1 sağ kenar.
    /// Piksel yerine normalize değer kullanmak, aynı hareketin 9:16 ve 16:9
    /// tuvallerde aynı görünmesini sağlıyor.
    public static Expr PanX(double from, double to, int frames, Easing easing)
    {
        var factor = Interpolate((from + 1) / 2, (to + 1) / 2, frames, easing).Text;
        return new Expr($"(iw-iw/zoom)*{factor}");
    }

    public static Expr PanY(double from, double to, int frames, Easing easing)
    {
        var factor = Interpolate((from + 1) / 2, (to + 1) / 2, frames, easing).Text;
        return new Expr($"(ih-ih/zoom)*{factor}");
    }

    /// Sabit kadraj: merkezde kal.
    public static Expr CenterX() => Expr.Raw("iw/2-iw/zoom/2");

    public static Expr CenterY() => Expr.Raw("ih/2-ih/zoom/2");
}
