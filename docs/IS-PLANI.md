# İş Planı — İçerik Fabrikası

Bu dosya **tek gerçek kaynaktır**. Bir görev bittiğinde `- [ ]` → `- [x]` yapılır, sonra:

```bash
python scripts/plan_progress.py
```

Bu komut yüzdeleri yeniden hesaplar ve `docs/plan-dashboard.html`'i günceller.

**Görev biçimi:** `- [ ] **KOD** \`Np\` — Başlık` · `N` = efor puanı (1–6) · altındaki *Bitti:* satırı kabul kriteridir. Bir görev, kabul kriteri sağlanmadan işaretlenmez.

İlgili mimari kararlar: [Icerik-Fabrikasi-Mimarisi.md](Icerik-Fabrikasi-Mimarisi.md)

---

## Faz 0 — Yürüyen iskelet

**Amaç:** Sistem uçtan uca çalışsın, içeriği sahte olsun. Bu faz bittiğinde tek komutla, sıfır para harcayarak, tekrarlanabilir bir mp4 üretilir.

### 0.A Altyapı

- [x] **P0-01** `2p` — Solution iskeleti: `BytemountsAiStudio.slnx`, 15 proje, `Directory.Build.props`, merkezi paket yönetimi, `.editorconfig`, `.gitattributes`, nullable + warnings-as-errors
  - *Bitti:* ✔ 27 Ağu 2026 — `dotnet build` 0 uyarı 0 hata, `dotnet test` yeşil, bağımlılık yönü `MediaPurityTests` ile IL seviyesinde korunuyor
- [x] **P0-01b** `1p` — CI: GitHub Actions (build + test + pano tazelik kontrolü)
  - *Bitti:* ✔ 27 Ağu 2026 — `selimmeric/BytemountsAiStudio` deposunda `build` ve `plan` işleri yeşil. Pano tazelik kontrolü ilk koşuda kırmızıya döndü ve gerçek bir sapmayı yakaladı
- [x] **P0-02** `2p` — Geliştirme ortamı: Docker Compose ile PostgreSQL 16 + pgvector + Seq
  - *Bitti:* ✔ 27 Ağu 2026 — PostgreSQL 16.15 (ICU locale provider), `vector` 0.8.6 + `pg_trgm` + `uuid-ossp` kurulu, kosinüs mesafesi doğrulandı. Seq 2026.1 ayakta (HTTP 200, ingestion açık). Portlar `127.0.0.1`'e bağlı
- [x] **P0-03** `5p` — EF Core `DbContext` + ilk migration: channels, topics, workflows, workflow_versions, runs, node_executions, run_events, jobs, assets, provider_calls
  - *Bitti:* ✔ 27 Ağu 2026 — 10 tablo, snake_case, `vector(768)` kolonu, 4 kısmi indeks. Migration uygulandı → geri alındı (1 tablo kaldı) → yeniden uygulandı (11 tablo). Seed idempotent. 7 test gerçek PostgreSQL'e karşı koşuyor; CI'a da Postgres servisi eklendi
- [x] **P0-04** `3p` — İçerik-adresli varlık deposu (CAS): `Put/Open/GetLocalPath`, sha256 adresleme, dizin sharding
  - *Bitti:* ✔ 27 Ağu 2026 — Akış tek geçişte hem hash'lenip hem yazılıyor (ağ akışları seekable değil). Yazma sırası önce dosya sonra kayıt: en kötü durum yetim dosya, "kayıt var dosya yok" değil. 6 test
- [ ] **P0-05** `2p` — Serilog + OpenTelemetry + correlation id (run/node bazında)
  - *Bitti:* Bir run'ın tüm logları tek id ile Seq'te filtreleniyor

### 0.B Kuyruk ve engine

- [x] **P0-06** `5p` — Job kuyruğu: `FOR UPDATE SKIP LOCKED` lease, heartbeat, lease süpürücü, kuyruk sınıfları
  - *Bitti:* ✔ 27 Ağu 2026 — `FOR UPDATE SKIP LOCKED` + kiralama + heartbeat + süpürücü. 15 test gerçek PostgreSQL'de. Çöken worker testi: negatif kiralama süresiyle taklit edilip geri alma doğrulandı, deneme sayacı korunuyor (sonsuz döngü olmaz). Duraklatılmış kanalın işi alınmıyor
