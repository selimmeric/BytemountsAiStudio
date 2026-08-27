# İçerik Fabrikası — Mimari Analiz ve Yol Haritası

**Proje:** AI destekli, sürekli çalışan, provider-bağımsız dijital içerik üretim platformu
**Durum:** Mimari tasarım — kod yazılmadı
**Tarih:** 27 Ağustos 2026

---

## Bu doküman neyi cevaplıyor

| İstediğiniz çıktı | Bölüm |
|---|---|
| 13 — Tanımınızda eksik olan noktalar | §2 |
| 9 — Teknoloji karşılaştırması | §3 |
| 1, 14 — Genel mimari + önerilen nihai mimari | §4 |
| 2 — Modül listesi | §5 |
| 3 — Workflow mimarisi (+ D: engine kararı) | §6 |
| 4 — AI Agent mimarisi | §7 |
| 6 — Queue / Worker mimarisi | §8 |
| 7 — Provider mimarisi | §9 |
| 5 — Database mimarisi | §10 |
| 8 — Video JSON / Timeline yapısı | §11 |
| I — Video rendering mimarisi | §12 |
| J — Cost control | §13 |
| K — Quality control | §14 |
| L — YouTube / yayınlama | §15 |
| M — Ölçeklenebilirlik (10 / 100 / 1000) | §16 |
| 10 — MVP kapsamı | §17 |
| 11 — Roadmap | §18 |
| 12 — Riskler ve kritik kararlar | §19 |
| — Çok dillilik (kesişen tasarım) | §20 |
| — Karar bekleyen sorular | §21 |

---

## 0. Yönetici özeti — önce şu beş şeyi bilin

**1. YouTube upload kotası hedefinizi bugünkü hâliyle imkânsız kılıyor.**
YouTube Data API v3'te bir Google Cloud projesinin varsayılan günlük kotası **10.000 birim**, bir `videos.insert` çağrısı **1.600 birim**. Yani proje başına **günde ~6 video**. "Günde 20 video" için ya Google'dan kota artırımı (audit süreci, haftalar sürer, reddedilebilir) ya da kanal başına ayrı GCP projesi gerekir. Bu bir detay değil, **kapasite tavanınız**. Kota, LLM rate-limit'inden daha kritik ve birinci sınıf bir domain nesnesi olmalı.

**2. Akış şemanızda bir sıra hatası var: Timeline, TTS'ten SONRA üretilmeli.**
Sizin akışınız `Senaryo → Sahne/Zaman Çizelgesi → Görsel → Ses`. Ama bir sahnenin gerçek süresi ancak TTS sesi üretildikten sonra bilinir. LLM'e "bu cümle 4.8 saniye sürecek" dedirtirseniz her videoda ses–görsel kayması yaşarsınız. Doğru sıra:

```
Senaryo → TTS (segment bazlı) → gerçek süre ölçümü + kelime hizalama (ASR) → Timeline → Görsel yerleştirme → Render
```

Sahne *sınırları* senaryodan gelir, sahne *süreleri* sesten gelir.

**3. Elinizde zaten üretim kalitesinde bir render motoru var.**
`Bytemounts-Studio` (PySide6 + FFmpeg) içinde katman modeli, keyframe→FFmpeg ifade derleyicisi, transform/easing, metin overlay, thumbnail, YouTube resumable upload + retry, sürümlü proje şeması ve golden filter-graph testleri mevcut. Bunu sıfırdan yazmak en pahalı hatanız olur. Detay §1'de.

**4. Kendi workflow engine'inizi yazın — ama çok ince bir tane.**
n8n / Temporal / Hangfire karşılaştırması §6.6'da. Özet: n8n fork'u lisans + bakım + veri modeli açısından tuzak; Temporal MVP için ağır ama 1000/gün için doğru hedef; Hangfire "engine" değil "worker substrate". Öneri: **node = kuyruğa atılan iş** prensibiyle çalışan ~3–5 bin satırlık kendi DAG yorumlayıcınız, arkasında PostgreSQL. Kritik kural: *video üretme mantığı asla engine'in içinde yaşamaz.*

**5. Asıl risk teknik değil; politika ve maliyet.**
YouTube'un 2025'te netleştirdiği "inauthentic / mass-produced content" politikası tam olarak "günde 20 AI videosu üreten kanal" profilini hedefliyor. Gerçekçi maliyet: uzun video başına **$0.50–3.00**, Shorts başına **$0.05–0.30** (§13). Günde 20 video = ayda **$300–1.800**. Maliyet tavanı ve kill-switch birinci gün var olmalı.

### Alınan kararlar (27 Ağustos 2026)

| Konu | Karar | Mimariye etkisi |
|---|---|---|
| Kalite / maliyet | **Karma:** ucuz adaptörlerle başla, ölç, kalem kalem yükselt | MVP'de Pexels + ucuz TTS; provider routing (§9.3) yükseltmeyi konfigürasyona indirger. Maliyet defteri (§13) birinci gün — "ölç" kısmı buna dayanıyor |
| Render motoru | **Sıfırdan yaz, Studio'dan ders al** | ADR-001r ve ADR-002r değişti. §12 tamamen yeniden yazıldı: Filter Graph IR, Skia metin, bölüm bazlı render. Tek dil kararı (.NET) buradan çıktı |
| Dil | **En az 2 dil, sonradan daha fazlası** | Dil, sonradan eklenen bir alan değil **birinci sınıf boyut** oldu: §20. Metin dizgisi (HarfBuzz), font fallback, dile göre tekillik, araştırma≠çıktı dili |

---

## 1. Mevcut durum — elinizde zaten ne var

`BytemountsAiStudio` deposu boş; sıfırdan başlıyoruz. Ancak komşu repolarınız hem teknoloji tercihini hem de yeniden kullanım fırsatını belirliyor.

### 1.1 Tespit edilen deneyim profili

| Alan | Kanıt | Sonuç |
|---|---|---|
| Kurumsal .NET | `BQMS`, `BM.base`, `kybsv2` — API / BL / Core / DAL / WebUI katmanlı çözümler | Katmanlı, uzun ömürlü sistem tasarımına alışıksınız |
| Modern .NET + React | `Bytemounts` — .NET backend (EF Core migrations, DTO, Services) + React/TS/Vite/Tailwind | Hedef stack'in her iki ucu da tanıdık |
| Üretim seviyesi Python | `Bytemounts-Studio` — PySide6, FFmpeg filter graph, pytest + coverage gate, ruff, PyInstaller | Python burada "script dili" değil, mühendislik dili |
| Medya + YouTube | `Bytemounts-Studio/youtube`, `YoutubeTransfer` (yt-dlp, Google API, Anthropic) | OAuth, resumable upload, retry zaten çözülmüş |
| Gömülü / donanım | ESP32, STM32, PLC, BLE repoları | Kaynak sınırlı, hata toleranslı sistem düşüncesi |

### 1.2 Studio'nun referans değeri

Kod kopyalanmayacak (ADR-001r), ama `Bytemounts-Studio` bu projenin en değerli referansı: aynı problemi bir kez çözmüş, üretimde koşan bir sistem. Hangi modülden ne öğreniliyor:

| Modül | Referans değeri | Ne öğreniyoruz |
|---|---|---|
| `render/service.py` (2.360 satır) | **Kritik** | Hem doğru fikirler (düz keyframe ifadeleri, `filter_complex_script`, `.partial.mp4`) hem de kaçınılacak desenler (string birleştirme, konumsal indeksler). Ayrıntılı analiz **§12.1** |
| `core/layers.py`, `core/animation.py` | **Yüksek** | Layer + Clip + Keyframe + Transform + easing modeli doğru soyutlama; timeline şeması (§11) bu kavramları alıyor |
| `core/project_store.py`, `core/layer_adapters.py` | **Yüksek** | Sürümlü şema + göç yolu deseni — timeline `schema_version` bundan geliyor |
| `youtube/service.py` | **Yüksek** | OAuth + resumable upload + retry akışının doğrulanmış hâli; .NET'e çevrilecek davranış referansı |
| `metadata/ai` | **Yüksek** | Modeli `tool_choice` ile şemaya zorlama ve sınırları kod tarafında uygulama — tüm agent'ların standardı (§7.2) |
| `audio/` (loudness, ducking) | **Orta** | Ducking ve loudness ölçümü parametreleri |
| `batch/`, `ui/` | **Düşük** | Yerini kuyruk ve web dashboard alıyor |

**Neden aynen kullanılamıyor:** Studio'nun modeli "tek kapak + ses + spektrum"; bizim ihtiyacımız "N sahne + N görsel + TTS + çok dilli altyazı". Katman/klip modeli bunu ifade *edebilir*, ama motorun iç yapısı (string tabanlı filter graph, konumsal girdi indeksleri, UI sözlüğünün sızması) yeni gereksinimlerin altında kalırdı. Bu yüzden karar: **aynı kavramlar, yeni ve test edilebilir bir uygulama.**

> **ADR-001r (27 Ağu 2026 kararı):** Render motoru **sıfırdan ve daha temiz** yazılır; Studio'nun kodu ne paketlenir ne kopyalanır. Studio bir **ders kaynağıdır**: neyin işe yaradığı (düz keyframe ifadeleri, `filter_complex_script`, `.partial.mp4`, golden testler) ve neyin acı verdiği (435 satırlık string birleştirme, konumsal girdi indeksleri, UI sözlüğünün motora sızması) `render/service.py` okunarak çıkarıldı. Somut bulgular ve yeni tasarım **§12**'de.
>
> Bedeli: 4–6 haftalık ek iş. Karşılığı: test edilebilir bir IR katmanı, çok dilli metin dizgisi, bölüm bazlı render ve tek dilli çözüm (§12.7).

---

## 2. Gereksinim analizi — tanımınızda eksik veya örtük kalan noktalar

Tanımınız kapsam olarak çok iyi. Aşağıdakiler eksik ya da fazla iyimser varsayılmış maddeler.

### 2.1 Sert teknik sınırlar (mimariyi değiştirir)

1. **YouTube upload kotası** (10.000 birim/gün, upload = 1.600 birim → ~6 video/gün/proje). Ölçek hedefinizin gerçek tavanı. Kanal başına ayrı GCP projesi + kota audit başvurusu ayrı bir iş kalemi.
2. **Google OAuth "Testing" modunda refresh token 7 günde ölür.** Uygulamanızı "Production"a alıp doğrulatmadan sistem her hafta sessizce durur. Yayınlanmamış uygulamada hassas scope'lar için 100 test kullanıcısı sınırı da vardır.
3. **TTS süresi öngörülemez → timeline TTS sonrası üretilmeli.** Akış şemanızdaki sıra düzeltilmeli.
4. **Kelime seviyesi altyazı için ASR geri-hizalama gerekir.** TTS çıktısını Whisper benzeri bir modelden geçirip kelime zaman damgası çıkarmadan "highlight word" / kinetic typography yapılamaz. Bazı TTS sağlayıcıları karakter/kelime zamanlaması döner — bu bir provider seçim kriteri olmalı.
5. **Render throughput sistemin gerçek darboğazı.** 10 dakikalık 1080p video CPU'da 8–25 dakika sürer. AI çağrıları saniyeler, render dakikalar. Render kuyruğu ayrı havuz, ayrı eşzamanlılık limiti, muhtemelen ayrı makine.
6. **Depolama büyümesi.** Ara varlıklar video başına 200 MB–2 GB. Günde 20 video → ayda ~1 TB. İçerik-adresli (sha256) varlık deposu + retention politikası gerekir.
7. **Arama sağlayıcısı çürümesi.** Klasik Bing Search API 2025'te emekliye ayrıldı; "Google araması" için resmî ucuz bir API yok. Gerçekçi seçenekler: Brave Search API, Tavily, Exa, SerpAPI — hepsi ücretli, hepsi değişir. Provider soyutlamasının neden zorunlu olduğunun canlı kanıtı.

### 2.2 Doğruluk ve içerik kalitesi

8. **"Fact Checker Agent" tek başına yetersiz.** Bir LLM'in ürettiğini başka bir LLM'e doğrulatmak korelasyonlu hata üretir. Doğru kural: **kaynağı olmayan iddia senaryoya giremez.** Her `claim` bir kaynak URL'i + o kaynaktan alınmış birebir alıntı taşımalı; fact-checker'ın sorusu "doğru mu" değil, "bu alıntı bu iddiayı destekliyor mu" (entailment) olmalı. Bu, çözülebilir bir sorudur.
9. **Dil asimetrisi.** Türkçe içerik için kaynak çoğu konuda İngilizce. Araştırma dili ≠ senaryo dili; çeviri adımı ve terim sözlüğü gerekir.
10. **TTS metin normalizasyonu.** "1453", "%12", "M.Ö. 300", "NASA", yabancı özel isimler. Senaryo metni ile TTS metni **ayrı alanlar** olmalı (`display_text` / `speech_text`).
11. **Görsel–içerik alaka doğrulaması.** "Roma İmparatorluğu" araması Roma tatil fotoğrafı getirir. Sahne başına VLM ile alaka kontrolü gerekir ama pahalıdır → örnekleme + eşik stratejisi.

### 2.3 Hukuk, politika, para

12. **YouTube gelir politikası riski.** "Inauthentic content" (toplu üretilmiş, tekrarlayan içerik) kuralı tam bu profili hedefliyor. Mimarinin cevabı: kanal başına farklı ses/stil/format, insan onayı modu, ve *az ama iyi* moduna geçebilme.
13. **Arka plan müziği Content ID riski.** "Telifsiz" diye indirilen müzik claim yer. Sadece YouTube Audio Library, satın alınmış lisans veya üretilmiş müzik; her müzik varlığı lisans kanıtıyla saklanmalı.
14. **Stok görsel ToS'ları tekdüze değil.** Pexels / Pixabay / Unsplash izin verir ama atıf, yeniden dağıtım ve "değiştirilmemiş kopya" kuralları farklıdır. Varlık kaydındaki lisans bilgisi bir metadata değil, **uyum kaydıdır**.
15. **Web scraping sınırları.** robots.txt, paywall, ToS. Araştırma motoru "her siteyi kazı" değil, "izin verilen kaynak listesi + arama API'si + resmî API'ler" olmalı.

### 2.4 Sistem dayanıklılığı

