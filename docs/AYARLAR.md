# Ayarlar — bütün ortam değişkenleri

**Bu belge bir eksikliği kapatıyor.** Sistemdeki 52 ortam değişkeninin
**19'u hiçbir belgede yazmıyordu** — aralarında ffmpeg yolu, kiralama
süreleri, saklama pencereleri ve hat seçimi gibi işletme kararları
vardı. Kodda okunuyor olmaları onları *ayarlanabilir* yapmıyor:
**varlığından haberi olmadığın bir parametre, parametre değildir.**

Kural: bu tabloda olmayan bir `BMAI_*` değişkeni eklemek eksik iştir.
`ProviderEndpointTests` katalog ile kodun aynı adresi göstermesini
zorluyor; bu belge de aynı işi *insan* tarafında yapıyor.

---

## Hat seçimi

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_PIPELINE` | `acik` | `acik` \| `sahte` — hangi sağlayıcılar koşuyor |

**En önemli ayar bu.** `sahte` hat ağa çıkmıyor, para harcamıyor ve
**gerçek bir video dosyası üretiyor**: doğru süre, doğru çözünürlük,
doğru altyazı. Çıktı dizinine bakan bir insan ikisini ayırt edemiyor —
bu yüzden seçim hem açılışta loglanıyor hem de **her koşunun kaydına**
(`context.pipeline`) yazılıyor.

`bmai run` ve `bmai real` bu değişkeni **okumuyor**: ikisi ayrı komut ve
kullanıcı hangisini yazdıysa onu istiyor. Değişken yalnızca açık bir
seçimin olmadığı yerlerde (Worker, API) karar veriyor.

Kapta varsayılan `sahte` (`docker-compose.uygulama.yml`) ve sebebi
kabın kendisi: gerçek hattın konuşma sentezi Windows'un yerel sesini
kullanıyor, Linux kabında o yok. Yan servis (`tools-sidecar`)
kaldırıldığında `BMAI_PIPELINE=acik` yapılabilir.

## Veritabanı ve depolama

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_CONNECTION` | `Host=localhost;...;Database=bmai` | PostgreSQL bağlantısı |
| `BMAI_CONNECTION_READ` | (birincil) | Okuma replikası (P4-06). Yoksa birincile düşüyor — replika bir **optimizasyon**, doğruluk ona bağlı değil |
| `BMAI_STORAGE` | `./storage` | Dosya sistemi deposunun kökü |
| `BMAI_OUTPUT` | `./output` | Render edilmiş videoların çıktığı dizin |
| `BMAI_S3_ENDPOINT` | (yok) | Doluysa nesne deposu, boşsa dosya sistemi — seçim tek yerde (`StorageSelection`) |
| `BMAI_REDIS` | (yok) | Doluysa dağıtık hız sınırı ve devre kesici, boşsa süreç içi |
| `BMAI_KEYRING_PATH` | `~/.bmai/keyring` | Şifreleme anahtarının dosyası |

## Saklama ve bakım

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_RETENTION_DAYS` | `30` | Ara ürünlerin saklama süresi. Yayınlanmış içerik ve lisanslı varlık **hiç** silinmiyor |
| `BMAI_EVENT_RETENTION_DAYS` | `90` | `run_events` bölümlerinin düşürülme penceresi |

Bakım günde bir koşuyor (`PartitionService`). Sıfır ve negatif değerler
reddediliyor: sıfır gün, bugünün bölümünü düşürmeye çalışmak — yani
koşan sistemin altından tabloyu çekmek — demekti.

## Kuyruk ve eşzamanlılık

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_ROLE` | `all` | `all` \| `render` \| `light` — bu worker hangi kuyrukları dinliyor |
| `BMAI_CONCURRENCY_<KUYRUK>` | kaynaktan türetilmiş | Örn. `BMAI_CONCURRENCY_RENDER=4` |
| `BMAI_LEASE_<KUYRUK>` | render 60, upload 30, align 15, tts 5, diğer 3 (dakika) | Kiralama süresi |
| `BMAI_ATTEMPTS_<KUYRUK>` | upload 5, render/align/imagegeneration 2, diğer 3 | Deneme sayısı |
| `BMAI_RUNS_PER_CHANNEL` | `1` | Bir kanalda aynı anda kaç koşu |
| `BMAI_HEARTBEAT` | (yok) | Worker'ın kalp atışını yazdığı dosya — sağlık kontrolü buna bakıyor |

