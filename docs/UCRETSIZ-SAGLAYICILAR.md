# Ücretsiz Sağlayıcı Kataloğu

Sistemin **API anahtarı olmadan** çalışabildiği servisler ve anahtar geldiğinde açılacak olanlar. Makine tarafından okunan hâli: [`config/providers.json`](../config/providers.json).

```bash
dotnet run --project src/BytemountsAiStudio.Cli -- providers
```

Bu komut "şu an ne ile çalışabiliyorum" sorusunu tek ekranda cevaplıyor.

---

## Şu an çalışan hat — hiçbir anahtar gerekmiyor

| Rol | Servis | Maliyet | Kalite | Not |
|---|---|---|---|---|
| LLM | **Ollama** (yerel) | ücretsiz | orta | Sınırsız, ağ gerektirmez |
| Araştırma | **Wikipedia** resmî API | ücretsiz | yüksek | Ansiklopedik içeriğin ilk durağı |
| Araştırma | **SearXNG** (kendi sunucunuzda) | ücretsiz | yüksek | Genel web araması |
| Stok görsel | **Openverse** | ücretsiz | orta | Creative Commons, lisans filtreli |
| AI görsel | **Pollinations** | ücretsiz | orta | Anahtarsız üretim |
| Ses | **Windows konuşma sentezi** | ücretsiz | düşük-orta | tr-TR: Microsoft Tolga |

Doğrulanmış çıktı: `bmai real --topic "Göbeklitepe"` → 1080×1920, 13.5 sn, Wikipedia'ya dayalı Türkçe senaryo, gerçek seslendirme, AI görseller.

---

## Kurulum

**Ollama** (zaten kurulu). Senaryo kalitesi için genel bir instruct modeli çekin — coder modelleri anlatı yazmakta zayıf:

```bash
ollama pull qwen2.5:7b-instruct
```

**SearXNG** — Docker profiliyle geliyor, varsayılan olarak başlamıyor:

```bash
docker compose --profile tools up -d searxng
```

`docker/searxng/settings.yml` içinde `formats` listesine `json` eklenmiş durumda. Bu satır olmadan API 403 döner — SearXNG'nin varsayılanı JSON'u kapalı tutmak.

**Windows sesi** — kurulum gerekmiyor. Yüklü sesleri görmek için:

```bash
powershell -Command "[Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices | ForEach-Object { $_.DisplayName + ' / ' + $_.Language }"
```

Başka bir dil eklemek isterseniz Windows *Ayarlar → Saat ve Dil → Dil → Konuşma* üzerinden ses paketi kurulabiliyor.

---

## Lisans uyarısı — Openverse

Openverse'ün **varsayılan sonuçları kullanılamaz.** Filtresiz arama çoğunlukla `by-nc-nd` döndürüyor:

- **NC** (NonCommercial): gelirlendirilmiş kanalda kullanılamaz
- **ND** (NoDerivatives): kırpma bir türevdir — **Ken Burns hareketi bile ihlal**

Bu yüzden `license_type=commercial,modification` filtresi **koda gömülü ve konfigürasyondan kapatılamıyor**. Yalnızca `by`, `by-sa`, `cc0`, `pdm` kabul ediliyor. Gelen sonuç ayrıca ikinci kez doğrulanıyor: API davranışı değişirse sessizce ihlal etmeyelim.

CC0 ve PDM dışındaki lisanslar **atıf zorunlu** — video açıklamasına eklenmesi gerekiyor. Bu bilgi her varlıkla birlikte saklanıyor (§2.3/14).

---

## Anahtar geldiğinde açılacaklar

Hiçbiri zorunlu değil; sistem onlarsız çalışıyor. Sıralama, **kaliteye en çok katkı yapandan** başlıyor.

| Öncelik | Servis | Ortam değişkeni | Neden |
|---|---|---|---|
| 1 | **ElevenLabs** | `ELEVENLABS_API_KEY` | Ses kalitesi farkı en çok hissedilen kalem. Ayrıca karakter zamanlaması döndürüyor — ASR yedeğini gereksiz kılar |
| 2 | **Pexels** | `PEXELS_API_KEY` | Görsel kalitesi açık ara en iyisi. Anahtar ücretsiz ve dakikalar sürüyor |
| 3 | **Gemini** | `GEMINI_API_KEY` | Cömert ücretsiz kotası var; senaryo kalitesini Ollama'nın üstüne çıkarır |
| 4 | **Flux** | `FLUX_API_KEY` | AI görsel kalitesi ve hızı |
| 5 | **Brave Search** | `BRAVE_API_KEY` | Aylık 2000 sorgu ücretsiz; güncel konular için |
| 6 | **YouTube** | `YOUTUBE_CLIENT_SECRET` | Yayınlama. Kota tavanı: proje başına günde ~6 video |

### Anahtar geldiğinde ne yapılacak

Kod değişmiyor. `config/providers.json` içinde iki satır:

```jsonc
{ "key": "elevenlabs", "enabled": true }          // false → true
"tts": ["elevenlabs", "windows-speech"]           // yeni sağlayıcı başa
```

Anahtar dosyaya **girmiyor** — ortam değişkeninden okunuyor. Katalog depoya giriyor, anahtarlar girmiyor.

Katalog doğrulaması, anahtarı tanımlı olmayan bir sağlayıcı yönlendirmeye konursa **açılışta hata veriyor**; ilk çağrıda beklenmedik bir kimlik hatası almaktansa hemen görünmesi daha iyi.

---

## Denenip listeye alınmayanlar

| Servis | Neden alınmadı |
|---|---|
| DuckDuckGo Instant Answer | Yalnızca özet cevap döndürüyor, gerçek web araması değil. Katalogda kapalı duruyor |
| Lorem Picsum | Rastgele görsel; konuyla ilgisi yok, içerik üretiminde işe yaramaz |
| Wikimedia Commons | Çalışıyor ama lisanslar dosya bazında değişiyor ve her birinin ayrıca kontrolü gerekiyor. Openverse bu işi zaten yapıyor |
| **AI web arayüzlerini tarayıcıyla sürmek** | Kullanım şartlarına aykırı, bot tespitine takılır, hesabı riske atar. ADR-015'te açıkça reddedildi |

---

## Bilinen sınırlar

**Pollinations paralellikte 429 veriyor.** Üç eşzamanlı istekte sınır uygulandı; ikiye indirildi ve istekler arasına 400 ms kayma kondu. 429 artık *geçici hata* değil **kaynak hatası** olarak işleniyor: iş ertelenir, deneme sayacı artmaz, run düşmez.

**Ollama GPU hatası verebiliyor.** Bir koşuda `CUDA error: unknown error` alındı; geçici hata sınıfına girdiği için ikinci denemede geçti. Retry mekanizması bunu görünmez kıldı.

**Windows TTS kelime zamanlaması vermiyor.** Altyazı hizalaması şu an TTS'in kendi ürettiği zamanlamaya değil, sahte dağıtıma dayanıyor. Gerçek hizalama için WhisperX yan servisi (P1-04) gerekiyor.

**Windows TTS Linux'ta çalışmaz.** Faz 4'te Linux'a geçerken Piper'a taşınacak — katalogda kayıtlı, kurulumu bekliyor.