16. **Idempotency, özellikle upload'ta.** Upload başarılı olup DB yazımı çökerse retry ikinci kez yükler. Resumable session id + idempotency key kalıcı saklanmalı.
17. **Bütçe ortada biterse ne olur?** Yarım videonun politikası tanımlı olmalı: bitir / dondur / iptal et.
18. **Hata taksonomisi.** Geçici (429, timeout) / kalıcı (400, policy reject) / zehirli (her denemede aynı çöküş). Retry politikası node tipine göre farklı; DLQ + insan triyaj ekranı şart.
19. **Global kill-switch ve devre kesici.** Kanal bazında duraklat, provider bazında circuit breaker, sistem geneli acil durdur.
20. **Prompt'lar koddur.** Sürümlenmeli, regresyon fixture'larıyla test edilmeli; bir run'ın hangi prompt sürümüyle koştuğu kaydedilmeli — yoksa "öğrenen sistem" neyin işe yaradığını ilişkilendiremez.
21. **Workflow sürümleme.** Devam eden bir run, başladığı workflow sürümüyle bitmeli.
22. **Gözlemlenebilirlik.** API → worker → FFmpeg → ASR yan servisi zinciri boyunca tek correlation id. Run detay ekranı = node bazında girdi/çıktı/maliyet/süre/log. Sonradan eklemek 3 kat pahalı.
23. **Belirsiz sistem nasıl test edilir? → fake provider seti.** Her provider arayüzünün deterministik sahte implementasyonu olmalı (sabit metin, 1×1 px görsel, 2 sn sessizlik). Tüm pipeline testleri fake'lerle koşar; gerçek provider testleri ayrı ve nadir. Studio'daki golden filter-graph testi deseni render tarafında aynen sürer.

### 2.5 Ürün ve kullanım

24. **Onay modu kullanılabilir olmalı.** Günde 20 videoda "her aşamada onay" pratikte imkânsız. Onay UX'i toplu ve mobil dostu olmalı; ayrıca *seçici onay*: sadece QC skoru eşiğin altındakiler insana düşsün.
25. **Kanallar arası adalet.** Tek kuyrukta bir kanal diğerlerini aç bırakabilir. Kanal bazlı fair scheduling gerekir.
26. **Yayın takvimi ve tempo.** Aynı anda 20 upload spam sinyali verir. Kanal başına tempo, saat dilimi, minimum aralık kuralları.
27. **"Öğrenen sistem" bir deney tasarımı problemidir, analitik problemi değil.** İzlenme farkının nedeni başlık mı, thumbnail mı, konu mu, saat mi? Karıştırıcılar ayrılmadan yapılan "öğrenme" batıl inanç üretir. Doğru yapı: tek değişkenli varyant testleri + minimum örneklem eşiği. Ayrıca YouTube Analytics verisi 24–72 saat gecikmelidir; döngü yavaştır.
28. **Çok platform ≠ çok API.** TikTok Content Posting API audit ister, Instagram Business hesap + Graph API ister, X'in video sınırları farklıdır. Soyutlama doğru; ama her platformun erişim maliyeti ayrı bir proje kalemi.

### 2.6 Fazla tanımlanmış (MVP'den çıkarılacak) noktalar

- **Browser automation ile AI görsel üretimi:** kırılgan, ToS riski taşır, bakımı sürekli. Provider mimarisinde yeri olsun ama MVP'de hiç yazılmasın.
- **Görsel workflow editörü:** MVP'de gerekmez. Workflow'u JSON olarak tanımlayın; editör Phase 3.
- **`Workers` tablosu:** worker'lar geçicidir, heartbeat dışında DB satırları olmamalı.
- **Ayrı `Voices` / `AudioFiles` tabloları:** ses ayarı konfigürasyondur, ses dosyası varlıktır; ikisi de mevcut tablolara girer.

---

## 3. Teknoloji karşılaştırması ve karar

### 3.1 Backend dili

| Kriter | C# / .NET 9 | Node.js / TS | Python |
|---|---|---|---|
| Sizin hızınız | **En yüksek** (10+ repo) | Orta (frontend'den tanıdık) | Yüksek (Studio, YoutubeTransfer) |
| Uzun vadeli sürdürülebilirlik | **Çok iyi** — statik tip, refactor güvenli | Orta | Orta (tip ipuçlarıyla iyi) |
| Uzun ömürlü stateful servis | **Çok iyi** (`BackgroundService`, DI, health checks) | İyi | Orta (GIL, süreç modeli) |
| Provider soyutlaması | **Çok iyi** (interface + DI ile doğal) | İyi | İyi (Protocol) |
| AI/medya ekosistemi | Zayıf-orta | Orta | **En iyi** (whisper, Pillow, numpy, ffmpeg sarmalayıcıları) |
| Paralel iş | **Çok iyi** (async + Channels) | İyi (tek thread + IO) | Orta |
| Hata yönetimi / gözlemlenebilirlik | **Çok iyi** (OpenTelemetry birinci sınıf) | İyi | Orta |

> **ADR-002r — Tek çalışma zamanı: .NET 9.** Workflow engine, kuyruk, API, DB, maliyet, agent'lar, **render motoru, metin kompozisyonu (SkiaSharp), thumbnail ve yayınlama** tek bir .NET çözümünde.
>
> **Tek istisna:** kelime seviyesi ses hizalaması için küçük, durumsuz bir **Python ASR yan servisi** (WhisperX). Sebebi teknik: whisper.cpp'nin attention tabanlı kelime zamanlaması kırılgan; doğru sonuç wav2vec2 ile forced alignment gerektiriyor ve bu yalnızca Python tarafında olgun. Servis tek işlevli ve dar: `wav + metin → kelime zamanları`. **TTS sağlayıcısı zaten kelime/karakter zamanlaması veriyorsa hiç çalıştırılmaz.**

ADR-001r ile Studio'yu paketleme kararı düşünce, "medya düzlemi Python olsun" gerekçesinin de temeli kalmadı. Tek dilin kazandırdıkları: timeline modelinin **tek tanımı** (iki dilde iki kopyayı senkron tutma yükü yok), tek CI hattı, tek test çerçevesi, HTTP köprüsü yok. Ayrıntılı karşılaştırma §12.7'de.

### 3.2 Veritabanı

| Kriter | PostgreSQL | MSSQL | Redis |
|---|---|---|---|
| JSON belge desteği | **JSONB + GIN index** | `NVARCHAR(MAX)` + JSON fonksiyonları (zayıf) | — |
| Kuyruk (`SKIP LOCKED`) | **Var** | `READPAST` ile mümkün ama daha kırılgan | Ayrı ürün |
| Vektör benzerlik (konu tekilliği) | **pgvector** | Yok / harici | Redis Stack |
| Lisans / maliyet | Ücretsiz | Lisans maliyeti (Express sınırlı) | Ücretsiz |
| Sizin tecrübeniz | Orta | **Yüksek** | Orta |

> **ADR-003 — PostgreSQL 16.** Tek başına belirleyici sebep **pgvector**: "Dünyanın En Tehlikeli 10 Yeri" ile "Dünyanın En Tehlikeli 10 Bölgesi"ni ayırt etmenin doğru yolu embedding benzerliğidir, LLM'e sormak değil (yavaş, pahalı, tutarsız). JSONB + `FOR UPDATE SKIP LOCKED` ikinci ve üçüncü sebep. MSSQL'e alışkınlığınız gerçek bir avantaj ama bu üç yetenek onu telafi etmiyor.
> **Redis MVP'de yok.** Kuyruk için Postgres yeterli. Redis, dağıtık rate-limit token bucket'ı gerektiğinde (Phase 4) gelir.

### 3.3 Workflow / orkestrasyon — bkz. §6.6 (detaylı karşılaştırma)

> **ADR-004 — Kendi ince DAG engine'imiz**, Postgres destekli kuyruk üzerinde. `IWorkflowEngine` arayüzü arkasında saklanır ki Phase 4'te Temporal'a geçiş bir implementasyon değişimi olsun.

### 3.4 Video motoru

| Seçenek | Değerlendirme |
|---|---|
| **FFmpeg (filter graph)** | Zaten sizde çalışıyor, en hızlı, en kontrollü, en az bağımlılık. Karmaşık kompozisyonda filter graph okunaksızlaşır — Studio'daki golden test disiplini bunu yönetiyor. **Seçim bu.** |
| MoviePy | Kolay API ama yavaş (kare kare Python), bellek yiyor, 1000/gün ölçeğinde uygun değil |
| Remotion | React ile video; güçlü ve okunaklı ama Node + headless Chrome + kare kare tarayıcı render'ı → CPU maliyeti FFmpeg'in kat kat üstü, lisans ücretli (şirket kullanımı) |
| Blender / After Effects scripting | Aşırı ağır |

> **ADR-005r — FFmpeg**, ama ham string üretimiyle değil: tipli bir **Filter Graph IR** üzerinden (§12.3). Metin ve thumbnail kompozisyonu **SkiaSharp + HarfBuzz** ile; `drawtext` hiç kullanılmaz (§12.4). SkiaSharp MIT lisanslı; ImageSharp ticari kullanımda ücretli lisans istediği için tercih edilmedi.

### 3.5 Frontend

React + TypeScript + Vite + Tailwind — zaten `Bytemounts/bytemounts-frontend` stack'iniz. Workflow editörü geldiğinde **React Flow**. TanStack Query + Zustand yeterli; ağır state kütüphanesi gerekmez.

### 3.6 Nihai stack

```
Frontend     React 19 + TS + Vite + Tailwind + React Flow + TanStack Query
API          ASP.NET Core 9 (REST + SSE canlı durum)
Orkestrasyon .NET — kendi DAG engine + Postgres kuyruk (SKIP LOCKED, lease)
Domain       .NET — Topic, Research, Script, Timeline, Publish servisleri
Provider     .NET arayüzler + adaptörler (LLM/Search/TTS/Image/Storage/Publish)
Medya        .NET — Filter Graph IR + FFmpeg subprocess + SkiaSharp/HarfBuzz
ASR (yedek)  Python — küçük WhisperX yan servisi (yalnızca TTS timing vermezse)
DB           PostgreSQL 16 + pgvector
Depolama     MVP: yerel disk (içerik-adresli) → Phase 4: S3 uyumlu (MinIO / R2)
Gözlem       Serilog + OpenTelemetry → Seq / Grafana
Dağıtım      MVP: Windows servis → Phase 4: Docker Compose / Linux
```

---

## 4. Genel mimari

### 4.1 Katmanlar

```
┌────────────────────────────────────────────────────────────────────┐
│  SUNUM         React SPA — Dashboard, Run detay, Onay kuyruğu,     │
│                Kanal ayarları, Maliyet, Workflow editörü (P3)      │
└──────────────────────────────┬─────────────────────────────────────┘
                               │ REST + SSE
┌──────────────────────────────▼─────────────────────────────────────┐
│  API / KONTROL DÜZLEMİ  (ASP.NET Core)                             │
│  ┌──────────────┬───────────────┬──────────────┬────────────────┐  │
│  │ Workflow     │ Scheduler     │ Cost &       │ Approval &     │  │
│  │ Engine       │ (tempo/cron)  │ Budget       │ Policy         │  │
│  └──────────────┴───────────────┴──────────────┴────────────────┘  │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ Job Queue (Postgres, lease + SKIP LOCKED, sınıf bazlı havuz)  │ │
│  └───────────────────────────────────────────────────────────────┘ │
└──────────────────────────────┬─────────────────────────────────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        ▼                      ▼                      ▼
┌───────────────┐   ┌────────────────────┐   ┌──────────────────┐
│ AGENT         │   │ ASSET WORKERS      │   │ MEDIA WORKERS    │
│ WORKERS       │   │                    │   │ Render (IR→      │
│ Topic, Script,│   │ Image search/gen,  │   │ FFmpeg), Skia    │
│ Research, SEO,│   │ Music, Download,   │   │ metin, Thumbnail,│
│ QC-semantik   │   │ Lisans kaydı       │   │ Upload           │
└───────┬───────┘   └─────────┬──────────┘   └────────┬─────────┘
                                                      │
                                             ┌────────▼─────────┐
                                             │ ASR SIDECAR      │
                                             │ (Python,WhisperX)│
                                             │ yalnızca yedek   │
                                             └────────┬─────────┘
        │                     │                       │
        └─────────────────────┼───────────────────────┘
                              ▼
┌────────────────────────────────────────────────────────────────────┐
│  PROVIDER KATMANI — ILLM / ISearch / ITTS / IImage / IMusic /       │
│  IAsr / IStorage / IPublisher / IAnalytics                          │
│  (rate limit, retry, circuit breaker, maliyet ölçümü BURADA)        │
└──────────────────────────────┬─────────────────────────────────────┘
                               ▼
     OpenAI · Anthropic · Gemini · ElevenLabs · Brave/Tavily ·
     Pexels/Pixabay/Unsplash · Flux/Stability · YouTube · TikTok (P6)

┌────────────────────────────────────────────────────────────────────┐
│  KALICILIK   PostgreSQL (+pgvector)  ·  Asset Store (CAS)  ·  Logs  │
└────────────────────────────────────────────────────────────────────┘
```

### 4.2 Ana döngü (sonsuz içerik modu)

```
┌──► Strategy Scheduler ──► "Kanal A'nın bugün 3 Shorts kotası var,
│         (tempo, kota,       şu an 1 boş slot var" ──► Run başlat
│          bütçe kontrolü)
│                                      │
│                                      ▼
│                          Topic Pool'dan en yüksek skorlu
│                          uygun konu seçilir (yoksa Topic Agent tetiklenir)
│                                      │
│                                      ▼
│                          ┌──────────────────────┐
│                          │  CONTENT RUN (DAG)   │
│                          │  research → script → │
│                          │  tts → timeline →    │
│                          │  assets → render →   │
│                          │  qc → publish        │
│                          └──────────┬───────────┘
│                                     ▼
│                          Publication + Metrics kaydı
│                                     │
└──────── Learning (Phase 5) ◄────────┘
          Analytics → skor ağırlıkları / prompt varyantları
```

Kritik: **konu üretimi ile içerik üretimi ayrı döngülerdir.** Topic Agent, havuzdaki "NEW" konu sayısı eşiğin altına düştüğünde arka planda çalışır; content run'ı bekletmez.

---

## 5. Modül listesi

### 5.1 .NET tarafı (`BytemountsAiStudio.*`)

| Modül | Sorumluluk |
|---|---|
| `.Core` | Domain modelleri, enum'lar, Result tipi, domain event'leri. Bağımlılığı yok. |
| `.Contracts` | Provider arayüzleri, node sözleşmeleri, DTO'lar |
| `.Persistence` | EF Core, DbContext, migration, repository'ler, CAS asset store |
| `.Queue` | Job kuyruğu, lease, retry politikaları, DLQ, fair scheduling |
| `.Workflow` | DAG modeli, run state machine, node registry, ifade değerlendirici |
| `.Agents` | Agent tanımları, prompt registry, şema doğrulama, repair döngüsü |
| `.Providers.Llm` | OpenAI / Anthropic / Gemini adaptörleri + Fake |
| `.Providers.Search` | Brave / Tavily / Exa + web fetch + robots kontrolü + Fake |
| `.Providers.Tts` | ElevenLabs / OpenAI / Azure + Fake |
| `.Providers.Image` | Pexels / Pixabay / Unsplash / Flux / OpenAI + Fake |
| `.Providers.Publish` | YouTube → sonra TikTok/IG |
| `.Domain.Topics` | Konu havuzu, skorlama, tekillik (embedding), durum makinesi |
| `.Domain.Research` | Kaynak toplama, claim çıkarma, entailment doğrulama, knowledge base |
| `.Domain.Script` | Senaryo üretimi, format şablonları, TTS normalizasyonu |
| `.Domain.Timeline` | Ses sürelerinden timeline derleme, sahne–varlık eşleme |
| `.Domain.Publishing` | Metadata, thumbnail talebi, zamanlama, kota yönetimi |
| `.Cost` | Provider çağrı defteri, tahmin, bütçe kapıları, kill-switch |
| `.Quality` | Mekanik QC kuralları + semantik QC agent orkestrasyonu |
| `.Scheduling` | Kanal tempo planlayıcı, sürekli mod, kota farkındalığı |
| `.Api` | Controller'lar, SSE, auth, validation |
| `.Worker` | Worker host — hangi kuyruklardan hangi eşzamanlılıkla tüketir |

### 5.2 Medya tarafı (`BytemountsAiStudio.Media.*`, .NET)

| Modül | Sorumluluk |
|---|---|
| `.Media.Timeline` | Timeline şeması, doğrulama, sürümleme + göç |
| `.Media.Planner` | Timeline → RenderPlan (saf) |
| `.Media.Ir` | Filter Graph IR: düğüm tipleri, `Expr`, validator, dot dökümü |
| `.Media.Emitter` | IR → `filter_complex` metni + ffmpeg argv (escape burada) |
| `.Media.Text` | SkiaSharp + HarfBuzz metin/altyazı kompozisyonu, font fallback zinciri |
| `.Media.Audio` | Segment birleştirme, loudness normalizasyonu, ducking zarfı |
| `.Media.Executor` | FFmpeg süreç yönetimi, ilerleme, iptal, ffprobe doğrulama, atomik taşıma |
| `.Media.Thumbnail` | Thumbnail kompozisyonu (aynı Skia katmanı) |
| `.Media.Publish` | YouTube resumable upload, kota rezervasyonu, kurtarma |

### 5.3 ASR yan servisi (`asr-sidecar`, Python — küçük ve tek işlevli)

FastAPI + WhisperX. Tek uç nokta: `POST /align {audio_path, text, language} → {words:[{text,start_ms,end_ms}]}`. Durumsuz, ~200 satır. Yalnızca TTS sağlayıcısı kelime zamanlaması vermediğinde çağrılır.

### 5.4 Frontend (`studio-web`)

Dashboard · Run listesi/detay (node bazlı zaman çizelgesi) · Onay kuyruğu · Konu havuzu · Kanal yönetimi · Provider & anahtar yönetimi · Maliyet paneli · Varlık gezgini · Workflow editörü (P3) · Analytics (P5)

---

## 6. Workflow mimarisi

### 6.1 Temel ilke

> **Node = kuyruğa atılan bir iş. Engine = bir sonraki node'u seçen durum makinesi. İş mantığı node'un içinde değil, domain servisinde.**

Bu üç cümle engine'in kapsamını sabitler ve "kendi n8n'imizi yazalım" derken kaybolmayı engeller.

### 6.2 Workflow tanımı (JSONB)

```jsonc
{
  "schema_version": 1,
  "key": "shorts-tr-v3",
  "name": "Türkçe Shorts Üretimi",
  "content_type": "short",
  "nodes": [
    { "id": "topic",    "type": "topic.select",     "config": { "min_score": 70 } },
    { "id": "research", "type": "research.deep",    "config": { "max_sources": 8, "depth": "standard",
                                                                "allowed_domains": ["*.edu","*.gov","wikipedia.org"] } },
    { "id": "script",   "type": "script.generate",  "config": { "format": "hook-list-payoff",
                                                                "target_seconds": 50, "model": "tier.strong" } },
    { "id": "approve1", "type": "human.approval",   "config": { "when": "channel.mode == 'approval'" } },
    { "id": "tts",      "type": "tts.synthesize",   "config": { "voice_ref": "channel.default_voice" } },
    { "id": "align",    "type": "audio.align",      "config": { "granularity": "word" } },
    { "id": "timeline", "type": "timeline.compile", "config": { "aspect": "9:16", "captions": "karaoke" } },
    { "id": "visuals",  "type": "visual.resolve",   "config": { "order": ["pexels","pixabay","ai"],
                                                                "ai_fallback": true } },
    { "id": "render",   "type": "media.render",     "config": { "preset": "shorts-1080x1920" } },
    { "id": "qc",       "type": "quality.check",    "config": { "profile": "shorts", "min_score": 75 } },
    { "id": "seo",      "type": "seo.metadata",     "config": {} },
    { "id": "thumb",    "type": "thumbnail.create", "config": { "template": "bold-title" } },
    { "id": "publish",  "type": "publish.youtube",  "config": { "visibility": "public", "schedule": "channel" } }
  ],
  "edges": [
    { "from": "topic", "to": "research" },
    { "from": "research", "to": "script" },
    { "from": "script", "to": "approve1" },
    { "from": "approve1", "to": "tts" },
    { "from": "tts", "to": "align" },
    { "from": "align", "to": "timeline" },
    { "from": "timeline", "to": "visuals" },
    { "from": "visuals", "to": "render" },
    { "from": "render", "to": "qc" },
    { "from": "qc", "to": "seo", "when": "qc.passed" },
    { "from": "qc", "to": "render", "when": "qc.retry_target == 'render'", "max_loops": 2 },
    { "from": "seo", "to": "thumb" },
    { "from": "thumb", "to": "publish" }
  ]
}
```

Notlar:
- `when` ifadeleri **çok kısıtlı bir ifade dili** (karşılaştırma, `&&`, `||`, alan erişimi). Rastgele kod yok — güvenlik ve test edilebilirlik için.
- `max_loops` olmadan QC→render döngüsü sonsuz para harcar.
- `seo` ve `thumb` aslında paralel çalışabilir; engine bunu `edges`'e bakarak zaten yapar (bir node'un tüm girdi kenarları tamamlanınca tetiklenir).

