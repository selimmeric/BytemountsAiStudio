using System.Globalization;
using BytemountsAiStudio.Core.Execution;

namespace BytemountsAiStudio.Worker;

/// Worker'ın hangi kuyrukları dinlediği (P4-01).
///
/// NEDEN ROL: render bir makinenin bütün çekirdeklerini ve
/// gigabaytlarca belleğini yiyor. LLM ve varlık işleri ise ağ
/// bekliyor — yani ucuz bir makinede sekiz tanesi rahatça koşuyor.
/// İkisini aynı süreçte tutmak, ağ bekleyen işleri render'ın
/// bitmesini bekleyen bir makineye hapsetmek demek.
///
/// AYIRAN ŞEY KOD DEĞİL, YAPILANDIRMA. Kuyruk zaten kiralama tabanlı
/// (`FOR UPDATE SKIP LOCKED`): iki worker aynı veritabanına bakıyor ve
/// hiçbiri diğerinin işini almıyor. "Ayrı makine" bunun bir sonucu,
/// yeni bir mekanizma değil.
public enum WorkerRole
{
    /// Bütün kuyruklar — tek makinede çalışan kurulum.
    All = 0,

    /// YALNIZCA RENDER (ve yükleme).
    ///
    /// Yükleme de burada, çünkü yüklenecek dosya bu makinede duruyor:
    /// ayrı bir makineye almak, gigabaytlarca videoyu iki kez
    /// taşımak demekti.
    Render = 1,

    /// RENDER DIŞINDAKİ HER ŞEY.
    ///
    /// Senaryo, arama, seslendirme, görsel — hepsi ağ bekliyor ve
    /// ucuz bir makinede yüksek eşzamanlılıkla koşuyor.
    Light = 2,
}

public static class WorkerRoles
{
    /// `BMAI_ROLE` ortam değişkeninden okunuyor.
    ///
    /// TANINMAYAN DEĞER SESSİZCE `All`'A DÜŞMÜYOR: yazım hatası olan
    /// bir rol, bütün kuyrukları dinleyen bir render makinesi demekti
    /// ve bunu ancak makinenin neden LLM işi aldığını merak eden biri
    /// fark ederdi.
    public static Result Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Result(WorkerRole.All, null);
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "all" or "hepsi" => new Result(WorkerRole.All, null),
            "render" => new Result(WorkerRole.Render, null),
            "light" or "hafif" => new Result(WorkerRole.Light, null),
            _ => new Result(WorkerRole.All,
                $"'{value}' tanınmayan bir rol; bütün kuyruklar dinleniyor. "
                + "Geçerli değerler: all, render, light."),
        };
    }

    public readonly record struct Result(WorkerRole Role, string? Warning);

    /// Role göre kuyruk eşzamanlılığı.
    ///
    /// Sayılar kaynağın kendisinden geliyor (§8.1) ve rol yalnızca
    /// hangilerinin AÇIK olduğunu değiştiriyor — render makinesinde
    /// render eşzamanlılığı yine 1, çünkü sınırı makine sayısı değil
    /// FFmpeg'in kendisi.
    public static IReadOnlyDictionary<QueueClass, int> ConcurrencyFor(WorkerRole role)
    {
        var all = new Dictionary<QueueClass, int>
        {
            [QueueClass.Llm] = 8,
            [QueueClass.Search] = 4,
            [QueueClass.Asset] = 8,
            [QueueClass.ImageGeneration] = 2,
            [QueueClass.Tts] = 3,
            [QueueClass.Align] = 2,
            [QueueClass.Render] = 1,
            [QueueClass.Upload] = 1,
        };

        var selected = role switch
        {
            WorkerRole.Render => all
                .Where(e => e.Key is QueueClass.Render or QueueClass.Upload)
                .ToDictionary(e => e.Key, e => e.Value),

            WorkerRole.Light => all
                .Where(e => e.Key is not (QueueClass.Render or QueueClass.Upload))
                .ToDictionary(e => e.Key, e => e.Value),

            _ => all,
        };

        // ORTAM DEĞİŞKENİYLE İNCE AYAR: `BMAI_CONCURRENCY_RENDER=2`.
        //
        // Makineler aynı değil ve sayıları koda gömmek, on altı
        // çekirdekli bir render makinesinde de tek render koşturmak
        // demekti.
        foreach (var queue in selected.Keys.ToList())
        {
            var name = "BMAI_CONCURRENCY_" + queue.ToString().ToUpperInvariant();
            var raw = Environment.GetEnvironmentVariable(name);

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value > 0)
            {
                selected[queue] = value;
            }
        }

        return selected;
    }
}
