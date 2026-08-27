using BytemountsAiStudio.Core.Time;

namespace BytemountsAiStudio.Contracts.Providers;

/// Ölçülen segment süresini kelimelere dağıtır (P1-15 ara çözüm).
///
/// NEDEN GEREKLİ: ücretsiz hatta kullandığımız Windows konuşma sentezi
/// kelime zamanlaması VERMİYOR. Zamanlama olmayınca altyazı ipucu da
/// üretilemiyordu ve gerçek videolar altyazısız çıkıyordu — sahte hatta
/// altyazı vardı, gerçek hatta yoktu, ve bu fark kimseye görünmüyordu.
///
/// NE DEĞİL: bu bir hizalama değil, DAĞITIMDIR. Gerçek hizalama ya
/// TTS'in kendi zamanlamasından (ElevenLabs veriyor) ya da ASR
/// yan servisinden (WhisperX, P1-04) gelir. Öncelik sırası:
///   1. Sağlayıcının kendi zamanlaması — gerçek
///   2. ASR hizalaması                 — gerçek, ölçülmüş
///   3. Bu dağıtım                     — tahmin, ama altyazısız kalmaktan iyi
///
/// Tahmin olduğu için kayıtta İŞARETLENİYOR: bir altyazı kaymasının
/// sebebi araştırılırken "bu zamanlama ölçülmedi, dağıtıldı" bilgisi
/// ilk bakılacak şey.
public static class WordTimingEstimator
{
    /// Noktalama sonrası duraklama ağırlığı.
    ///
    /// Konuşmada cümle ve virgül sonrasında gerçek bir es var; bunu
    /// saymazsak sondaki kelimeler erken biter ve altyazı sesin önüne
    /// geçer. Önüne geçen altyazı, arkada kalandan daha rahatsız edici.
    private const double PauseWeight = 2.5;

    /// Kelime başına taban ağırlık. Tek harflik bir kelime bile sıfır
    /// süre almamalı.
    private const double BaseWeight = 1.5;

    /// Bir ipucunun ekranda kalabileceği en kısa süre.
    private static readonly Ms MinimumCue = new(120);

    /// Metni kelimelere bölüp verilen süreyi aralarında paylaştırır.
    ///
    /// Ağırlık karakter sayısına göre: uzun kelime daha uzun sürüyor.
    /// Eşit paylaştırmak "bir" ile "arkeologların" kelimesine aynı süreyi
    /// verirdi ve uzun kelimelerde altyazı sesin gerisinde kalırdı.
    public static IReadOnlyList<WordTiming> Distribute(string text, Ms total)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = text.Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || total.Value <= 0)
        {
            return [];
        }

        var weights = new double[words.Length];
        var sum = 0.0;

        for (var i = 0; i < words.Length; i++)
        {
            weights[i] = BaseWeight + words[i].Length;

            if (EndsWithPause(words[i]))
            {
                weights[i] += PauseWeight;
            }

            sum += weights[i];
        }

        var timings = new List<WordTiming>(words.Length);
        var cursor = 0.0;

        for (var i = 0; i < words.Length; i++)
        {
            var start = cursor;
            cursor += weights[i] / sum * total.Value;

            // Son kelimenin sonu TAM olarak segment sonu: kayan nokta
            // birikimi yüzünden birkaç milisaniye eksik kalmasın, yoksa
            // uzun videolarda fark büyür.
            var end = i == words.Length - 1 ? total.Value : cursor;

            timings.Add(new WordTiming(
                words[i],
                new Ms((int)Math.Round(start)),
                new Ms(Math.Max((int)Math.Round(end), (int)Math.Round(start) + MinimumCue.Value))));
        }

        return timings;
    }

    private static bool EndsWithPause(string word)
        => word.Length > 0 && word[^1] is '.' or ',' or ';' or ':' or '!' or '?' or '…';
}