### 6.3 Run durum makinesi

```
Pending ──► Running ──┬──► WaitingApproval ──► Running
                      ├──► WaitingResource (kota/bütçe) ──► Running
                      ├──► Completed
                      ├──► Failed  (kalıcı hata / max retry)
                      └──► Cancelled (kill-switch / kullanıcı)
```

Node seviyesi: `Pending → Leased → Running → Succeeded | Failed | Skipped`

### 6.4 Yürütme döngüsü

1. Run başlatılır; giriş node'ları `Pending` yazılır ve ilgili kuyruğa iş atılır.
2. Worker işi **lease** alır (görünürlük zaman aşımı + heartbeat).
3. Node handler çalışır: `(config, run_context, inputs) → outputs + artifacts`.
4. Handler çıktıyı `node_executions` satırına yazar, işi commit eder.
5. Engine bir sonraki node'ları hesaplar (tüm girdi kenarları tamam olanlar), kuyruğa atar.
6. Worker çökerse lease süresi dolar, iş yeniden dağıtılır. **Bu yüzden node handler'lar idempotent olmak zorunda.**

Adım 4 ile 5 aynı transaction'da olmalı — yoksa "iş bitti ama sonraki node kuyruğa girmedi" durumu oluşur ve run sessizce asılı kalır.

### 6.5 İdempotency

Her node çalıştırması bir anahtar üretir:

```
idempotency_key = sha256(run_id | node_id | config_hash | input_hash)
```

Provider katmanı bu anahtarı görür: aynı anahtarla daha önce başarılı bir çağrı varsa **API'ye gitmez, kayıtlı sonucu döner**. Retry'ların ikinci kez para harcamasını engelleyen tek mekanizma budur.

### 6.6 Build vs. buy — detaylı karşılaştırma

