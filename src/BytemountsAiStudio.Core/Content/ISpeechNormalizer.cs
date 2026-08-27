namespace BytemountsAiStudio.Core.Content;

/// Ekranda görünen metni SESLENDİRİLECEK metne çevirir (§20.3).
///
/// `display_text` / `speech_text` ayrımının uygulandığı yer. "1453" ekranda
/// öyle yazılır ama "bin dört yüz elli üç" diye okunur; TTS'e ham hâliyle
/// verilirse sağlayıcı ya rakamı harf harf okur ya da dili yanlış varsayar.
///
/// Kural tabanlı, LLM değil. Üç sebep:
///   1. Aynı sayı her videoda aynı okunmalı — LLM tutarsızlık üretir
///   2. Her cümle için model çağırmak para ve gecikme demek
///   3. Yanlış okunuşu düzeltmek kural eklemekle olur, prompt'u
///      "biraz daha iyi" yazmaya çalışmakla değil
public interface ISpeechNormalizer
{
    /// Hangi dil için: "tr", "en"…
    string Language { get; }

    string Normalize(string displayText);
}