Kuyruk adları büyük harfle yazılıyor (`RENDER`, `LLM`, `TTS`, `UPLOAD`,
`ALIGN`, `IMAGEGENERATION`, `SEARCH`). Çeviri `InvariantCulture` ile:
Türkçe kültürde `i` → `İ` olur ve değişken adı hiçbir zaman eşleşmezdi.

Kiralama **iş koşarken uzatılıyor** (`LeaseKeeper`), o yüzden süreyi
büyütmek çoğu durumda gerekmiyor. Süre yine de ayarlanabilir: atış da
bir veritabanı kesintisinde kaçırılabiliyor.

`BMAI_RUNS_PER_CHANNEL` varsayılanı bir ve öyle kalmalı — paralel
koşular bütçeyi, QC'nin sorunu yakalamasından daha hızlı harcıyor.
Günlük hedefi büyütmek riski otomatik büyütmemeli, o yüzden ayrı ayar.

## Zamanlayıcı

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_ORCHESTRATOR` | `off` | `on` yapılmadıkça hiçbir koşu kendiliğinden başlamıyor |
| `BMAI_ORCHESTRATOR_INTERVAL` | `60` (saniye) | Kanalların değerlendirilme sıklığı |

Varsayılan kapalı çünkü bu döngü gerçek para harcayabiliyor. Açmak
bilinçli bir hareket olmalı.

## Sağlayıcı zinciri

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_PROVIDERS` | `./config/providers.json` | Sağlayıcı kataloğunun yolu |
| `BMAI_PROVIDER_ATTEMPTS` | `3` | Zincir içi çağrı denemesi — kuyruk denemesiyle **çarpılıyor** |
| `BMAI_BREAKER_THRESHOLD` | `5` | Kaç ardışık hatadan sonra sağlayıcı ölü sayılıyor |

Hız sınırları katalogdan okunuyor (`limits.requests_per_*`), koddan
değil. Katalog okunamazsa zincir yine kuruluyor ama **sınırsız** —
ve bu loglanıyor.

## Yerel model (Ollama)

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_OLLAMA` | (yok) | Doluysa konu üreticisi kaydediliyor |
| `BMAI_OLLAMA_URL` | `http://localhost:11434` | **Uzak olabilir** — zayıf makine güçlü makineye bağlanıyor |
| `BMAI_OLLAMA_MODEL_CHEAP` | `qwen2.5:7b-instruct` | Hacimli işler |
| `BMAI_OLLAMA_MODEL_STANDARD` | `qwen2.5:7b-instruct` | Araştırma planı, iddia çıkarma |
| `BMAI_OLLAMA_MODEL_STRONG` | `qwen2.5:7b-instruct` | Senaryo |
| `BMAI_OLLAMA_EMBEDDING` | `paraphrase-multilingual` | Konu tekilliği (768 boyut) |
| `BMAI_OLLAMA_TIMEOUT` | `300` (saniye) | 14B model ilk çağrıda beşi aşabiliyor |
| `BMAI_OLLAMA_CPU` | kapalı | **Modeli ekran kartına hiç yükleme** (`num_gpu: 0`) |

Ayrıntı: [DONANIM-VE-MODEL.md](DONANIM-VE-MODEL.md).