| | Kendi engine | n8n (fork/embed) | Temporal | Hangfire | BullMQ | Quartz | Elsa 3 (.NET) |
|---|---|---|---|---|---|---|---|
| Görsel editör | Kendimiz (P3) | **Hazır, olgun** | Yok | Yok | Yok | Yok | Hazır (sınırlı) |
| Dayanıklı yürütme | Kuyruk + DB ile kurulur | Zayıf (uzun run'lar için tasarlanmadı) | **Sınıfının en iyisi** | Orta (continuation) | Orta | Yok | İyi |
| Saatlerce süren run | Tasarımı gereği uygun | Kötü | **Çok iyi** | Orta | Orta | — | İyi |
| İnsan onayı / bekleme | Doğal (run parkı) | Zorlama | **Signal ile doğal** | El ile | El ile | Yok | İyi |
| Domain verisi nerede | **Kendi tablolarımızda** | n8n execution blob'unda | Kendi tablolarımızda | Kendi | Kendi | — | Elsa tablolarında |
| Dil | C# | JS/TS (node yazımı) | Çok dilli | C# | Node.js | C# | C# |
| İşletme yükü | Düşük (Postgres) | Orta (ayrı servis+DB) | **Yüksek** (cluster veya Cloud $) | Düşük | Orta (Redis) | Düşük | Düşük |
| Lisans | — | Sustainable Use (ticari kısıt) | MIT | LGPL/ticari (Pro ücretli) | MIT | Apache | MIT |
| Öğrenme eğrisi | Düşük | Orta | **Yüksek** (determinizm, replay, versioning) | Düşük | Düşük | Düşük | Orta |
| Sizin ihtiyacınıza uyum | **Yüksek** | Düşük | Orta-yüksek (aşırı) | Parça | Düşük | Parça | Orta |

**Neden n8n değil:** (a) Lisansı ticari kullanımda kısıtlı ve fork bakımı sizin sırtınıza kalır. (b) Node'lar JS; sizin domain kodunuz C#/Python — her node için köprü yazmak zorundasınız. (c) Yürütme modeli tek seferde biten akışlar için; 25 dakikalık render + insan onayı + gün sonrası zamanlama bu modele uymuyor. (d) En önemlisi: konu, iddia, kaynak, timeline gibi **domain verileriniz n8n'in execution JSON'ı içinde hapsolur** — üzerine sorgu yazamaz, öğrenen sistemi kuramazsınız. n8n'i sistemin *dışında*, üçüncü parti entegrasyon yüzeyi olarak (webhook ile) tutmak makul olabilir; *içinde* olmamalı.

**Neden Temporal (henüz) değil:** Doğru cevap ama yanlış zaman. Determinizm kuralları, workflow versioning ve replay semantiği tek geliştirici için ciddi bir bilişsel yük; ayrıca ayrı bir cluster işletmek gerekir. Ancak §16'daki 1000/gün senaryosunda Temporal'a geçmek mantıklı hâle gelir. Bu yüzden engine `IWorkflowEngine` arkasında durur.

**Hangfire'ın yeri:** Kuyruk substratı olarak kullanılabilir, ama Postgres `SKIP LOCKED` + lease zaten yeterli ve sizin kuyruk semantiğiniz (sınıf bazlı havuz, kanal adaleti, rate-limit farkındalığı) Hangfire'ın modelinden farklı. Quartz ise sadece **tetikleyici** (cron) olarak kullanılabilir — bunun için makul.

---

## 7. AI Agent mimarisi

### 7.1 Agent nedir, ne değildir

Bu sistemde **agent = sürümlenmiş prompt + model katmanı + araç seti + çıktı şeması + doğrulayıcı + bütçe**. Agent'lar birbirleriyle serbestçe konuşmaz; orkestrasyon engine'in işidir ve deterministiktir. Ajanlar arası "sohbet" (swarm) hem maliyeti öngörülemez yapar hem hata ayıklamayı imkânsızlaştırır.

```csharp
public interface IAgent<TInput, TOutput>
{
    string Key { get; }                       // "script.generate"
    string PromptVersion { get; }             // "v7"
    ModelTier Tier { get; }                   // Cheap | Standard | Strong
    Task<AgentResult<TOutput>> RunAsync(TInput input, AgentContext ctx, CancellationToken ct);
}
```

`AgentContext` içinde: idempotency key, bütçe kalanı, correlation id, kanal ayarları, iptal token'ı.

### 7.2 Çıktı disiplini — evdeki desen

`Bytemounts-Studio/metadata/ai` zaten doğru yöntemi kullanıyor: modeli serbest metin yerine **bir aracı çağırmaya zorlamak** (`tool_choice`) ve cevabı ayrıştırmak yerine **şemayla doğrulamak**. Bu, tüm agent'lar için standart olsun.

Doğrulama zinciri her agent'ta aynı:

```
Model çağrısı
   ↓ şema doğrulaması (JSON Schema)          → başarısız: repair (maks 2)
   ↓ iş kuralları (uzunluk, dil, yasak ifade) → başarısız: repair (maks 1)
   ↓ kaynak/atıf kontrolü (varsa)             → başarısız: node fail
   ↓ maliyet kaydı
Sonuç
```

Sınırlar modele bırakılmaz, cevap geldikten sonra uygulanır (Studio'da YouTube başlık/tag sınırları için yapılan şey — aynı prensip her yerde).

### 7.3 Agent kataloğu

| Agent | Model katmanı | Girdi | Çıktı | Kritik kural |
|---|---|---|---|---|
| **Topic Generator** | Standard | Strateji + son 200 konu özeti | 10–20 aday konu | Havuzda benzeri varsa üretme (embedding ön filtresi prompt'a girer) |
| **Topic Scorer** | Cheap | Aday konu | 6 boyutlu skor + gerekçe | Skor açıklanabilir olmalı; tek sayı yetmez |
| **Research Planner** | Standard | Konu | Arama sorguları + kaynak tipleri | Sorgu dili ≠ içerik dili olabilir |
| **Research Agent** | Standard + araçlar | Plan | Kaynak listesi + alıntılar | **Tek gerçek araç döngüsü olan agent.** Adım sayısı + bütçe ile sınırlı |
| **Claim Extractor** | Cheap | Kaynak metinleri | `claim[]` + alıntı span | Alıntısız claim üretemez |
| **Entailment Checker** | Cheap-Standard | (claim, alıntı) çiftleri | destekliyor / çelişiyor / ilgisiz | Farklı model ailesi kullan (korelasyonlu hatayı kır) |
| **Script Agent** | **Strong** | Doğrulanmış claim'ler + format | Bölümlü senaryo, `display_text` + `speech_text` | Knowledge base dışına çıkamaz |
| **Scene Planner** | Standard | Senaryo | Sahne sınırları + görsel prompt'ları | Süre **vermez** — süre TTS'ten gelir |
| **Visual Director** | Standard | Sahne | Arama terimleri + AI prompt + stil | Kanal görsel stiline uyar |
| **Visual Relevance QC** | VLM (Cheap) | (görsel, sahne metni) | alaka skoru | Örnekleme ile çalışır (her sahne değil) |
| **SEO Agent** | Standard | Senaryo + konu | Başlık/açıklama/tag/kategori | Platform sınırları kod tarafında kırpılır |
| **Thumbnail Director** | Standard | Konu + senaryo | Kompozisyon talimatı | Üretimi Skia katmanı yapar, agent sadece tasarlar |
| **Quality Control Agent** | Standard | Render raporu + senaryo + örnek kareler | QC bulguları + retry hedefi | Sadece **semantik** kontroller; mekanik olanlar koddadır |

### 7.4 Model katmanlama

Tek model her işe koşulmaz. Kanal ayarında üç katman tanımlanır ve agent'lar katmana bağlanır:

```jsonc
"models": {
  "cheap":    { "provider": "openai",    "model": "…mini" },
  "standard": { "provider": "anthropic", "model": "…sonnet" },
  "strong":   { "provider": "anthropic", "model": "…opus" }
}
```

Böylece maliyet ayarı tek yerden yapılır ve provider değişimi agent kodunu etkilemez. Ölçek büyüdüğünde skorlama/çıkarım gibi hacimli işleri ucuz katmana indirmek maliyeti yarıya düşürebilir.

### 7.5 Prompt registry

Prompt'lar dosya sisteminde sürümlü tutulur (`prompts/script.generate/v7.md`), derlemeye gömülür, DB'de sadece **hangi run hangi sürümü kullandı** kaydı durur. Her prompt sürümünün bir **eval fixture seti** olur: 10–20 örnek girdi ve beklenen özellikler (uzunluk aralığı, şema geçerliliği, kaynak kullanımı). Prompt değiştirdiğinizde fixture'lar koşar. Bu, "öğrenen sistem"in ön şartıdır.

---

## 8. Queue / Worker mimarisi

### 8.1 Kaynak sınıfına göre kuyruk

Tek kuyruk yanlış olur: 2 saniyelik LLM çağrısıyla 25 dakikalık render aynı havuzda olamaz.

| Kuyruk | Eşzamanlılık (tek makine) | Timeout | Retry | Darboğaz |
|---|---|---|---|---|
| `llm` | 8–16 | 120 sn | 5, exp backoff | Provider rate limit |
| `search` | 4–8 | 60 sn | 3 | API kotası |
| `asset` (indirme/görsel) | 8 | 120 sn | 3 | Ağ / disk |
| `image-gen` | 2–4 | 180 sn | 2 | Maliyet |
| `tts` | 2–4 | 300 sn | 3 | Karakter kotası |
| `align` (ASR) | 1–2 | 600 sn | 2 | CPU/GPU |
| `render` | **1–2** | 3600 sn | 2 | **CPU/GPU — sistemin darboğazı** |
| `upload` | 1–2 | 1800 sn | 5 | **YouTube kotası** |

### 8.2 Lease modeli

```sql
UPDATE jobs
SET state='leased', leased_by=@worker, lease_expires_at=now()+@ttl, attempt=attempt+1
WHERE id = (
  SELECT id FROM jobs
  WHERE state='pending' AND queue=@queue AND run_after<=now()
    AND channel_id NOT IN (SELECT channel_id FROM paused_channels)
  ORDER BY priority DESC, fair_key, created_at
  FOR UPDATE SKIP LOCKED
  LIMIT 1
)
RETURNING *;
```

- Worker çalışırken `lease_expires_at`'i periyodik uzatır (heartbeat).
- Worker çökerse lease dolar → süpürücü işi `pending`'e döndürür. Kurtarma bundan ibaret.
- `fair_key`: kanal başına round-robin (örn. `channel_id`'nin son bitirdiği iş zamanı) — bir kanal diğerlerini aç bırakmaz.

### 8.3 Rate limit ve devre kesici

Rate limit **worker başına değil, provider hesabı başına** olmalı. MVP'de Postgres'te token bucket satırı yeterli (`provider_accounts.tokens`, `refilled_at`); Phase 4'te Redis. İş sırası:

```
Kuyruk → lease → rate limit izni (yoksa run_after=+X, geri bırak)
       → circuit breaker açık mı? (açıksa geri bırak)
       → bütçe kapısı → provider çağrısı → maliyet kaydı → sonuç
```

**Devre kesici:** bir provider'da art arda N hata → 5 dk açık → yarı açık deneme. Açıkken o provider'a giden işler kuyrukta bekler, run başarısız olmaz. Fallback provider tanımlıysa ona düşer.

### 8.4 Retry ve DLQ

| Hata sınıfı | Örnek | Davranış |
|---|---|---|
| Geçici | 429, 502, timeout, ağ | Exponential backoff + jitter, maks N |
| Kalıcı | 400, geçersiz prompt, policy reject | Retry yok → node fail → run `Failed` |
| Zehirli | Aynı hata her denemede | 2. denemeden sonra DLQ |
| Kaynak | Kota bitti, bütçe doldu | `WaitingResource` — retry değil, **erteleme** |

DLQ bir tablo ve bir ekran: hata, girdi, son log, "yeniden dene" / "run'ı iptal et" / "node'u atla" aksiyonları.

### 8.5 Worker host

MVP'de tek `.NET Worker` süreci tüm kuyrukları konfigürasyonla tüketir; `render` kuyruğunun eşzamanlılığı ayrı ve düşüktür. ASR yan servisi ayrı bir süreçtir ve yalnızca `align` işi düştüğünde HTTP ile çağrılır. Phase 4'te worker'lar kuyruk sınıfına göre ayrı makinelere dağılır — kod değişmez, sadece konfigürasyon.

---

## 9. Provider mimarisi

### 9.1 Arayüzler

```csharp
public interface ILlmProvider {
    string Key { get; }                                   // "anthropic"
    Task<LlmResult> CompleteAsync(LlmRequest r, CancellationToken ct);
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    ProviderCapabilities Capabilities { get; }            // tool use, json mode, vision, ctx
}

public interface ISearchProvider {
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery q, CancellationToken ct);
}

public interface IWebFetchProvider {                       // robots.txt + rate limit burada
    Task<FetchedDocument> FetchAsync(Uri url, CancellationToken ct);
}

public interface ITtsProvider {
    Task<TtsResult> SynthesizeAsync(TtsRequest r, CancellationToken ct);
    bool SupportsWordTimings { get; }                      // seçim kriteri
    Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(CancellationToken ct);
}

public interface IImageProvider {
    ImageProviderKind Kind { get; }                        // Stock | Generative
    Task<IReadOnlyList<ImageCandidate>> FindAsync(ImageQuery q, CancellationToken ct);   // Stock
    Task<ImageResult> GenerateAsync(ImagePrompt p, CancellationToken ct);                // Generative
}

public interface IAsrProvider  { Task<AlignmentResult> AlignAsync(AlignRequest r, CancellationToken ct); }
public interface IMusicProvider{ Task<MusicResult> SelectAsync(MusicQuery q, CancellationToken ct); }
public interface IStorageProvider {
    Task<AssetRef> PutAsync(Stream s, AssetMeta m, CancellationToken ct);   // içerik-adresli
    Task<Stream> OpenAsync(AssetRef r, CancellationToken ct);
    Task<Uri> GetLocalPathAsync(AssetRef r, CancellationToken ct);          // render worker için
}
public interface IPublisher {
    string Platform { get; }                               // "youtube"
    PublishCapabilities Capabilities { get; }              // max süre, en-boy, metadata sınırları
    Task<PublishResult> PublishAsync(PublishRequest r, CancellationToken ct);
}
public interface IAnalyticsProvider {
    Task<IReadOnlyList<MetricSnapshot>> FetchAsync(PublicationRef p, DateRange d, CancellationToken ct);
}
```

### 9.2 Dekoratör zinciri

Her provider aynı sarmalayıcılardan geçer — bu mantık adaptörlerin içine **asla** yazılmaz:

```
Agent/Node
   │
   ▼
[ Idempotency ]  → önceki başarılı sonuç varsa döner, çağrı yapılmaz
[ Budget Gate ]  → tahmini maliyet bütçeyi aşıyorsa reddeder
[ RateLimit   ]  → provider hesabı bazında token bucket
[ CircuitBreak]  → provider sağlıksızsa erken durur
[ Retry       ]  → sınıflandırılmış hata + backoff
[ Metering    ]  → token/karakter/görsel/saniye sayar, `provider_calls`'a yazar
[ Telemetry   ]  → süre, correlation id, span
   │
   ▼
Gerçek adaptör (HTTP)
```

### 9.3 Seçim ve yedekleme

Provider seçimi bir **politika**dır, kod değil:

```jsonc
"routing": {
  "tts": [
    { "provider": "elevenlabs", "when": "content.language=='tr' && channel.tier=='premium'" },
    { "provider": "openai-tts", "when": "true" }
  ],
  "image.stock": ["pexels", "pixabay", "unsplash"],
  "image.generative": [
    { "provider": "flux", "on_error_fallback": "openai-image" }
  ]
}
```

### 9.4 Fake provider seti — pazarlık dışı

Her arayüzün `Fake*` implementasyonu **birinci gün** yazılır: sabit senaryo metni, 1×1 renkli PNG, N saniyelik sessizlik WAV'ı, sabit arama sonuçları. Tüm entegrasyon testleri ve tüm yerel geliştirme bunlarla koşar. Faydası: pipeline'ı sıfır maliyetle ve deterministik olarak uçtan uca çalıştırabilirsiniz. Bu olmadan her hata ayıklama turu para harcar ve tekrarlanabilir olmaz.

### 9.5 Düşük API bütçesi modu

Sınırlı API erişimi bu projede bir kısıt değil, **tasarım girdisi**. Provider mimarisi zaten yönlendirmeyi politikaya bağladığı için, "ücretsiz/yerel önce" bir konfigürasyon meselesidir:

| İhtiyaç | Ücretsiz / yerel yol | Notlar |
|---|---|---|
| Arama | **SearXNG** (kendi sunucunuzda, Docker) | Meta arama motoru, JSON API'si var, anahtar gerekmez. En sağlam ücretsiz yol |
| Arama (yedek) | DuckDuckGo · Brave API ücretsiz kotası | Tek sağlayıcıya bağlanmamak için |
| Ansiklopedik bilgi | **Wikipedia + Wikidata API** | Ücretsiz, resmî, sınırsıza yakın. "En iyi 10'lar / tarih / gizem" içeriğinin büyük kısmı buradan gelir |
| Sayfa içeriği | **Playwright** ile tarayıcı render'lı çekme | JS ile yüklenen sayfalar düz HTTP ile alınamaz |
| Ucuz LLM işleri | **Ollama** ile yerel model (Qwen / Llama / Gemma) | Konu skorlama, claim çıkarma, normalizasyon, sınıflandırma — hacimli ve basit işler. Ücretsiz ve sınırsız |
| Orta LLM işleri | Gemini / OpenRouter ücretsiz kotaları | Katman `standard` |
| Güçlü LLM | Ücretli | Yalnız senaryo — video başına 1–2 çağrı |
| Kelime zamanlaması | **WhisperX** yerel, ya da TTS'in kendi timing'i | Sıfır maliyet |

Model katmanlaması (§7.4) bu modun anahtarı: hacimli işler `cheap` katmanında yerel modele düşer, para yalnız `strong` katmanında harcanır. Ölçüm `provider_calls` üzerinden yapıldığı için hangi katmanın ne kadara mal olduğu görünür — "ucuz başla, ölç, yükselt" kararı buna dayanıyor.

> **ADR-015 — Ücretsiz/yerel önce, ücretli yalnız gerektiğinde.** Varsayılan yönlendirme SearXNG + Wikipedia + Ollama; ücretli sağlayıcılar yalnız kalite farkının ölçüldüğü yerlerde açılır.
>
> **Yapılmayacak:** AI web arayüzlerini (ChatGPT / Gemini paneli vb.) tarayıcı otomasyonuyla sürmek. Bu servislerin kullanım şartlarına aykırıdır, bot tespitine takılır, her arayüz değişiminde kırılır ve hesabı riske atar. Yukarıdaki yol aynı sonucu meşru biçimde veriyor. Tarayıcı otomasyonu **yalnız açık web sayfalarının içeriğini çekmek** için kullanılır ve robots.txt ile izinli alan listesine uyar.

### 9.6 Kimlik bilgisi yönetimi

- API anahtarları ve OAuth token'ları DB'de **şifreli** durur (Windows'ta DPAPI, sonra ASP.NET Data Protection ile anahtar halkası; Phase 4'te KeyVault/Vault).
- Kanal başına kimlik bilgisi seti: bir kanalın YouTube token'ı diğerini etkilemez.
- Token yenileme merkezî; süresi dolan token için proaktif yenileme işi.
- Log'lara anahtar sızmaması için provider katmanında redaction.

---

## 10. Veri modeli

### 10.1 Tasarım kararları

- **Grafik JSONB'de**, ayrı `workflow_nodes` tablosu yok — node'lar üzerinde sorgu ihtiyacınız yok, join maliyeti karşılıksız.
- **`content_items` platform-bağımsız**; `publications` platform bazlı. Böylece TikTok/blog eklemek şema değişikliği gerektirmez.
- **`assets` içerik-adresli** (sha256 birincil): aynı görsel 40 videoda kullanılsa tek satır, tek dosya.
- **`provider_calls` maliyet defteridir** — her para harcayan çağrı buraya yazılır; maliyet raporları buradan türetilir, ayrı `costs` tablosu yok.
- **`workers`, `voices`, `audio_files`, `render_jobs`, `youtube_uploads` tabloları yok** — sırasıyla: geçici, konfigürasyon, varlık, iş, yayın.

### 10.2 Şema (özet)

```sql
-- ===== Kimlik & yapılandırma =====
channels(id, name, platform_defaults jsonb, language, voice_config jsonb,
         visual_style jsonb, model_tiers jsonb, publishing_schedule jsonb,
         mode, /* auto | approval | selective */ state, created_at)

channel_credentials(id, channel_id, kind, /* youtube_oauth, ... */
         encrypted_payload bytea, expires_at, updated_at)

provider_accounts(id, provider_key, label, encrypted_credentials bytea,
         quota_config jsonb, tokens numeric, refilled_at, state)

-- ===== Strateji & konu =====
content_strategies(id, channel_id, name, seed_topic, mode, /* once | continuous */
         daily_target int, type_mix jsonb, constraints jsonb, state)

topics(id, strategy_id, channel_id, title, angle, language,
       scores jsonb,           -- interest/trend/researchability/visual/competition/evergreen/overall
       overall_score numeric,  -- generated column, index'li
       embedding vector(1536), -- pgvector: benzerlik ile tekillik
       state, /* new,queued,in_progress,published,failed,rejected */
       rejected_reason, published_content_id, created_at, updated_at)
-- index: ivfflat (embedding vector_cosine_ops), (state, overall_score DESC)

-- ===== Workflow & çalıştırma =====
workflows(id, key, name, content_type, channel_id nullable, current_version int)
workflow_versions(id, workflow_id, version, graph jsonb, created_at, created_by)

runs(id, workflow_version_id, channel_id, topic_id, content_item_id,
     state, priority, context jsonb,   -- node çıktıları: { "script": {...}, "tts": {...} }
     estimated_cost numeric, actual_cost numeric,
     started_at, finished_at, error jsonb)

node_executions(id, run_id, node_id, node_type, attempt,
     state, input_hash, idempotency_key,
     output jsonb, cost numeric, duration_ms, error jsonb, started_at, finished_at)
-- unique: (run_id, node_id, attempt)

run_events(id, run_id, node_id, level, message, data jsonb, at)  -- dashboard zaman çizelgesi

-- ===== Kuyruk =====
jobs(id, queue, run_id, node_id, channel_id, priority, fair_key,
     payload jsonb, state, attempt, max_attempts,
     run_after, leased_by, lease_expires_at, last_error, created_at)
-- index: (queue, state, run_after) WHERE state='pending'

dead_letters(id, job_id, run_id, node_id, error jsonb, payload jsonb, at, resolved_at, resolution)

-- ===== İçerik =====
content_items(id, channel_id, topic_id, kind, /* video | short | blog | post */
     language, title, state, created_at)

research_sessions(id, content_item_id, plan jsonb, state, cost numeric, finished_at)
sources(id, research_session_id, url, domain, source_type, title,
     fetched_at, content_hash, license_note, trust_score)
claims(id, research_session_id, text, source_id, quote text,
     entailment, /* supported | contradicted | unrelated */
     confidence numeric, used_in_script bool)

scripts(id, content_item_id, version, format, total_chars,
     document jsonb,  -- bölümler, display_text/speech_text, claim referansları
     approved_by, approved_at, created_at)

audio_segments(id, content_item_id, script_section_id, asset_id,
     duration_ms, provider_key, voice_id, word_timings jsonb, created_at)

timelines(id, content_item_id, version, schema_version,
     document jsonb, content_hash, duration_ms, created_at)

assets(sha256 pk, kind, /* image | video | audio | music | font | output */
     mime, bytes bigint, width, height, duration_ms,
     storage_path, source_provider, source_url, author,
     license, license_url, license_captured_at, created_at)

asset_usages(id, asset_id, content_item_id, role, scene_index)

renders(id, content_item_id, timeline_id, preset, output_asset_id,
     state, engine_version, duration_ms, render_ms, log_path,
     probe jsonb,  -- ffprobe çıktısı: çözünürlük, bitrate, loudness
     created_at)

quality_reports(id, content_item_id, render_id, profile, score,
     checks jsonb,  -- [{check, severity, passed, detail}]
     passed bool, retry_target, created_at)

-- ===== Yayın & ölçüm =====
publications(id, content_item_id, channel_id, platform, external_id,
     metadata jsonb,  -- başlık/açıklama/tag/kategori/playlist
     thumbnail_asset_id, visibility, scheduled_at, published_at,
     state, idempotency_key, upload_session jsonb)
-- unique: (platform, external_id), unique: idempotency_key

publication_metrics(publication_id, captured_at, views, impressions, ctr,
     avg_view_duration_s, retention_curve jsonb, likes, comments, subs_gained)
-- pk: (publication_id, captured_at)

-- ===== Yönetim =====
approvals(id, run_id, node_id, kind, payload jsonb, state,
     decided_by, decided_at, note)
provider_calls(id, run_id, node_id, provider_key, operation,
     units jsonb,  -- {input_tokens, output_tokens} | {characters} | {images} | {seconds}
     cost numeric, latency_ms, http_status, at)
budgets(id, scope, /* global | channel | run */ scope_id, period,
     limit_amount numeric, spent_amount numeric, action_on_exceed, period_start)
audit_log(id, actor, action, entity, entity_id, data jsonb, at)

-- ===== Phase 5 =====
experiments(id, channel_id, hypothesis, variable, state, started_at, ended_at)
experiment_arms(id, experiment_id, name, config jsonb, publication_ids uuid[])
```

### 10.3 Neden bu ayrım

- `scripts` ↔ `timelines` ayrı: senaryo insanın okuduğu belge, timeline makinenin okuduğu belge. Aynı senaryodan 9:16 ve 16:9 iki timeline üretilebilir.
- `claims` ayrı tablo: "hangi videoda hangi kaynağı kullandık" sorusu bir gün hukuki olarak sorulabilir; ayrıca fact-check'in denetlenebilir olmasının tek yolu.
- `assets` sha256 birincil anahtar: tekilleştirme + render cache + "bu görseli kaç videoda kullandık" sorgusu bedava gelir.
- `runs.context` JSONB: node çıktılarının kanonik yeri `node_executions`; `context` sadece hızlı erişim için türetilmiş görünüm.

---

## 11. Video JSON / Timeline yapısı

### 11.1 İki belge, bir yön

```
Script Document          →  insanın onayladığı, LLM'in ürettiği anlatı
       ↓ (TTS + hizalama)
Timeline Document        →  makinenin render ettiği, tamamen çözümlenmiş belge
```

**Timeline değişmez kuralı:** Timeline'da hiçbir "sonra bulunacak" alan olamaz. Her varlık sha256 ile çözümlenmiş, her süre ölçülmüş, her metin normalize edilmiştir. Render worker internete çıkmaz. Bu kural sayesinde: render tekrarlanabilir, cache'lenebilir (timeline hash = çıktı kimliği) ve test edilebilir.

### 11.2 Şema

```jsonc
{
  "schema_version": 1,
  "content_item_id": "…",
  "language": "tr-TR",
  "text_direction": "ltr",
  "font_stack": ["Inter", "Noto Sans", "Noto Color Emoji"],
  "canvas": { "width": 1080, "height": 1920, "fps": 30, "background": "#0B0D10" },
  "duration_ms": 52480,

  "audio": {
    "voice_track": {
      "segments": [
        { "id": "s1", "asset": "sha256:a1b2…", "start_ms": 0,     "duration_ms": 4820,
          "speech_text": "Bin dört yüz elli üçte…", "script_section": "hook" },
        { "id": "s2", "asset": "sha256:c3d4…", "start_ms": 4820,  "duration_ms": 7210,
          "speech_text": "…", "script_section": "item_10" }
      ],
      "gain_db": 0.0, "target_lufs": -16.0
    },
    "music_track": {
      "asset": "sha256:e5f6…", "loop": true, "gain_db": -22.0,
      "ducking": { "enabled": true, "target_gain_db": -30.0, "attack_ms": 150, "release_ms": 600 },
      "fade_in_ms": 1200, "fade_out_ms": 2000,
      "license": { "source": "youtube_audio_library", "id": "…" }
    },
    "sfx": []
  },

  "scenes": [
    {
      "index": 0, "start_ms": 0, "end_ms": 4820,
      "voice_segments": ["s1"],
      "visual": {
        "asset": "sha256:9f8e…", "kind": "image",
        "fit": "cover",
        "motion": { "type": "ken_burns", "from": { "scale": 1.00, "x": 0.0, "y": 0.0 },
                    "to":   { "scale": 1.12, "x": 0.03, "y": -0.02 }, "easing": "ease_in_out" }
      },
      "overlays": [
        { "type": "text", "text": "1453", "style_ref": "big_number",
          "start_ms": 400, "end_ms": 2200,
          "anim": { "in": "pop", "out": "fade" } }
      ],
      "transition_out": { "type": "fade", "duration_ms": 300 }
    }
  ],

  "captions": {
    "enabled": true,
    "style_ref": "shorts_karaoke",
    "cues": [
      { "start_ms": 120, "end_ms": 460,  "text": "Bin",       "segment": "s1", "emphasis": false },
      { "start_ms": 460, "end_ms": 980,  "text": "dört yüz",  "segment": "s1", "emphasis": true }
    ]
  },

  "persistent_layers": [
    { "type": "image", "asset": "sha256:1010…", "role": "watermark",
      "anchor": "top_right", "margin": [40, 40], "opacity": 0.55 }
  ],

  "styles": {
    "big_number":      { "font": "Inter-Black", "size_pct": 14, "color": "#FFFFFF",
                         "stroke": { "color": "#000000", "width": 6 }, "align": "center" },
    "shorts_karaoke":  { "font": "Inter-Bold", "size_pct": 6.5, "color": "#FFFFFF",
                         "highlight_color": "#FFD400", "box": { "color": "#000000", "opacity": 0.35 },
                         "position": { "anchor": "bottom_center", "offset_pct": 18 },
                         "max_lines": 2 }
  },

  "output": {
    "preset": "shorts-1080x1920",
    "container": "mp4", "video_codec": "h264", "crf": 20, "preset_speed": "medium",
    "audio_codec": "aac", "audio_bitrate": "192k",
    "pix_fmt": "yuv420p"
  },

  "provenance": {
    "script_id": "…", "prompt_versions": { "script.generate": "v7", "scene.plan": "v3" },
    "generated_at": "2026-08-27T09:12:00Z", "engine_min_version": "1.0.0"
  }
}
```

### 11.3 Tasarım gerekçeleri

- **Süreler `_ms` ve tam sayı.** Float saniyeler kare hesaplarında yuvarlama hatası üretir.
- **`styles` referansla kullanılır**, sahne içine gömülmez: bir kanalın altyazı stilini değiştirmek tek satırdır ve stil kanal şablonundan gelir.
- **`scenes[].visual` tek varlık**, çoklu değil. Bir sahnede iki görsel gerekiyorsa iki sahnedir. Bu, modeli basit ve render'ı öngörülebilir tutar.
- **`captions.cues` kelime seviyesinde**, çünkü ASR hizalamasından geliyor. Cümle altyazısı bunlardan türetilir; tersi mümkün değil.
- **`provenance` zorunlu:** hangi prompt sürümü hangi videoyu üretti — öğrenme döngüsünün temeli.
- **`engine_min_version`:** eski timeline'ı yeni motor açarken uyumluluk kararı verebilsin.
- Bu şema Studio'nun `Layer`/`LayerClip`/`AnimationKeyframe` modeline doğrudan derlenir; `motion` bir keyframe çiftidir, `overlays` bir metin katmanıdır, `transition_out` bir opacity keyframe'idir. Yani yeni bir render semantiği icat etmiyoruz.

---

## 12. Video rendering mimarisi — sıfırdan temiz tasarım

> **Karar:** Render motoru sıfırdan yazılır (ADR-001r). Studio'nun kodu kopyalanmaz; **dersleri** alınır. Aşağıdaki tasarım, Studio'nun `render/service.py`'sinin gerçek okunmasından çıkarılmıştır.

### 12.1 Studio'dan çıkan dersler

`Bytemounts-Studio/src/bytemounts_studio/render/service.py` — 2.360 satır, içinde 435 satırlık tek bir `build_filter_graph` fonksiyonu. Somut bulgular:

| # | Gözlem (kanıt) | Neden sorun | Yeni tasarımda karşılığı |
|---|---|---|---|
| L1 | Filtreler f-string ile birleştirilen **ham metin**; tek soyutlama string concat (`service.py:1394-1829`) | Grafiği inceleyemez, doğrulayamaz, yeniden sıralayamazsınız. Tek güvenlik ağı nihai metnin golden testidir | **Filter Graph IR**: tipli düğüm/kenar nesneleri; metin en sonda üretilir |
| L2 | Girdi indeksleri konumsal ve elle korunan bir teamüle bağlı — ARCHITECTURE.md'de yazıyor: *"klip en son girdi olarak eklenir, böylece indeksler değişmez — golden testlerin dayandığı garanti budur"* | Doğruluk bir konvansiyona bağlı. Yeni girdi tipi eklemek tüm indeksleri kaydırır | **İsimli akışlar** (`bg`, `scene_3`, `voice`); indeksleri emitter en son atar |
| L3 | UI sözlüğü render çekirdeğinde: `project.camera_motion == "Slow Zoom In"` (`service.py:1470`) | Sunum katmanının string'leri motorun içinde. Dil değişse render bozulur | `KenBurns(from, to, easing)` gibi **değer nesneleri**; preset eşlemesi kenarda |
| L4 | Her fonksiyon 40+ alanlı `ProjectSettings` tanrı nesnesini alıyor | Bağımlılık örtük; bir parçayı izole test etmek için tüm projeyi kurmak gerekir | Her derleyici fonksiyonu **tam olarak ihtiyacı olanı** alır |
| L5 | `time_offset` kesişen bir kaygı, elle her keyframe çağrısına taşınıyor | Bir yerde unutulursa sessiz kayma | Zaman **tek tip** (`Ms` tamsayı) + `TimeBase` bir kez uygulanır |
| L6 | Font metriği, Pillow çizimi, FFmpeg escape, codec argümanları, süreç yönetimi, loudness — hepsi tek dosyada | Değişiklik yarıçapı büyük, test yüzeyi geniş | 5 ayrı katman, aralarında tek yönlü bağımlılık |
| L7 | Metin iki yoldan çiziliyor (Pillow overlay **ve** FFmpeg drawtext) | İki farklı ölçüm/kırpma davranışı, iki escape kuralı | **Tek yol: Skia.** `drawtext` hiç kullanılmaz |

**Korunacak iyi fikirler** (bunlar zor kazanılmış, aynen taşınır):

- Keyframe'lerin iç içe değil **düz toplam ifadelere** derlenmesi — FFmpeg'in ifade derinliği sınırına takılmayı engelliyor
- `-filter_complex_script` ile grafiği dosyadan geçirmek — komut satırı uzunluk sınırı
- `.partial.mp4` → başarıdan sonra taşıma — yarım dosya asla "başarılı çıktı" sanılmaz
- Golden testler (artık nihai metin yerine **IR topolojisi** üzerinde, çok daha okunaklı)
- İçerik-hash'e göre önbellek; bağımsız karelerin süreç havuzunda üretimi

### 12.2 Mimari: dört saf aşama + bir yan etkili

```
Timeline (JSON, tamamen çözümlenmiş)
   │
   ▼  ①  PLANNER            saf: timeline → RenderPlan
   │       • girdi bildirimleri (isimli), sahne→akış eşlemesi, süre matematiği
   │       • hiç FFmpeg bilgisi yok
   ▼  ②  COMPILER           saf: RenderPlan (+ Capabilities) → FilterGraph IR
   │       • tipli düğümler: Scale, Crop, Overlay, Zoompan, Concat, AFade, Sidechain…
   │       • pad bağlantıları nesne referansı, string değil
   ▼  ③  VALIDATOR          saf: IR → hata listesi
   │       • her pad tam bir kez tüketildi mi, döngü var mı, medya tipi uyuyor mu
   ▼  ④  EMITTER            saf: IR → filter_complex metni + argv
   │       • escape/quoting SADECE burada; indeks ataması burada
   ▼  ⑤  EXECUTOR           tek yan etkili katman
           • süreç, ilerleme, iptal, ffprobe doğrulama, atomik taşıma
```

Kazanç şu: ①–④ **saf fonksiyonlar**. Testler girdi verip çıktı karşılaştırır; FFmpeg çalıştırmaya gerek yok. Studio'da bunların hepsi tek fonksiyonda olduğu için tek test yöntemi nihai 12 KB'lık metni karşılaştırmaktı.

### 12.3 Filter Graph IR

```csharp
public sealed record StreamRef(string Id, MediaKind Kind);          // "scene3.v", Video

public abstract record FilterNode {
    public required IReadOnlyList<StreamRef> Inputs  { get; init; }
    public required IReadOnlyList<StreamRef> Outputs { get; init; }
}
public sealed record Scale(int W, int H, ScaleMode Mode)      : FilterNode;
public sealed record Crop(Expr X, Expr Y, int W, int H)       : FilterNode;
public sealed record Overlay(Expr X, Expr Y, TimeRange? When) : FilterNode;
public sealed record Zoompan(Expr Zoom, Expr X, Expr Y, int Frames, Size Out) : FilterNode;
public sealed record Concat(int Segments, bool Video, bool Audio) : FilterNode;
public sealed record SidechainDuck(double ThresholdDb, int AttackMs, int ReleaseMs) : FilterNode;

public sealed record FilterGraph(
    IReadOnlyList<InputDecl> Inputs,     // isimli: dosya yolu + tip + döngü/ss
    IReadOnlyList<FilterNode> Nodes,
    StreamRef VideoOut, StreamRef AudioOut);
```

`Expr` ayrı bir küçük tip: sabit sayı ya da derlenmiş keyframe ifadesi. Böylece "bu bir ifade mi sabit mi" sorusu tip sisteminde cevaplanır, string içinde değil.

**Validator kuralları** (hepsi FFmpeg çalıştırmadan):
pad tekil tüketim · dangling çıkış yok · döngü yok · video pad'i video girdisine · `enable` aralıkları klip süresinin içinde · çözünürlük zinciri tutarlı · girdi dosyaları mevcut.

**Hata ayıklama:** IR → Graphviz `dot` dökümü. Render patladığında 12 KB metne değil, bir resme bakarsınız. (FFmpeg'in kendi `graph2dot` aracının yaptığının aynısı, ama derleme öncesinde.)

### 12.4 Metin ve altyazı: tek yol, Skia

`drawtext` **hiç kullanılmaz**. Gerekçeler:

1. **Dizgi (shaping) yok.** Türkçe idare eder ama Arapça bitişik yazı, Hint dilleri, CJK satır kırma, emoji — `drawtext` bunları doğru dizemez. Çok dilli hedef (§20) bunu doğrudan dışlıyor.
2. **Escape cehennemi.** Metindeki `:`, `'`, `\`, `%` karakterleri filtre sözdizimiyle çakışır; Studio'nun escape kodu bu yüzden var.
3. **Karaoke kontrolü yok.** Kelime vurgusu için satırın tamamını çizip yalnızca o kelimeyi boyamak gerekir — Studio bunu Pillow'da yapıyor, doğru karar.

Yerine: **SkiaSharp + HarfBuzz** ile şeffaf PNG üretimi. Lisans da bunu destekliyor — SkiaSharp MIT, ImageSharp ise ticari kullanımda ücretli lisans isteyen "Six Labors Split License" altında.

**Kare dizisi tuzağından kaçınma:** karaoke altyazı için saniyede 30 PNG üretmek gereksiz. Bir altyazı satırındaki her *kelime vurgusu durumu* tek bir PNG'dir — 5 kelimelik satır = 5 PNG, her biri `overlay ... : enable='between(t,a,b)'` ile gösterilir. 50 saniyelik Shorts'ta ~120 küçük PNG; 1.500 kare yerine. Tam senkron, düşük maliyet.

### 12.5 Bölüm bazlı render

Uzun video tek FFmpeg çağrısıyla render edilmez:

```
sahne grupları → segment render (paralel, N worker) → segment_0.mp4 … segment_k.mp4
                                                     → concat demuxer → master
                                                     → ses karışımı + altyazı overlay → final
```

Kazançlar: paralelleşir · bellek sabit kalır · bir segment bozulursa yalnız o yeniden üretilir (QC retry'ı ucuzlatır) · segment hash'i önbellek anahtarıdır, senaryonun 3. bölümü değiştiğinde yalnız o segment yeniden render edilir.

Dikkat: segmentler **aynı kodlayıcı parametreleriyle** üretilmeli, yoksa concat sırasında yeniden kodlama gerekir. Anahtar kare hizası segment sınırına zorlanır (`-force_key_frames`).

### 12.6 Yürütme ve doğrulama

```
IR → filter_complex.txt → ffmpeg (-progress pipe:) → out.partial.mp4
   → ffprobe doğrulama: süre ±%1 · çözünürlük · ses akışı var · loudness · true peak
   → geçerse atomik taşıma → CAS'e yaz → renders satırı + probe raporu
```

- **Render worker durumsuz ve ağa kapalı.** Girdi: timeline + yerel varlıklar. Çıktı: mp4 + probe + log. Ayrı makineye taşımak bir konfigürasyon değişikliği.
- **Önbellek anahtarı:** `hash(IR kanonik hâli + engine_version + ffmpeg_version)`. Timeline'ı hash'lemekten iyidir; motor sürümü ve yetenek farkını da kapsar.
- **Kodlayıcı:** MVP'de libx264 (`crf 20, preset medium`). NVENC Phase 4'te ve **kalite ölçülerek** — aynı bitrate'te x264'ten düşük kalitelidir.
- **İlerleme:** `-progress` çıktısı ayrıştırılıp `run_events`'e yazılır → dashboard'da gerçek yüzde.

### 12.7 Dil kararı: motor hangi dilde yazılsın?

Studio'yu paketlemek masadan kalkınca, "kontrol .NET + medya Python" ayrımının ana gerekçesi de ortadan kalktı. Yeniden değerlendirme:

| Kriter | .NET (SkiaSharp) | Python (Pillow) |
|---|---|---|
| Timeline modelinin tek tanımı | **Tek yerde** — DTO'lar paylaşılır | İki dilde iki kopya, sürekli senkron yükü |
| Metin dizgi (shaping) | **SkiaSharp.HarfBuzz** — tam shaping | Pillow'da shaping sınırlı (raqm gerekir) |
| Görsel kompozisyon hızı | **Skia (C++)** — yüksek | Pillow — orta |
| Lisans | SkiaSharp **MIT** | Pillow HPND — sorunsuz |
| Süreç/iptal/işlem yönetimi | **Güçlü** (`Process`, `CancellationToken`) | Orta |
| Test/CI | **Tek çözüm, tek pipeline** | İkinci CI hattı |
| ASR (kelime hizalama) | Zayıf — whisper.cpp'nin attention tabanlı zamanlaması **kırılgan** | **Güçlü** — WhisperX (faster-whisper + wav2vec2 forced alignment) en doğru sonucu veriyor |

> **ADR-002r — Tek çözüm: .NET.** Render motoru, metin kompozisyonu, thumbnail ve yayınlama .NET'te yazılır. **Tek istisna:** kelime seviyesi hizalama gerektiğinde çağrılan **küçük, durumsuz bir Python ASR yan servisi** (WhisperX). Bu servis 200 satırdan küçüktür ve tek işi vardır: `wav + metin → kelime zamanları`.
>
> Üstelik çoğu zaman hiç çalışmaz: **TTS sağlayıcısı kelime/karakter zamanlaması döndürüyorsa ASR atlanır.** Bu yüzden `ITtsProvider.SupportsWordTimings` bir seçim kriteridir (§9.1) — timing veren sağlayıcı tercih edilir, ASR yalnızca yedektir.

### 12.8 Test stratejisi

| Seviye | Ne test edilir | Hız |
|---|---|---|
| Birim | Planner/Compiler saf fonksiyonları, `Expr` derleyicisi, zaman matematiği | ms |
| Topoloji (golden) | IR'ın kanonik JSON dökümü — okunabilir diff, string değil | ms |
| Emitter (golden) | IR → filter_complex metni; küçük ve mekanik | ms |
| Piksel | Bilinen timeline → belirli karelerin hash'i (SSIM toleranslı) | sn |
| Entegrasyon | Gerçek FFmpeg ile uçtan uca kısa render + ffprobe doğrulaması | dk |

İlk üç seviye CI'da her commit'te koşar ve FFmpeg gerektirmez. Studio'da mümkün olmayan şey buydu.

---

## 13. Maliyet kontrolü

### 13.1 Video başına gerçekçi maliyet

> Fiyatlar Mayıs 2026 bilgisiyle, **büyüklük mertebesi** olarak verilmiştir; uygulamadan önce her provider'ın güncel fiyatı doğrulanmalıdır.

**Shorts (~50 sn, ~700 karakter):**

| Kalem | Ucuz kurgu | Premium kurgu |
|---|---|---|
| Araştırma (arama + fetch + LLM) | $0.02 | $0.10 |
| Senaryo (strong model) | $0.02 | $0.08 |
| TTS | $0.01 (ucuz TTS) | $0.15 (premium ses) |
| Görseller | $0.00 (stok) | $0.20 (5 AI görsel) |
| ASR hizalama | $0.00 (yerel whisper) | $0.01 |
| Render + depolama | $0.01 | $0.02 |
| **Toplam** | **~$0.06** | **~$0.56** |

**Uzun video (~10 dk, ~9.000 karakter):**

| Kalem | Ucuz | Premium |
|---|---|---|
| Derin araştırma | $0.10 | $0.50 |
| Senaryo + sahne planı | $0.10 | $0.40 |
| TTS | $0.13 | $1.80 |
| Görseller (15–25 adet) | $0.00 | $0.80 |
| ASR + render + depolama | $0.05 | $0.15 |
| **Toplam** | **~$0.38** | **~$3.65** |

**Günde 20 video (14 Shorts + 6 uzun):** ucuz kurguda ~$3/gün (~$90/ay), premium kurguda ~$30/gün (~$900/ay). Aradaki fark neredeyse tamamen **TTS ve AI görsel** kalemlerinde. İlk kararınız bu iki kalemde nerede duracağınız olmalı.

### 13.2 Mekanizma

```
Run başlamadan:  tahmin = Σ (node tipi × geçmiş ortalama birim maliyet)
                 tahmin > channel.max_cost_per_video ise → run başlamaz, konu ertelenir

Her provider çağrısında:  ön kontrol (bütçe kalanı) → çağrı → gerçek maliyet kaydı

Bütçe kapıları:   run bütçesi   → aşılırsa: node fail, run Failed(BudgetExceeded)
                  kanal günlük  → aşılırsa: kanal Paused, bekleyen run'lar WaitingResource
                  global aylık  → aşılırsa: KILL SWITCH — tüm kuyruklar durur, bildirim
```

`budgets.action_on_exceed`: `pause` | `finish_running_then_pause` | `hard_stop`. Varsayılan **`finish_running_then_pause`** olmalı — yarım kalan videolar harcanmış para demektir.

### 13.3 Maliyet görünürlüğü

Dashboard'da: bugün/bu ay harcama, kanal bazında dağılım, **node tipi bazında dağılım** (nerede para yandığını görmenin tek yolu), video başına ortalama, ve "en pahalı 10 run". `provider_calls` tablosu bunların hepsini tek kaynaktan besler.

---

## 14. Kalite kontrol

Sizin listeniz doğru ama iki farklı şeyi karıştırıyor. Ayırın:

### 14.1 Mekanik kontroller (kod, ücretsiz, deterministik)

Bunlar agent değil; `Quality` modülünde saf fonksiyonlar:

| Kontrol | Yöntem | Ağırlık |
|---|---|---|
| Video süresi timeline ile uyumlu (±%1) | ffprobe | **Bloklayıcı** |
| Çözünürlük ve en-boy hedefe uygun | ffprobe | **Bloklayıcı** |
| Ses kanalı var, sessiz değil | ffprobe + loudness | **Bloklayıcı** |
| Loudness hedef aralıkta (-14…-18 LUFS) | ffmpeg loudnorm | **Bloklayıcı** |
| Clipping (true peak > -1 dBTP) | ffmpeg | Uyarı |
| Müzik/konuşma oranı makul | segment analizi | Uyarı |
| Her sahnede görsel var | timeline | **Bloklayıcı** |
| Altyazı süresi ses süresini aşmıyor | timeline | **Bloklayıcı** |
| Tüm claim'lerin kaynağı var | DB | **Bloklayıcı** |
| Metadata sınırları (başlık ≤100, tag toplam ≤500) | kod | **Bloklayıcı** |
| Thumbnail var, boyut/oran doğru | dosya | **Bloklayıcı** |
| Aynı konu daha önce yayınlanmamış | embedding | **Bloklayıcı** |

### 14.2 Semantik kontroller (AI, pahalı, örneklemeli)

| Kontrol | Yöntem | Ne zaman |
|---|---|---|
| Görsel sahneyle alakalı mı | VLM, sahne örneklemi (%30) | Her video |
| Senaryo iddiaları knowledge base ile tutarlı mı | entailment, örneklem | Her video |
| Ton/dil kanal kimliğine uygun mu | LLM | Her video |
| Başlık tıklama tuzağı mı / yanıltıcı mı | LLM | Her video |
| Hassas/riskli içerik var mı (politika) | LLM sınıflandırıcı | Her video |

### 14.3 Karar

```
score = Σ(ağırlık × geçti)   →  bloklayıcı bir kontrol düştüyse score önemsiz, retry
score ≥ 85  → otomatik yayın
70–85       → insan onayı kuyruğuna (selective mode)
< 70        → retry_target belirle (render / visuals / script) → maks 2 döngü → Failed
```

`retry_target` kritik: QC'nin çıktısı "kötü" değil, **"hangi node'a dön"** olmalı. Aksi hâlde tüm pipeline baştan koşar ve para yanar.

---

## 15. YouTube ve yayınlama

### 15.1 Kota — planlamanın merkezinde

| İşlem | Birim |
|---|---|
| `videos.insert` (upload) | 1.600 |
| `thumbnails.set` | 50 |
| `playlistItems.insert` | 50 |
| `search.list` | 100 |
| Okuma çağrıları | 1 |

Günlük 10.000 birim → **~6 upload/gün/proje**. Sonuçlar:

- `provider_accounts` her GCP projesi için bir satır ve **kota bütçesi**dir. Upload kuyruğu bu bütçeyi bilir; kota bitince işler `WaitingResource` olur ve ertesi güne kayar (hata değil).
- 20 video/gün için ya kota artırım audit'i (başvuru + inceleme, garantisi yok) ya çoklu proje. Çoklu projeyi Google'ın kullanım şartlarına aykırı biçimde (aynı uygulamanın kotasını bölmek için) kullanmak hesap kapanmasına yol açabilir — bunu **kanal başına gerçek ayrı uygulama** olacak şekilde kurgulayın.
- `search.list` çağrılarından kaçının (100 birim!). Rakip analizi için ayrı proje/kota kullanın.

### 15.2 Upload akışı

```
render tamam → metadata hazır → thumbnail hazır
   ↓
kota rezervasyonu (atomik: tokens -= 1600)  → yoksa ertele
   ↓
publications satırı: state=Uploading, idempotency_key yazılır
   ↓
resumable upload başlat → upload_session jsonb'ye kaydedilir (çökerse kaldığı yerden)
   ↓
başarılı → external_id kaydet → thumbnails.set → playlist ekle
   ↓
state=Published | Scheduled
```

Çökme kurtarma: `state=Uploading` ve `upload_session` dolu olan kayıtlar başlangıçta taranır; YouTube'a session durumu sorulur, tamamlanmışsa **yeniden yüklenmez**, sadece kayıt düzeltilir. Çift yükleme sorununun tek doğru çözümü budur.

### 15.3 Zamanlama

Kanal başına yayın planı: gün içi saat pencereleri, minimum aralık (örn. 90 dk), günlük tavan. Scheduler upload'ı `scheduled_at` ile YouTube'a `private + publishAt` olarak verir; böylece kota gündüz harcanır, yayın istenen saatte olur — kota ve tempo birbirinden ayrılır. **Bu, kota sıkışıklığını çözen en pratik numara.**

### 15.4 Analytics (Phase 5)

YouTube Analytics API ile günlük çekim: views, impressions, CTR, ortalama izlenme süresi, retention eğrisi, abone. Veri 24–72 saat gecikmeli; ilk anlamlı ölçüm yayından ~3 gün sonra. `publication_metrics` zaman serisi olarak tutulur (tek satır güncelleme değil) — trend analizi ancak böyle mümkün.

---

## 16. Ölçeklenebilirlik

### 16.1 Günde 10 video (MVP hedefi)

- Tek makine (mevcut Windows geliştirme makineniz yeterli).
- Postgres kuyruk, in-process worker'lar, yerel disk (CAS).
- Render: ~2–4 saat/gün CPU → sorun yok.
- YouTube: **tek GCP projesiyle sınırda** (6/gün). 10 için ikinci proje ya da kota artırımı gerekir.
- Maliyet: ucuz kurguda ~$1–2/gün.

### 16.2 Günde 100 video

Değişenler:

| Alan | Değişiklik |
|---|---|
| Render | **Ayrı render makineleri (2–4 adet).** ~25 saat/gün render işi tek makineye sığmaz |
| Depolama | S3 uyumlu nesne deposuna geç (MinIO / Cloudflare R2). Aylık ~5 TB ara varlık → agresif retention (ara varlıklar 7 gün) |
| Kuyruk | Postgres hâlâ yeterli, ama rate-limit için Redis gelir |
| YouTube | **~17 GCP projesi veya kota artırımı zorunlu.** Gerçekçi çözüm: 10–20 kanal, her biri kendi projesi |
| DB | Connection pool, `node_executions` ve `run_events` için partition (aylık) |
| Gözlem | Merkezî log (Seq/Loki) + metrik (Prometheus/Grafana) zorunlu |
| Maliyet | Ucuz kurguda ~$10–20/gün, premium ~$150–300/gün |

### 16.3 Günde 1000 video

Burada mimarinin şekli değişir:

| Alan | Değişiklik |
|---|---|
| Orkestrasyon | **Temporal'a geçiş değerlendirilir.** 1000 eşzamanlı uzun run'da kendi engine'inizin operasyonel yükü artar |
| Render | GPU fleet (NVENC), 20–40 worker; bölüm bazlı paralel render + concat zorunlu |
| Depolama | Sıcak/soğuk katman, çıktı videoları yükleme sonrası 30 günde arşive |
| Kuyruk | Redis Streams veya NATS JetStream; Postgres kuyruk bu yükte yazma darboğazı olur |
| DB | Okuma replikası, `provider_calls` ve `run_events` için TimescaleDB veya ayrı analitik store |
| Dağıtım | Docker + Linux + orkestratör (Nomad/K8s). Windows servis modeli burada biter |
| YouTube | **Tıkanma noktası burası.** 1000 upload/gün = ~167 GCP projesi. Pratikte imkânsız → çok platform + çok hesap + iş ortaklığı seviyesi erişim gerekir |

> **Dürüst değerlendirme:** 1000 video/gün'de sınırlayıcı faktör altyapı değil, **platform politikası ve içerik kalitesidir**. Bu ölçekte YouTube'un spam/inauthentic content filtreleri devreye girer. Mimariyi 100/gün için sağlam kurun, 1000/gün'ü ancak çok platform + çok hesap stratejisiyle ve o günün kurallarına göre planlayın.

---

## 17. MVP kapsamı

### 17.1 MVP'nin tek cümlelik tanımı

> **Bir kanal için, bir konudan başlayıp, insan onayıyla YouTube'a yüklenen Türkçe Shorts videosunu uçtan uca, tekrarlanabilir ve maliyeti ölçülen biçimde üretebilmek.**

### 17.2 Kapsam İÇİ

| Alan | MVP kapsamı |
|---|---|
| Kanal | **2 kanal — her dil için bir tane** |
| Dil | **2 dil.** Bilerek MVP'de: dil soyutlamasının gerçekten çalıştığını kanıtlamanın tek yolu ikinci dili baştan koşturmaktır. Üçüncü dil sonra konfigürasyonla gelir |
| İçerik türü | **Sadece Shorts** (9:16, 30–60 sn) |
| Workflow | JSON tanımlı, **sabit tek graf**, engine gerçek (DAG + kuyruk + retry) |
| Konu | Topic Agent + skorlama + havuz + **embedding ile tekillik** |
| Araştırma | 1 arama sağlayıcı + web fetch + claim çıkarma + entailment kontrolü |
| Senaryo | Script Agent, `display_text`/`speech_text` ayrımı |
| Ses | 1 TTS sağlayıcı (ucuz katman), dil başına ses, segment bazlı, gerçek süre ölçümü |
| Hizalama | Öncelik: TTS'in döndürdüğü kelime zamanları. Yedek: Python WhisperX yan servisi |
| Görsel | 1 stok sağlayıcı (Pexels) + 1 AI görsel sağlayıcı (fallback) |
| Timeline | Tam şema (§11), Ken Burns + fade + altyazı |
| Render | **Yeni motor:** Planner → IR → Validator → Emitter → Executor. Ken Burns, fade, Skia altyazı, watermark |
| QC | **Tüm mekanik kontroller** + 1 semantik kontrol (görsel alaka) |
| Onay | Senaryo sonrası ve yayın öncesi insan onayı |
| Yayın | YouTube upload + thumbnail + metadata, kota farkındalığı, idempotent |
| Maliyet | `provider_calls` defteri + run tahmini + kanal günlük bütçe + kill-switch |
| Dashboard | Run listesi, run detay (node zaman çizelgesi + log + maliyet), onay kuyruğu, konu havuzu, DLQ |
| Provider | **Tüm arayüzler + Fake implementasyonlar** + her tipten 1 gerçek adaptör |

### 17.3 Kapsam DIŞI (bilerek)

Görsel workflow editörü · İkiden fazla dil · Uzun video · Sürekli otonom mod · Öğrenme döngüsü / analytics · Browser automation · Arka plan müziği (Phase 2) · Çoklu platform · Blog/podcast içerik türleri · Aynı içerikten çok dilli türev (§20.7) · Kullanıcı/rol yönetimi · Docker/dağıtık kurulum · GPU render

### 17.4 MVP'nin "mükemmel olması gereken" tek parçası

**Run gözlemlenebilirliği.** Diğer her şey değişecek; ama "bu video neden böyle oldu, hangi node ne üretti, ne kadara mal oldu, hangi prompt sürümü kullanıldı" sorusunu cevaplayamayan bir sistemde ne kaliteyi artırabilir ne maliyeti düşürebilirsiniz. Bunu sonradan eklemek üç kat pahalıdır.

---

## 18. Yol haritası

### Phase 0 — İskelet ve yürüyen iskelet (4–5 hafta)

**Amaç:** Sistem uçtan uca çalışsın; içeriği sahte olsun.

- Solution yapısı, Postgres + EF Core migration, CAS asset store
- Job kuyruğu (lease, retry, DLQ), workflow engine (DAG + run state machine)
- Tüm provider arayüzleri + **Fake implementasyonlar**
- Timeline şeması v1 + doğrulayıcı
- **Render motoru çekirdeği:** Planner → IR → Validator → Emitter → Executor; Skia metin katmanı; golden topoloji testleri; `dot` dökümü
- CLI: `run workflow shorts-fake --topic "test"` → gerçek mp4 çıkar (sahte metin, düz renk görseller, sessizlik sesi)

**Çıktı:** Sıfır para harcayarak, tek komutla, tekrarlanabilir bir video üreten iskelet. Bu iskelet sonradan hiç değişmeyecek olan omurgadır.

> Süre ADR-001r yüzünden 2–3 haftadan 4–5 haftaya çıktı — render motoru artık hazır koddan değil, sıfırdan geliyor. Bu, bilinçli olarak ödenen bedel.

### Phase 1 — Gerçek Shorts hattı (3–4 hafta)

**Amaç:** İnsan onayıyla gerçek bir Shorts yayınlanabilsin.

- Gerçek adaptörler: LLM, arama, TTS, stok görsel, AI görsel (**ucuz katman** — karma karar gereği)
- Research → claim → entailment zinciri; `research_language ≠ content_language` yolu
- Script Agent + `speech_text` normalizasyonu (dil başına) + TTS + kelime zamanları + timeline derleme
- **İkinci dil:** ikinci kanal, font fallback zinciri, dile göre tekillik — abstraction'ı burada kanıtlıyoruz
- Render (Ken Burns, Skia altyazı, watermark), mekanik QC
- YouTube upload (kota, idempotency, resumable kurtarma), thumbnail
- Dashboard v1: run listesi/detay, onay kuyruğu, maliyet defteri

**Çıktı:** İlk gerçek video YouTube'da. Video başına gerçek maliyet ölçülmüş.

### Phase 2 — Otonomi ve dayanıklılık (3–4 hafta)

- Topic Pool otomatik doldurma + embedding tekillik + skorlama
- Scheduler: kanal tempo, günlük hedef, kota farkındalığı, sürekli mod
- Bütçe kapıları, kill-switch, circuit breaker, kanal adaleti
- Semantik QC + `retry_target` ile hedefli yeniden çalıştırma
- Arka plan müziği + ducking, lisans kaydı
- Selective approval (sadece düşük skorlular insana)
- DLQ triyaj ekranı, gözlemlenebilirlik (OpenTelemetry, correlation id)

**Çıktı:** Sistem gece boyunca kendi başına çalışıp sabaha 3–5 video hazırlıyor.

### Phase 3 — Çoklu kanal, uzun video, workflow editörü (4–6 hafta)

- Çoklu kanal + kanal başına kimlik/ayar/stil/ses
- Uzun video formatı: derin araştırma, bölüm bazlı render + concat, intro/outro
- React Flow ile görsel workflow editörü + node ayar formları + sürümleme
- Prompt registry UI + eval fixture koşumu
- Varlık gezgini, lisans raporu

**Çıktı:** Farklı kanallar farklı workflow'larla çalışıyor; workflow'lar UI'dan düzenleniyor.

### Phase 4 — Ölçek (3–5 hafta)

- Render worker'ların ayrı makineye çıkarılması, iş dağıtımı
- S3 uyumlu nesne deposu + retention politikaları
- Redis: dağıtık rate-limit + circuit breaker durumu
- Çoklu GCP projesi / kota havuzu yönetimi
- Docker + Linux dağıtımı, sağlık kontrolleri, otomatik yeniden başlatma
- `IWorkflowEngine` arkasında Temporal spike'ı (karar noktası)

**Çıktı:** 100 video/gün kapasitesi.

### Phase 5 — Öğrenen sistem (4–6 hafta)

- YouTube Analytics günlük çekim, `publication_metrics` zaman serisi
- Deney çerçevesi: tek değişkenli varyantlar (thumbnail A/B, başlık A/B), minimum örneklem
- Konu skorlama ağırlıklarının gerçek performansla kalibrasyonu
- Prompt varyant performans raporu
- "Ne işe yarıyor" dashboard'u

**Çıktı:** Konu seçimi ve metadata üretimi ölçülmüş verilerle yönleniyor.

### Phase 6 — Çok platform, çok içerik türü (açık uçlu)

- `IPublisher` altında TikTok / Instagram Reels / X
- Aynı içerikten farklı rendition'lar (9:16 / 1:1 / 16:9, süre kırpma)
- Blog/makale/sosyal post içerik türleri (aynı knowledge base'den)
- Podcast (sadece ses rendition'ı)

---

## 19. Riskler ve kritik mimari kararlar

### 19.1 Risk kaydı

| # | Risk | Etki | Olasılık | Azaltma |
|---|---|---|---|---|
| R1 | YouTube upload kotası ölçeği tıkar | **Yüksek** | **Kesin** | Phase 1'de kota artırım başvurusu; kanal=proje modeli; `publishAt` ile tempo ayrımı |
| R2 | YouTube "inauthentic content" politikası → demonetizasyon/kapatma | **Kritik** | Orta-yüksek | Kanal başına farklı stil, insan onayı, kalite eşiği, az-ama-iyi moduna geçebilme |
| R3 | Maliyet kontrolsüz büyür | Yüksek | Orta | Bütçe kapıları + kill-switch Phase 1'de; ucuz/premium kurgu ayrımı |
| R4 | Hatalı bilgi yayını → itibar/strike | Yüksek | Orta | Kaynaksız claim yasağı, entailment kontrolü, insan onayı |
| R5 | Render throughput yetmez | Orta | Yüksek | Ayrı render havuzu, bölüm bazlı render, Phase 4'te fleet |
| R6 | Provider kapanır/fiyat değiştirir (bkz. Bing) | Orta | **Yüksek** | Provider soyutlaması + routing politikası + fallback |
| R7 | Workflow engine kapsam kayması | Yüksek | **Yüksek** | "Node = iş, mantık domain'de" kuralı; ifade dili kısıtlı; editör Phase 3 |
| R8 | Sıfırdan render motoru Phase 0'ı uzatır / eksik kalır | Yüksek | Orta | Kapsam MVP'de dar: Ken Burns + fade + altyazı + watermark. Efekt zenginliği Phase 3. IR sayesinde yeni filtre eklemek ucuz |
| R8b | Tek geliştirici bakım yükü | Orta | Düşük | ADR-002r ile tek dile inildi; Python yüzeyi tek dosyalık ASR servisine indi |
| R9 | OAuth token'ları sessizce ölür | Orta | **Yüksek** | Production'a alma + doğrulama; proaktif yenileme; token sağlık ekranı |
| R10 | Telifli müzik/görsel → Content ID claim | Orta | Orta | Lisans kaydı zorunlu; yalnızca beyaz listedeki kaynaklar |
| R11 | Aynı içeriğin tekrar üretimi | Düşük-orta | Yüksek | pgvector benzerlik eşiği + yayınlanmış konu kontrolü QC'de bloklayıcı |
| R12 | "Öğrenme" yanlış sonuç çıkarır | Orta | Yüksek | Deney tasarımı + minimum örneklem; otomatik strateji değişimi Phase 5 sonuna kadar kapalı |

### 19.2 Kritik mimari kararlar (ADR özeti)

| ADR | Karar | Gerekçe | Geri dönüş maliyeti |
|---|---|---|---|
| **001r** | Render motoru **sıfırdan** yazılır; Studio ders kaynağıdır | Test edilebilir IR, çok dilli dizgi, bölüm bazlı render; teknik borç devralınmaz | Orta |
| **002r** | **Tek dil: .NET.** İstisna: küçük Python ASR yan servisi | Timeline modelinin tek tanımı, tek CI; ASR'da Python gerçekten daha iyi | Orta |
| 003 | PostgreSQL + pgvector | Konu tekilliği, JSONB, SKIP LOCKED kuyruk | Yüksek (sonradan değişmez) |
| 004 | Kendi ince DAG engine'i, `IWorkflowEngine` arkasında | Domain verisi bizde; Temporal'a yol açık | Orta |
| **005r** | FFmpeg, ama **tipli IR** üzerinden; metin yalnızca Skia+HarfBuzz | String birleştirme test edilemez; `drawtext` çok dilli dizgi yapamaz | Yüksek |
| 006 | Timeline TTS'ten sonra derlenir | Senkron sorununun tek doğru çözümü | Düşük |
| 007 | Timeline tamamen çözümlenmiş belgedir; render ağa çıkmaz | Tekrarlanabilirlik, cache, test | Düşük |
| 008 | Kaynaksız claim senaryoya giremez | Halüsinasyona karşı yapısal savunma | Düşük |
| 009 | Fake provider seti birinci gün | Deterministik test, sıfır maliyetli geliştirme | Düşük |
| 010 | Idempotency key her node çalıştırmasında | Retry'ın çift ödeme/çift upload yapmasını engeller | Orta |
| 011 | Kota birinci sınıf kaynak (bütçe gibi) | YouTube tavanı planlamanın merkezinde | Orta |
| 012 | Prompt'lar sürümlü ve run'a kaydedilir | Öğrenme döngüsünün ön şartı | Düşük |
| **013** | Dil birinci sınıf boyut; MVP iki dille çıkar | Sonradan çok dilli yapmak en pahalı dönüşümdür | Yüksek |
| **014** | Bölüm bazlı render + segment önbelleği | Paralellik, sabit bellek, ucuz QC retry | Orta |
| **015** | Ücretsiz/yerel önce (SearXNG + Wikipedia + Ollama) | Sınırlı API bütçesi bir kısıt değil, tasarım girdisi (§9.5) | Düşük |

---

## 20. Çok dillilik — kesişen tasarım

Karar: en az iki dil, ileride daha fazlası. Dil sonradan eklenen bir alan değil, **sistemin birinci sınıf boyutu**. Sonradan eklenmesi en pahalı özellik türüdür; şimdi doğru kurulursa yeni dil eklemek bir konfigürasyon satırıdır.

### 20.1 Dil, tek bir alan değil — dört ayrı kavram

Bunları karıştırmak en yaygın hatadır:

| Kavram | Anlamı | Nerede yaşar |
|---|---|---|
| `research_language` | Kaynakların dili | `research_sessions` |
| `content_language` | Senaryonun ve seslendirmenin dili | `content_items`, `scripts` |
| `audience_locale` | Sayı/tarih/para biçimi, kültürel referans | `channels` |
| `ui_language` | Panelin dili | kullanıcı tercihi |

Türkçe içerik çoğu konuda İngilizce kaynaktan üretilecek — yani `research_language ≠ content_language` **normal durumdur**, istisna değil. Boru hattı buna göre kurulur:

```
Konu (content_language: tr)
   → Araştırma planı: sorgular EN + TR olarak üretilir
   → Kaynaklar toplanır (çoğu EN)
   → Claim'ler KAYNAK DİLİNDE çıkarılır ve doğrulanır   ← kritik
   → Senaryo TR yazılır, claim'ler referans alınarak
```

Claim'i kaynak dilinde tutmanın sebebi: çeviri, doğrulamayı bozar. "Bu alıntı bu iddiayı destekliyor mu" sorusu ancak ikisi aynı dildeyken güvenilir cevaplanır. Çeviri en son, senaryo yazımında olur.

### 20.2 Terim sözlüğü

Kanal başına `glossary`: özel isimler, teknik terimler, çevrilmeyecek kelimeler, tercih edilen karşılıklar. Senaryo agent'ının prompt'una girer. Olmadan aynı kavram videodan videoya farklı çevrilir ve kanal tutarsız görünür.

### 20.3 Konuşma metni normalizasyonu dile bağlıdır

`display_text` / `speech_text` ayrımı (§2.2) burada karşılığını buluyor. Normalizasyon kuralları dile göre değişir:

| Girdi | tr-TR | en-US |
|---|---|---|
| `1453` | "bin dört yüz elli üç" | "fourteen fifty-three" |
| `%12` | "yüzde on iki" | "twelve percent" |
| `M.Ö. 300` | "milattan önce üç yüz" | "three hundred B C" |
| `$4.5M` | "dört buçuk milyon dolar" | "four point five million dollars" |

Bu bir `ISpeechNormalizer` arayüzüdür, dil başına implementasyon. Kural tabanlı başlanır; LLM'e bırakmak tutarsızlık üretir ve her seferinde para harcar.

### 20.4 Metin dizgisi ve fontlar — motorun gereksinimi

Bu, §12.4'teki "Skia, asla drawtext" kararının asıl gerekçesi:

- **Shaping:** Latin + Türkçe için `drawtext` idare eder; Arapça bitişik yazı, Farsça, Hintçe, Tayca **doğru dizilmez**. HarfBuzz zorunlu.
- **Font fallback zinciri:** tek font tüm dilleri kapsamaz. Kanal başına sıralı liste — ör. `[Inter, Noto Sans, Noto Sans Arabic, Noto Sans CJK, Noto Color Emoji]`. Eksik glif bir sonrakinden alınır. Fallback yoksa ekranda tofu (□□□) çıkar ve bunu ancak izleyici fark eder.
- **RTL:** Arapça/İbranice için `direction` bayrağı ve hizalama tersine döner; altyazı kutusu sağdan büyür.
- **Satır kırma:** CJK'da boşluk yoktur; kırma kuralları dile bağlıdır.
- **Metin genişlemesi:** Aynı cümle Almancada ~%30 uzar. Altyazı kutusu ve başlık yerleşimi sabit piksel değil, **ölçülen metin genişliğine** göre hesaplanmalı — Skia zaten bunu ölçüyor.

Fontlar varlık deposunda tutulur (`assets.kind = 'font'`), lisansıyla birlikte. Noto ailesi (SIL OFL) güvenli varsayılan.

### 20.5 Tekillik dile göre bölünür

`topics` tekillik kontrolü (§10.2, pgvector) **kanal + dil** kapsamında yapılır. "Dünyanın En Tehlikeli 10 Yeri" TR kanalında yayınlanmışsa, EN kanalında "10 Most Dangerous Places" **tekrar değildir** — farklı izleyici. Ama aynı kanalda aynı dilde tekrar olur.

Ayrıca embedding'ler diller arası karşılaştırılabilir olmalı → **çok dilli embedding modeli** seçilmeli (dile özel model kullanılırsa TR ve EN vektörleri aynı uzayda olmaz). Bu bir provider seçim kriteridir.

### 20.6 Ses, metadata, altyazı

- **Ses:** `channels.voice_config` dil başına ses kimliği tutar. TTS sağlayıcısının o dildeki kalitesi eşit değildir — dil başına sağlayıcı yönlendirmesi (§9.3) gerekir.
- **Metadata:** SEO agent'ı hedef dilde ve **hedef pazarın arama alışkanlığıyla** çalışır; başlığı çevirmek yetmez. Ayrıca YouTube'un çoklu dil başlık/açıklama özelliği kullanılabilir (tek video, birden çok dilde metadata).
- **Altyazı:** Yayınlanan videoya ayrıca **SRT altyazı dosyası** yüklenir (yakılmış altyazıdan bağımsız). Kelime zamanları zaten elimizde; SRT üretmek bedava ve erişilebilirliği ile keşfedilebilirliği artırır.

### 20.7 Aynı içerikten çok dilli türev

İleride (Phase 3+) tek bir knowledge base'den birden çok dilde içerik üretilebilir. Veri modeli buna hazır: `content_items` bir `source_content_id` alanı kazanır; araştırma ve claim'ler paylaşılır, senaryo/TTS/timeline/render dile özeldir. Görseller de paylaşılır — **metin içermeyen görsel seçmek** bu yüzden bir kalite kuralı hâline gelir.

> **ADR-013 — Dil birinci sınıf boyuttur.** Her içerik varlığı `language` taşır; tekillik ve metadata dil kapsamında çalışır; metin dizgisi HarfBuzz ile yapılır; normalizasyon dil başına implementasyondur. Yeni dil eklemek = konfigürasyon + font + normalizer, kod değişikliği değil.

---

## 21. Karar bekleyen sorular

Üçü 27 Ağustos'ta cevaplandı (§0). Kalanlar:

1. **Hangi iki dille başlıyoruz?** Türkçe + İngilizce mi, yoksa başka bir çift mi? (Font seti, TTS sağlayıcı yönlendirmesi ve normalizer önceliğini belirler.)
2. **Diller ayrı kanallar mı, aynı kanalda çoklu dil mi?** Ayrı kanal önerilir — YouTube algoritması tek dilli kanalları daha iyi dağıtır.
3. **Monetizasyon niyeti:** Kanallar gelir hedefliyor mu? Evetse R2 (inauthentic content) mimariyi "az ama iyi" yönüne çeker.
4. **Mevcut YouTube kanalları:** Kaç kanal var, hangileri aktif, herhangi birinde API kotası artırımı geçmişi var mı?
5. **Çalıştırma ortamı:** Windows makinede mi kalacak, yoksa bir VPS/sunucu var mı? (Phase 4 planını ve render kapasitesini belirler.)

---

## Sonraki adım

Görev kırılımı ayrı dosyada: **[IS-PLANI.md](IS-PLANI.md)** — 7 faz, 103 görev, 354 puan, her görevin kabul kriteriyle. İlerleme panosu `scripts/plan_progress.py` ile plandan üretilir.

Her adımda sıra aynı: önce plan, sonra uygulama, sonra test, sonra hata/log kontrolü, sonra doğrulama.