- [x] **P0-07** `3p` — Hata sınıflandırma (geçici/kalıcı/zehirli/kaynak) + retry politikaları + DLQ
  - *Bitti:* ✔ 27 Ağu 2026 — Dört hata sınıfının dördü de farklı davranıyor: geçici→backoff, kalıcı→tekrar yok, zehirli→ilk denemede DLQ, **kaynak→erteleme (deneme sayacı bile artmıyor)**. Son denemede DLQ. Her sınıfın testi var
- [ ] **P0-08** `3p` — Workflow tanım modeli: JSONB graf, şema doğrulama, `workflow_versions` sürümleme
  - *Bitti:* Geçersiz graf (döngü, kopuk node, bilinmeyen tip) reddediliyor
- [ ] **P0-09** `5p` — DAG engine: run durum makinesi, node tetikleme, "çıktı yazımı + sonraki node kuyruğa" tek transaction
  - *Bitti:* Engine ortasında süreç öldürülünce run asılı kalmıyor, kaldığı yerden devam ediyor
- [ ] **P0-10** `3p` — Kısıtlı ifade değerlendirici (`when`, `max_loops`): karşılaştırma, `&&`, `||`, alan erişimi — rastgele kod yok
  - *Bitti:* Fuzz testi; kod çalıştırma denemeleri reddediliyor
- [ ] **P0-11** `3p` — Idempotency: `sha256(run_id|node_id|config|input)` + başarılı sonuç önbelleği
  - *Bitti:* Aynı node iki kez koşturulunca provider çağrısı bir kez yapılıyor
- [ ] **P0-12** `2p` — Worker host: kuyruk sınıfı başına eşzamanlılık konfigürasyonu, graceful shutdown
  - *Bitti:* `render` kuyruğu 1, `llm` kuyruğu 8 ile koşuyor; kapanışta lease bırakılıyor

### 0.C Provider katmanı

- [x] **P0-13** `4p` — Provider arayüzleri: `ILlm`, `ISearch`, `IWebFetch`, `ITts`, `IImage`, `IAsr`, `IMusic`, `IStorage`, `IPublisher`, `IAnalytics`
  - *Bitti:* ✔ 27 Ağu 2026 — 10 arayüz `.Contracts`'ta. `ProviderContractTests` dört kuralı koruyor: hepsi mevcut, hepsi `IProvider` türetir, Contracts yalnızca Core'a bağımlı, her asenkron metot `CancellationToken` alır
- [ ] **P0-14** `4p` — Dekoratör zinciri: Idempotency → Budget → RateLimit → CircuitBreaker → Retry → Metering → Telemetry
  - *Bitti:* Zincir sırası testle sabitlenmiş; her dekoratörün birim testi var
- [x] **P0-15** `4p` — Fake provider seti (tüm arayüzler): sabit metin, düz renk PNG, N saniyelik sessizlik WAV, sabit arama sonuçları
  - *Bitti:* ✔ 27 Ağu 2026 — 10 fake, 31 test. Üretilen PNG ve WAV FFmpeg tarafından okunuyor ve gerçek 1080×1920 H.264 mp4'e dönüşüyor (elle doğrulandı). "Ağa çıkmama" kuralı IL metadata'sında test ediliyor; `System.Random` kullanımı da yasak. Boru hattı seviyesindeki uçtan uca doğrulama P0-27'de
- [ ] **P0-16** `3p` — Maliyet defteri: `provider_calls` yazımı, birim sayımı (token/karakter/görsel/saniye)
  - *Bitti:* Fake çağrılar sıfır maliyetle, gerçek birim sayıları doğru kaydediliyor
- [ ] **P0-17** `3p` — Rate limit token bucket + provider circuit breaker
  - *Bitti:* Limit dolunca iş `run_after` ile erteleniyor, hata üretmiyor

### 0.D Render motoru

- [x] **P0-18** `4p` — Timeline şeması v1: tipler, doğrulayıcı, `schema_version` + göç iskeleti
  - *Bitti:* ✔ 27 Ağu 2026 — Tam tipli belge + 15 doğrulama kuralı, 16 test. Çakışan sahne, sahneler arası boşluk, çakışan ses, tanımsız stil, sahne dışına taşan overlay, aralık dışı pan — hepsi FFmpeg çalıştırılmadan yakalanıyor