## Medya araçları

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_FFMPEG` | `ffmpeg` (PATH) | ffmpeg ikilisinin yolu |
| `BMAI_FFPROBE` | `ffprobe` (PATH) | ffprobe ikilisinin yolu |

Windows'ta ffmpeg PATH'te değilse render her koşuda düşer ve tek çözüm
makinenin PATH'ini değiştirmek olurdu. Aynı makinede iki farklı ffmpeg
sürümü (örn. NVENC destekli bir yapı) kullanmak da ancak böyle mümkün.

## Sağlayıcı adresleri

Hepsinin kod içinde bir varsayılanı var ve o varsayılan
`config/providers.json` ile **aynı olmak zorunda** —
`ProviderEndpointTests` bunu zorluyor. Adres değiştirmek, sahte bir
sunucuya yönlendirmek ya da bir aynaya bağlanmak için:

| Değişken | Servis |
|---|---|
| `BMAI_WIKIPEDIA_API_URL`, `BMAI_WIKIPEDIA_PAGE_URL` | Wikipedia (`{language}` yer tutucusu zorunlu) |
| `BMAI_WIKIDATA_URL` | Wikidata |
| `BMAI_SEARXNG_URL` | SearXNG |
| `BMAI_DUCKDUCKGO_URL` | DuckDuckGo Instant Answer |
| `BMAI_OPENVERSE_IMAGE_URL`, `BMAI_OPENVERSE_AUDIO_URL` | Openverse |
| `BMAI_POLLINATIONS_URL` | Pollinations görsel |
| `BMAI_POLLINATIONS_TEXT_URL` | Pollinations metin/görme (OpenAI uyumlu) |
| `BMAI_PEXELS_URL` | Pexels |
| `BMAI_OPENAI_URL`, `BMAI_OPENROUTER_URL`, `BMAI_GEMINI_URL` | Bulut LLM |
| `BMAI_ELEVENLABS_URL` | ElevenLabs |
| `BMAI_TOOLS_URL` | Araçlar yan servisi (Piper, WhisperX) |
| `BMAI_YOUTUBE_URL`, `BMAI_YOUTUBE_API_URL`, `BMAI_GOOGLE_TOKEN_URL` | YouTube yükleme ve OAuth |
| `BMAI_YOUTUBE_ANALYTICS_URL` | YouTube Analytics |
| `BMAI_TIKTOK_URL` | TikTok Content Posting |
| `BMAI_INSTAGRAM_URL` | Instagram Graph |

## Gözlemlenebilirlik

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_SEQ` | (yok) | Seq log toplayıcısının adresi |
| `BMAI_PROMPTS` | (yok) | İstem fixture'larının dizini — değerlendirme ekranı buna bakıyor |

## Yayın

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_PUBLIC_BASE_URL` | (yok) | Çıktı dosyalarının **dışarıdan erişilebilir** adres öneki |

**Instagram bunu zorunlu istiyor.** Instagram videoyu *çekiyor*,
yükleme kabul etmiyor: yerel dosya yolu işe yaramıyor. Render çıktısının
`public_url` alanı bu değişken ayarlandığında doluyor; ayarlanmadığında
`null` kalıyor ve yayıncı bunu açıkça söylüyor.

**Kod bunu üretemez, bir dağıtım kararıdır:** çıktı dosyası bir kabın
içinde ya da bir diskte duruyor ve internetten erişilebilir olup
olmadığını yalnızca kurulum bilir. Sessizce boş bir adres göndermek,
hatayı Meta tarafında "medya indirilemedi" diye görmek demekti.

---

## Anahtarlar

Anahtarlar ortam değişkeni **de** olabiliyor ama doğru yeri şifreli
depo:

```bash
bmai credential set youtube --hesap proje-02
```

Okuma sırası: şifreli depo → ortam değişkeni. Hangi anahtarın hangi
sağlayıcıya ait olduğu `config/providers.json` içindeki `key_env`
alanında. Kota havuzu için hesaba özel değişken adı kuralı:
`YOUTUBE_REFRESH_TOKEN` (varsayılan hesap),
`YOUTUBE_REFRESH_TOKEN_PROJE_02` (`proje-02` hesabı).
