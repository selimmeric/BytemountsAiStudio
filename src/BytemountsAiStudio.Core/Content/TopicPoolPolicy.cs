using System.Globalization;

namespace BytemountsAiStudio.Core.Content;

/// Konu havuzunun bir kanal + dil için durumu (P2-01).
public sealed record PoolStatus(int Ready, int Producing, int DailyTarget)
{
    /// Elde kaç günlük konu var.
    public double DaysOfSupply => DailyTarget <= 0 ? double.MaxValue : (Ready + Producing) / (double)DailyTarget;
}

/// Havuz doldurma kararı (P2-01).
public sealed record RefillPlan(int Count, string Reason)
{
    public bool ShouldRefill => Count > 0;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Count} konu: {Reason}");
}

/// Konu havuzunun otomatik doldurulması (P2-01, §7.1).
///
/// KABUL KRİTERİ: havuz hiç boşalmıyor; içerik koşusu konu beklemiyor.
///
/// SAF: veritabanı yok. "Şimdi konu üretmeli miyiz" kararı, havuz
/// gerçekten boşalarak öğrenilecek bir şey olmamalı — boşaldığı an
/// zaten geç kalınmış an.
///
/// EŞİK GÜN CİNSİNDEN, adet cinsinden değil. "En az 10 konu olsun"
/// demek, günde bir video üreten kanalda on günlük stok, günde beş
/// video üretende iki günlük stok demek — aynı sayı iki kanalda
/// tamamen farklı anlamlar taşıyor. "En az iki günlük" her ikisinde
/// de aynı şeyi söylüyor.
public static class TopicPoolPolicy
{
    /// Bu eşiğin altına düşünce üretim tetikleniyor.
    ///
    /// İKİ GÜN: bir günlük stok, üretimin gecikmesi hâlinde havuzun
    /// boşalması demek — ve üretim gecikebiliyor, çünkü LLM çağrısı
    /// da kuyruğa giriyor ve kotaya takılabiliyor.
    public const double LowWaterDays = 2.0;

    /// Buraya kadar dolduruluyor.
    ///
    /// BEŞ GÜN: yüksek eşik düşükten belirgin biçimde uzak olmak
    /// zorunda, yoksa her üretimden sonra havuz hemen tekrar eşiğin
    /// altına düşüyor ve sistem sürekli küçük partiler üretiyor —
    /// her parti bir LLM çağrısı ve her çağrının sabit bir maliyeti
    /// var.
    public const double HighWaterDays = 5.0;

    /// Tek seferde en fazla kaç konu üretilir.
    ///
    /// Sınır bir güvenlik kemeri: yanlış yapılandırılmış bir günlük
    /// hedef (örneğin 500) tek çağrıda yüzlerce konu üretmeye
    /// çalışırdı.
    public const int MaxBatch = 20;

    public static RefillPlan Decide(
        PoolStatus status, double lowWaterDays = LowWaterDays, double highWaterDays = HighWaterDays)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.DailyTarget <= 0)
        {
            // Hedefi olmayan kanal konu istemiyor. Üretmek, hiç
            // kullanılmayacak konular için para harcamaktı.
            return new RefillPlan(0, "kanalın günlük hedefi yok");
        }

        var low = Math.Max(lowWaterDays, 0);
        var high = Math.Max(highWaterDays, low);

        // ÜRETİLMEKTE OLANLAR DA SAYILIYOR.
        //
        // Yalnızca hazır olanlara bakmak, arka arkaya çalışan iki
        // doldurma turunun aynı eksiği iki kez kapatması demekti:
        // birincisi üretimi başlatıyor ama henüz hazır konu yok,
        // ikincisi "hâlâ boş" deyip bir tur daha başlatıyor.
        if (status.DaysOfSupply >= low)
        {
            return new RefillPlan(0, string.Create(CultureInfo.InvariantCulture,
                $"{status.DaysOfSupply:0.#} günlük stok var (eşik {low:0.#})"));
        }

        var target = (int)Math.Ceiling(high * status.DailyTarget);
        var needed = Math.Clamp(target - status.Ready - status.Producing, 1, MaxBatch);

        return new RefillPlan(needed, string.Create(CultureInfo.InvariantCulture,
            $"{status.DaysOfSupply:0.#} günlük stok kaldı, {high:0.#} güne çıkarılıyor"));
    }

    /// Havuz KRİTİK mi — yani bir sonraki koşu konu bekleyecek mi.
    ///
    /// Bu, doldurma eşiğinden farklı ve ayrı raporlanıyor: eşiğin
    /// altına düşmek normal işleyiş (doldurma tetiklenir), tamamen
    /// boşalmak bir arıza. İkisini aynı sayıya bakmak, arızayı normal
    /// işleyişin içinde gizlerdi.
    public static bool IsStarved(PoolStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return status.Ready == 0;
    }
}