- [x] **P0-19** `4p` — Planner: Timeline → RenderPlan (isimli girdiler, sahne eşlemesi, süre matematiği) — saf
  - *Bitti:* ✔ 27 Ağu 2026 — Saf: dosya/süreç/ağ yok, varlık yolları dışarıdan çözümlenmiş geliyor. Hareketsiz sahneye zoompan eklenmiyor (boşuna CPU ve netlik kaybı). 6 test
- [x] **P0-20** `5p` — IR: düğüm tipleri, `Expr`, keyframe → düz ifade derleyicisi
  - *Bitti:* ✔ 27 Ağu 2026 — Tipli düğümler + fabrika metotları + `Expr`. Keyframe ifadeleri DÜZ derleniyor: 30 kare ile 3000 kare aynı ifade derinliğini üretiyor (Studio'nun ayrıştırıcı sınırı dersi). 3 test
- [x] **P0-21** `3p` — Validator: pad tekil tüketim, döngü yok, medya tipi uyumu, `enable` aralık kontrolü
  - *Bitti:* ✔ 27 Ağu 2026 — Üretilmemiş akış, çift tüketim (split eksikliği), kullanılmayan ara akış, üretilmeyen çıkış, yanlış medya tipi ve döngü — 5 test. FFmpeg hiç çalıştırılmıyor
- [x] **P0-22** `4p` — Emitter: IR → `filter_complex` metni + argv; escape ve indeks ataması yalnız burada
  - *Bitti:* ✔ 27 Ağu 2026 — Escape ve indeks ataması tek noktada. **L2 regresyon testi geçiyor:** girdi sırası tersine çevrilince graf bozulmuyor, emitter indeksleri yeniden hesaplıyor. 4 test
- [x] **P0-23** `4p` — Executor: süreç yönetimi, `-progress` ayrıştırma, iptal, ffprobe doğrulama, `.partial` → atomik taşıma
  - *Bitti:* ✔ 27 Ağu 2026 — FFmpeg süreç yönetimi, `-progress` ayrıştırma (%50/%100 gözlendi), iptal, ffprobe doğrulama (video/ses akışı + %1 süre toleransı), `.partial` → atomik taşıma. Uzantı sonda kalmalı — `.mp4.partial` FFmpeg'in muxer seçimini bozuyor
- [ ] **P0-24** `5p` — Skia metin katmanı: HarfBuzz shaping, font fallback zinciri, altyazı kompozisyonu, kelime vurgusu
  - *Bitti:* Türkçe + ikinci dil metni doğru diziliyor; eksik glif fallback'ten geliyor, tofu çıkmıyor
- [x] **P0-25** `1p` — IR → Graphviz `dot` dökümü
  - *Bitti:* ✔ 27 Ağu 2026 — `GraphDot.Render` + CLI `--dot` seçeneği. Çalışan boru hattında 2.847 baytlık geçerli digraph üretildi
- [ ] **P0-26** `3p` — Golden testler: IR topolojisi (kanonik JSON) + emitter metni + 3 piksel testi
  - *Bitti:* CI'da FFmpeg olmadan koşuyor; piksel testleri ayrı işaretli

### 0.E Kilometre taşı

- [x] **P0-27a** `2p` — CLI: `pipeline` komutu → gerçek mp4 (doğrudan boru hattı)
  - *Bitti:* ✔ 27 Ağu 2026 — `bmai pipeline` konu→senaryo→TTS→ölçüm→timeline→plan→render zincirini koşuyor. Çıktı: **1080×1920 h264/aac, 13.0 sn, 46 KB, 2.4 sn render**. Sahne süreleri ffprobe ile ÖLÇÜLÜYOR (ADR-006)
- [ ] **P0-27** `2p` — CLI: `run workflow shorts-fake --topic "test"` → gerçek mp4
  - *Bitti:* Aynı çıktı, ama workflow engine üzerinden (P0-08/P0-09 gerekiyor). Doğrudan boru hattı P0-27a'da çalışıyor
- [ ] **P0-28** `2p` — 🏁 **Faz 0 kabul:** aynı komut iki kez koşturulunca birebir aynı çıktı hash'i
  - *Bitti:* Determinizm **elle doğrulandı** (iki koşu, birebir aynı sha256). Otomatik test ve kalan Faz 0 görevleri tamamlanınca işaretlenecek

---

## Faz 1 — Gerçek Shorts hattı

**Amaç:** İnsan onayıyla, gerçek içerikle, iki dilde Shorts yayınlanabilsin.

### 1.A Sağlayıcılar ve düşük API bütçesi

- [ ] **P1-01** `3p` — Kimlik deposu: şifreli credential (ASP.NET Data Protection), kanal başına set, redaction
  - *Bitti:* Anahtarlar DB'de düz metin değil; loglara sızmıyor (test)
- [ ] **P1-02** `4p` — LLM adaptörleri: **Ollama (yerel)** + Gemini/OpenAI + OpenRouter
  - *Bitti:* Aynı prompt üç sağlayıcıda da şemaya uygun çıktı veriyor
- [ ] **P1-03** `2p` — Model katmanlama (cheap/standard/strong) + yönlendirme politikası + fallback
  - *Bitti:* Kanal ayarından katman değişince kod değişmiyor
- [ ] **P1-04** `5p` — **Tools-sidecar (Python):** `/search` (SearXNG), `/fetch` (Playwright render), `/align` (WhisperX)
  - *Bitti:* Üç uç nokta da durumsuz; sağlık kontrolü var; .NET tarafında `Fake` ile değiştirilebiliyor
- [ ] **P1-05** `4p` — Search adaptörleri: SearXNG (self-host) + DuckDuckGo + **Wikipedia/Wikidata API**
  - *Bitti:* Tek arama sorgusu üç kaynaktan da sonuç dönüyor; API anahtarı gerekmiyor
- [ ] **P1-06** `3p` — WebFetch: robots.txt kontrolü, izinli alan listesi, ana içerik çıkarma, boyut/süre sınırı
  - *Bitti:* robots.txt yasaklı sayfa çekilmiyor; paywall tespit edilip atlanıyor
- [ ] **P1-07** `4p` — Prompt registry: dosya bazlı sürümleme, run'a kayıt, eval fixture koşucusu
  - *Bitti:* Prompt değişince fixture'lar koşuyor; kırılan prompt CI'da yakalanıyor

### 1.B Konu ve araştırma

- [ ] **P1-08** `5p` — Topic Agent + skorlama + havuz durum makinesi + **pgvector tekillik** (çok dilli embedding)
  - *Bitti:* "En Tehlikeli 10 Yer" ile "En Tehlikeli 10 Bölge" aynı sayılıyor; TR/EN çifti sayılmıyor
- [ ] **P1-09** `5p` — Research Planner + Research Agent (araç döngüsü, adım sayısı + bütçe sınırı)
  - *Bitti:* Bütçe dolunca döngü temiz duruyor, kısmi sonuç kaydediliyor
- [ ] **P1-10** `4p` — Claim Extractor + Entailment Checker (farklı model ailesi)
  - *Bitti:* Alıntısız claim üretilemiyor; desteklenmeyen claim işaretleniyor
- [ ] **P1-11** `2p` — Knowledge base: `sources` + `claims` yazımı, kaynak güven skoru
  - *Bitti:* Bir videonun tüm kaynakları tek sorguyla listeleniyor

### 1.C Senaryo ve ses

- [ ] **P1-12** `4p` — Script Agent + format şablonları (hook–list–payoff), `display_text`/`speech_text` ayrımı
  - *Bitti:* Knowledge base dışı iddia üretilirse QC yakalıyor
- [ ] **P1-13** `3p` — `ISpeechNormalizer`: dil başına sayı/tarih/kısaltma/para normalizasyonu
  - *Bitti:* 30 örneklik altın küme her iki dilde de doğru okunuyor
- [ ] **P1-14** `4p` — TTS adaptörü + segment üretimi + gerçek süre ölçümü
  - *Bitti:* Segment süreleri ffprobe ile doğrulanıyor
- [ ] **P1-15** `3p` — Kelime zamanları: önce TTS'ten, yoksa ASR sidecar
  - *Bitti:* İki yol da aynı şemayı dönüyor; sidecar kapalıyken TTS yolu çalışıyor

### 1.D Görsel, timeline, render

- [ ] **P1-16** `3p` — Scene Planner + Visual Director (arama terimi + AI prompt + stil)
  - *Bitti:* Sahne sınırları senaryodan, süreler sesten geliyor (regresyon testi)
- [ ] **P1-17** `3p` — Stok görsel adaptörü (Pexels) + indirme + **lisans kaydı**
  - *Bitti:* Her varlıkta lisans metni, yazar ve alınma tarihi dolu
- [ ] **P1-18** `2p` — AI görsel adaptörü + stok bulunamazsa fallback
  - *Bitti:* Yönlendirme politikasından açılıp kapanabiliyor
- [ ] **P1-19** `4p` — Timeline derleyici: ölçülen sürelerden sahne/altyazı/ducking üretimi
  - *Bitti:* Ses ile görsel kayması < 50 ms (otomatik ölçüm)
- [ ] **P1-20** `3p` — Render preset `shorts-1080x1920`: Ken Burns, fade, yakılmış altyazı, watermark
  - *Bitti:* 50 sn'lik video < 3 dk'da render ediliyor (referans makinede)

### 1.E Kalite, yayın, panel

- [ ] **P1-21** `4p` — Mekanik QC: 12 bloklayıcı kontrol + skor hesaplama + `retry_target`
  - *Bitti:* Bilerek bozulmuş 12 timeline'ın hepsi yakalanıyor
- [ ] **P1-22** `2p` — SEO Agent + platform sınırlarının kod tarafında uygulanması
  - *Bitti:* 100 karakteri aşan başlık kırpılıyor, upload reddi olmuyor
- [ ] **P1-23** `3p` — Thumbnail üretimi (Skia şablonu, dile duyarlı metin)
  - *Bitti:* Her iki dilde okunaklı; boyut/oran YouTube kurallarına uygun
- [ ] **P1-24** `3p` — YouTube OAuth (Production modu) + **kota rezervasyonu**
  - *Bitti:* Kota bitince iş `WaitingResource`, ertesi güne kayıyor; hata sayılmıyor
- [ ] **P1-25** `4p` — Resumable upload + idempotency + çökme kurtarma + thumbnail/playlist
  - *Bitti:* Upload ortasında süreç öldürülünce ikinci kez yüklenmiyor (test)
- [ ] **P1-26** `3p` — İkinci dil: ikinci kanal, font zinciri, dile göre tekillik, dile göre ses
  - *Bitti:* Aynı workflow iki dilde de uçtan uca koşuyor
- [ ] **P1-27** `3p` — Onay akışı: `human.approval` node, run parkı, onay kuyruğu
  - *Bitti:* Onay bekleyen run worker kaynağı tüketmiyor
- [ ] **P1-28** `4p` — API: run / topic / approval / cost uç noktaları + SSE canlı durum
  - *Bitti:* Dashboard yenilemeden ilerleme görüyor
- [ ] **P1-29** `6p` — Dashboard v1: run listesi, **run detay (node zaman çizelgesi + log + maliyet)**, onay kuyruğu, konu havuzu, DLQ, maliyet paneli
  - *Bitti:* "Bu video neden böyle oldu" sorusu paneldan cevaplanabiliyor

### 1.F Kilometre taşı

- [ ] **P1-30** `2p` — 🏁 **Faz 1 kabul:** her iki dilde birer gerçek Shorts yayında, video başına gerçek maliyet ölçülmüş
  - *Bitti:* İki video linki + maliyet raporu

---

## Faz 2 — Otonomi ve dayanıklılık

**Amaç:** Sistem gece boyunca kendi başına çalışıp sabaha video hazırlasın.

- [ ] **P2-01** `4p` — Topic Pool otomatik doldurma: eşik altına düşünce arka planda üretim
  - *Bitti:* Havuz hiç boşalmıyor; content run konu beklemiyor
- [ ] **P2-02** `5p` — Scheduler: kanal tempo, günlük hedef, saat pencereleri, **kota farkındalığı**
  - *Bitti:* Aynı anda toplu upload olmuyor; `publishAt` ile yayın saati ayrılıyor
- [ ] **P2-03** `4p` — Bütçe kapıları: run tahmini, kanal günlük, global aylık + `action_on_exceed`
  - *Bitti:* Limit aşımında yarım videolar bitiriliyor, yenisi başlamıyor
- [ ] **P2-04** `2p` — Global kill-switch + kanal duraklatma + provider devre kesici paneli
  - *Bitti:* Tek tıkla tüm kuyruklar duruyor, çalışan işler temiz kapanıyor
- [ ] **P2-05** `3p` — Kanal adaleti (fair scheduling)
  - *Bitti:* 3 kanallı yük testinde hiçbiri aç kalmıyor
- [ ] **P2-06** `4p` — Semantik QC: görsel alaka (VLM, örneklemeli), ton, yanıltıcı başlık, politika sınıflandırıcı
  - *Bitti:* Alakasız görsel yerleştirilen test videosu yakalanıyor
- [ ] **P2-07** `3p` — Hedefli retry: `retry_target` ile yalnız ilgili node'dan yeniden koşma + `max_loops`
  - *Bitti:* QC retry'ı tüm pipeline'ı yeniden koşturmuyor (maliyet ölçümü kanıt)
- [ ] **P2-08** `3p` — Selective approval: yalnız skoru eşiğin altındakiler insana
  - *Bitti:* Onay kuyruğu günde 20 videoda yönetilebilir kalıyor
- [ ] **P2-09** `3p` — Arka plan müziği + ducking + **lisans kanıtı kaydı**
  - *Bitti:* Lisanssız müzik varlığı yayına giremiyor (bloklayıcı kontrol)
- [ ] **P2-10** `3p` — DLQ triyaj ekranı: yeniden dene / node atla / run iptal
  - *Bitti:* Takılan run insan müdahalesiyle 3 tıkta kurtarılıyor
- [ ] **P2-11** `3p` — Bölüm bazlı render + segment önbelleği
  - *Bitti:* Tek sahne değişince yalnız o segment yeniden render ediliyor
- [ ] **P2-12** `2p` — Sürekli mod: `continuous` strateji, günlük hedef, tür karışımı
  - *Bitti:* Sistem 12 saat kesintisiz koşuyor, log temiz
- [ ] **P2-13** `2p` — 🏁 **Faz 2 kabul:** bir gecede 3–5 video insan müdahalesi olmadan hazır
  - *Bitti:* Sabah raporu + maliyet + QC skorları

---

## Faz 3 — Çoklu kanal, uzun video, workflow editörü

- [ ] **P3-01** `4p` — Çoklu kanal: kimlik, ayar, stil, ses, dil, takvim ayrımı
- [ ] **P3-02** `5p` — Uzun video formatı: derin araştırma, bölüm planı, 8–15 dk senaryo
- [ ] **P3-03** `4p` — Uzun video render: segment paralel render + concat + anahtar kare hizası
- [ ] **P3-04** `3p` — Intro/outro, bölüm geçişleri, chapter işaretleri
- [ ] **P3-05** `6p` — React Flow workflow editörü: node ekleme/bağlama, ayar formları, doğrulama
- [ ] **P3-06** `3p` — Workflow sürümleme UI + çalışan run'ların eski sürümde kalması
- [ ] **P3-07** `3p` — Prompt registry UI + eval sonuçları
- [ ] **P3-08** `3p` — Varlık gezgini + lisans raporu
- [ ] **P3-09** `3p` — Üçüncü dil (konfigürasyonla) — soyutlamanın sınavı
- [ ] **P3-10** `2p` — 🏁 **Faz 3 kabul:** iki kanal farklı workflow'larla, biri uzun video üretiyor

---

## Faz 4 — Ölçek

- [ ] **P4-01** `5p` — Render worker'ların ayrı makineye çıkarılması + iş dağıtımı
- [ ] **P4-02** `4p` — S3 uyumlu nesne deposu (MinIO/R2) + retention politikaları
- [ ] **P4-03** `3p` — Redis: dağıtık rate-limit + circuit breaker durumu
- [ ] **P4-04** `4p` — Çoklu GCP projesi / kota havuzu yönetimi
- [ ] **P4-05** `4p` — Docker + Linux dağıtımı, sağlık kontrolleri, otomatik yeniden başlatma
- [ ] **P4-06** `3p` — DB partition (`node_executions`, `run_events`) + okuma replikası
- [ ] **P4-07** `3p` — NVENC değerlendirmesi (kalite ölçümüyle)
- [ ] **P4-08** `3p` — Temporal spike + `IWorkflowEngine` arkasında karar
- [ ] **P4-09** `2p` — 🏁 **Faz 4 kabul:** 100 video/gün yük testi geçildi

---

## Faz 5 — Öğrenen sistem

- [ ] **P5-01** `4p` — YouTube Analytics günlük çekim + `publication_metrics` zaman serisi
- [ ] **P5-02** `5p` — Deney çerçevesi: tek değişkenli varyantlar, minimum örneklem, sonuç testi
- [ ] **P5-03** `4p` — Thumbnail A/B + başlık A/B
- [ ] **P5-04** `4p` — Konu skorlama ağırlıklarının gerçek performansla kalibrasyonu
- [ ] **P5-05** `3p` — Prompt varyant performans raporu
- [ ] **P5-06** `3p` — "Ne işe yarıyor" dashboard'u
- [ ] **P5-07** `2p` — 🏁 **Faz 5 kabul:** bir strateji değişikliği veriyle gerekçelendirildi

---

## Faz 6 — Çok platform, çok içerik türü

- [ ] **P6-01** `5p` — `IPublisher`: TikTok Content Posting API
- [ ] **P6-02** `4p` — Instagram Reels (Graph API)
- [ ] **P6-03** `3p` — Aynı içerikten rendition'lar (9:16 / 1:1 / 16:9, süre kırpma)
- [ ] **P6-04** `4p` — Blog/makale içerik türü (aynı knowledge base'den)
- [ ] **P6-05** `3p` — Podcast rendition'ı (yalnız ses)
- [ ] **P6-06** `4p` — Çok dilli türev: tek knowledge base → N dilde içerik (§20.7)

---

## Ek: Düşük API bütçesi modu

API sınırı bu projede bir kısıt değil, bir **tasarım girdisi**. Plan buna göre kuruldu:

| İhtiyaç | Ücretsiz / yerel yol | Plan görevi |
|---|---|---|
| Arama | **SearXNG** (kendi sunucunuzda, Docker) — meta arama, anahtar yok | P1-04, P1-05 |
| Arama (yedek) | DuckDuckGo HTML uç noktası; Brave API ücretsiz kotası | P1-05 |
| Ansiklopedik bilgi | **Wikipedia + Wikidata API** — ücretsiz, sınırsıza yakın, "En iyi 10'lar / tarih / gizem" içeriğinin büyük kısmını karşılar | P1-05 |
| Sayfa içeriği | **Playwright** ile tarayıcı render'lı çekme (JS'li siteler için) | P1-04, P1-06 |
| Ucuz LLM işleri (skorlama, claim çıkarma, normalizasyon) | **Ollama** ile yerel model (Qwen/Llama/Gemma) — ücretsiz, sınırsız | P1-02, P1-03 |
| Orta LLM işleri | Gemini / OpenRouter ücretsiz kotaları | P1-02 |
| Güçlü LLM (senaryo) | Ücretli — ama video başına yalnız 1–2 çağrı | P1-03 |
| Kelime zamanlaması | **WhisperX** yerel (ücretsiz) veya TTS'in kendi timing'i | P1-04, P1-15 |
| Görsel | Pexels/Pixabay/Unsplash ücretsiz kotaları | P1-17 |

**Yapılmayacak:** AI web arayüzlerini (ChatGPT/Gemini paneli vb.) tarayıcı otomasyonuyla sürmek. Servislerin kullanım şartlarına aykırı, bot tespitine takılır, her arayüz değişiminde kırılır ve hesabınızı riske atar. Yukarıdaki yerel + ücretsiz yol aynı sonucu meşru biçimde veriyor.

Tarayıcı otomasyonu **yalnız sayfa içeriği çekmek** için kullanılır (P1-06) ve robots.txt ile izinli alan listesine uyar.
