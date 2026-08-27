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
- [x] **P0-05** `2p` — Serilog + OpenTelemetry + correlation id (run/node bazında)
  - *Bitti:* ✔ 27 Ağu 2026 — `CorrelationScope` (AsyncLocal) + Serilog enricher + Seq. Engine her node çalıştırmasında kapsam açıyor; `RunId` ve `NodeId` her satıra kendiliğinden ekleniyor. Worker çalıştırıldı, Seq olay akışına log düştü. *Not: Seq arayüzünde RunId filtresini gözle doğrulamak size kaldı*

### 0.B Kuyruk ve engine

- [x] **P0-06** `5p` — Job kuyruğu: `FOR UPDATE SKIP LOCKED` lease, heartbeat, lease süpürücü, kuyruk sınıfları
  - *Bitti:* ✔ 27 Ağu 2026 — `FOR UPDATE SKIP LOCKED` + kiralama + heartbeat + süpürücü. 15 test gerçek PostgreSQL'de. Çöken worker testi: negatif kiralama süresiyle taklit edilip geri alma doğrulandı, deneme sayacı korunuyor (sonsuz döngü olmaz). Duraklatılmış kanalın işi alınmıyor
- [x] **P0-07** `3p` — Hata sınıflandırma (geçici/kalıcı/zehirli/kaynak) + retry politikaları + DLQ
  - *Bitti:* ✔ 27 Ağu 2026 — Dört hata sınıfının dördü de farklı davranıyor: geçici→backoff, kalıcı→tekrar yok, zehirli→ilk denemede DLQ, **kaynak→erteleme (deneme sayacı bile artmıyor)**. Son denemede DLQ. Her sınıfın testi var
- [x] **P0-08** `3p` — Workflow tanım modeli: JSONB graf, şema doğrulama, `workflow_versions` sürümleme
  - *Bitti:* ✔ 27 Ağu 2026 — JSONB graf + tam doğrulayıcı: bilinmeyen node tipi, tekrarlanan kimlik, kopuk kenar, erişilemeyen node, giriş node'u olmayan graf, kendine bağlanma, bozuk koşul — 13 test. Döngü yasak değil (QC→render meşru desen) ama `max_loops` sınırsız olamaz
- [x] **P0-09** `5p` — DAG engine: run durum makinesi, node tetikleme, "çıktı yazımı + sonraki node kuyruğa" tek transaction
  - *Bitti:* ✔ 27 Ağu 2026 — Run durum makinesi + node tetikleme + koşullu kenar + döngü sınırı. **Çıktı yazımı ve sonraki node'un kuyruğa atılması tek transaction'da.** İşleyici istisnası worker'ı düşürmüyor (zehirli sayılıyor); kaynak hatası run'ı düşürmüyor, `WaitingResource`'a alıyor; iptal edilmiş run'ın bekleyen işleri çalıştırılmıyor. 9 test
- [x] **P0-10** `3p` — Kısıtlı ifade değerlendirici (`when`, `max_loops`): karşılaştırma, `&&`, `||`, alan erişimi — rastgele kod yok
  - *Bitti:* ✔ 27 Ağu 2026 — Kasıtlı olarak ZAYIF dil: alan erişimi, sabit, karşılaştırma, mantık. Fonksiyon çağrısı, atama, aritmetik, ifade ayırıcı — hiçbiri yok. Sözcükleyici beyaz liste kullanıyor: eklenmeyen hiçbir karakter dile giremiyor. 7 kod-çalıştırma denemesi teste bağlandı; dilin BÜYÜMESİNİ engelliyorlar (R7)
- [x] **P0-11** `3p` — Idempotency: `sha256(run_id|node_id|config|input)` + başarılı sonuç önbelleği
  - *Bitti:* ✔ 27 Ağu 2026 — `sha256(run_id|node_id|config|input)`, deneme sayısı bilerek dahil değil — retry aynı anahtarı üretmeli ki sağlayıcı katmanı önceki sonucu tanısın. JSON kanonikleştiriliyor: alan sırası anahtarı değiştirmiyor. 4 test
- [x] **P0-12** `2p` — Worker host: kuyruk sınıfı başına eşzamanlılık konfigürasyonu, graceful shutdown
  - *Bitti:* ✔ 27 Ağu 2026 — Kuyruk sınıfı başına ayrı döngü, eşzamanlılık kaynaktan türetilmiş (render 1, llm 8). Ayrı bir kurtarma döngüsü: tüketiciler tıkansa bile süresi dolmuş kiralamalar toplanıyor. Bir döngünün hatası diğerlerini durdurmuyor. Worker ayağa kalktı, 8 kuyruk dinliyor

### 0.C Provider katmanı

- [x] **P0-13** `4p` — Provider arayüzleri: `ILlm`, `ISearch`, `IWebFetch`, `ITts`, `IImage`, `IAsr`, `IMusic`, `IStorage`, `IPublisher`, `IAnalytics`
  - *Bitti:* ✔ 27 Ağu 2026 — 10 arayüz `.Contracts`'ta. `ProviderContractTests` dört kuralı koruyor: hepsi mevcut, hepsi `IProvider` türetir, Contracts yalnızca Core'a bağımlı, her asenkron metot `CancellationToken` alır
- [x] **P0-14** `4p` — Dekoratör zinciri: Idempotency → Budget → RateLimit → CircuitBreaker → Retry → Metering → Telemetry
  - *Bitti:* ✔ 27 Ağu 2026 — Yedi middleware, sıra testle sabitlenmiş. **Metering retry'ın İÇİNDE:** dışında olsaydı üç kez denenip başarısız olan çağrının maliyeti bir kez sayılırdı. Önbellekten gelen sonucun maliyeti sıfır. Kalıcı hata devreyi açmıyor (sağlayıcı değil isteğimiz bozuk)
- [x] **P0-15** `4p` — Fake provider seti (tüm arayüzler): sabit metin, düz renk PNG, N saniyelik sessizlik WAV, sabit arama sonuçları
  - *Bitti:* ✔ 27 Ağu 2026 — 10 fake, 31 test. Üretilen PNG ve WAV FFmpeg tarafından okunuyor ve gerçek 1080×1920 H.264 mp4'e dönüşüyor (elle doğrulandı). "Ağa çıkmama" kuralı IL metadata'sında test ediliyor; `System.Random` kullanımı da yasak. Boru hattı seviyesindeki uçtan uca doğrulama P0-27'de
- [x] **P0-16** `3p` — Maliyet defteri: `provider_calls` yazımı, birim sayımı (token/karakter/görsel/saniye)
  - *Bitti:* ✔ 27 Ağu 2026 — `provider_calls` defteri: başarısız çağrılar dahil, birimler ham JSON olarak. Maliyet fiyattan türetiliyor — fiyat değişince geçmiş yeniden hesaplanabilir. Bütçe kapısı + kill-switch. 5 test
- [x] **P0-17** `3p` — Rate limit token bucket + provider circuit breaker
  - *Bitti:* ✔ 27 Ağu 2026 — Token bucket sağlayıcı HESABI başına (worker başına değil). Devre kesici üç durumlu, yarı açık deneme dahil. İkisi de sınır aşımında `Resource` döndürüyor: iş kuyruğu erteliyor, run düşmüyor. 7 test

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
- [x] **P0-24** `5p` — Skia metin katmanı: HarfBuzz shaping, font fallback zinciri, altyazı kompozisyonu, kelime vurgusu
  - *Bitti:* ✔ 27 Ağu 2026 — SkiaSharp + font fallback zinciri. **Kare dizisi tuzağından kaçınıldı:** her vurgu durumu tek PNG — 13 sn'lik videoda 27 görüntü, 390 kare yerine. Kontur önce çiziliyor (sonra çizilseydi harflerin içini yerdi). Türkçe karakterler ve vurgu rengi piksel seviyesinde test ediliyor. Render edilen videoda altyazı bandında 65 farklı renk ölçüldü, üst bantta 1
- [x] **P0-25** `1p` — IR → Graphviz `dot` dökümü
  - *Bitti:* ✔ 27 Ağu 2026 — `GraphDot.Render` + CLI `--dot` seçeneği. Çalışan boru hattında 2.847 baytlık geçerli digraph üretildi
- [x] **P0-26** `3p` — Golden testler: IR topolojisi (kanonik JSON) + emitter metni + 3 piksel testi
  - *Bitti:* ✔ 27 Ağu 2026 — İki ayrı altın kayıt: **topoloji** (grafın yapısı, okunabilir) ve **emitter** (metnin kendisi). Ayırmanın sebebi: argüman biçimi değişince yalnızca ikincisi kırılıyor ve diff okunabilir kalıyor. Studio'daki 12 KB'lık tek metin karşılaştırmasında "ne değişti" sorusu cevapsızdı. Timeline JSON gidiş-gelişi de sabitlendi

### 0.E Kilometre taşı

- [x] **P0-27a** `2p` — CLI: `pipeline` komutu → gerçek mp4 (doğrudan boru hattı)
  - *Bitti:* ✔ 27 Ağu 2026 — `bmai pipeline` konu→senaryo→TTS→ölçüm→timeline→plan→render zincirini koşuyor. Çıktı: **1080×1920 h264/aac, 13.0 sn, 46 KB, 2.4 sn render**. Sahne süreleri ffprobe ile ÖLÇÜLÜYOR (ADR-006)
- [x] **P0-27** `2p` — CLI: `run workflow shorts-fake --topic "test"` → gerçek mp4
  - *Bitti:* ✔ 27 Ağu 2026 — `bmai run` workflow engine üzerinden koşuyor: topic → research → script → tts → visuals → timeline → render, hepsi kuyruktan. Node çalıştırmaları kaydediliyor. Çıktı: 1080×1920, 12.78 sn, 327 KB, altyazılı
- [x] **P0-28** `2p` — 🏁 **Faz 0 kabul:** aynı komut iki kez koşturulunca birebir aynı çıktı hash'i
  - *Bitti:* ✔ 27 Ağu 2026 — **Determinizm engine üzerinden doğrulandı:** farklı run kimlikleriyle iki bağımsız koşu, birebir aynı sha256. Faz 0 tamamlandı

---

## Faz 1 — Gerçek Shorts hattı

**Amaç:** İnsan onayıyla, gerçek içerikle, iki dilde Shorts yayınlanabilsin.

### 1.A Sağlayıcılar ve düşük API bütçesi

- [x] **P1-00** `2p` — **Sağlayıcı kataloğu:** `config/providers.json` + açılışta doğrulama + `bmai providers`
  - *Bitti:* ✔ 27 Ağu 2026 — 18 sağlayıcı, 11'i anahtarsız. Katalog veri olduğu için hatası derlemede yakalanmıyordu; doğrulama açılışa çekildi: yönlendirmede tanımsız/kapalı sağlayıcı ya da anahtarı ortamda olmayan bir servis varsa sistem hiç açılmıyor. Anahtar geldiğinde değişen tek şey iki JSON satırı — kod değişmiyor. 7 test + depodaki gerçek kataloğu doğrulayan test
- [x] **P1-01** `3p` — Kimlik deposu: şifreli credential (ASP.NET Data Protection), kanal başına set, redaction
  - *Bitti:* ✔ 27 Ağu 2026 — Anahtarlar DB'de şifreli; yedeği alan kişi hesapları göremiyor (test bunu doğruluyor). Anahtar halkası veritabanının DIŞINDA — aynı yerde dursaydı şifrelemenin anlamı kalmazdı. Çözüm sırası kanal → genel → ortam değişkeni: ortamın en sonda olması bilinçli, yoksa sunucuda unutulmuş bir değişken bütün kanalları sessizce aynı hesaba bağlardı. Redaction kalıba değil BİLİNEN DEĞERE bakıyor (`sk-...` kalıbı yeni bir sağlayıcı formatını ıskalardı); süzgeç `run_events.message` ve `jobs.last_error` yazımında devrede — sızıntının gerçekte olduğu iki nokta. `bmai credential set` değeri stdin'den okuyor, komut satırından değil: komut satırı kabuk geçmişine, işlem listesine ve ekran görüntüsüne birden girer. 12 + 18 test
- [x] **P1-02a** `2p` — LLM adaptörü: **Ollama (yerel)**
  - *Bitti:* ✔ 27 Ağu 2026 — Gerçek yerel modele karşı doğrulandı: `qwen2.5-coder:7b`, şemaya uygun JSON, 45→120 token, sıfır maliyet. Zorunlu araç Ollama'nın `format` alanıyla yapılıyor — `tools` desteği modele göre değişiyor, `format` her modelde aynı çalışıyor. 5xx geçici / 4xx kalıcı ayrımı testli
- [~] **P1-02b** `2p` — LLM adaptörleri: Gemini / OpenAI / OpenRouter
  - *Kısmen:* 28 Ağu 2026 — Adaptörler yazıldı ve stub HTTP ile sınandı; **anahtar yalnızca canlı doğrulama için gerekiyor**. OpenAI ve OpenRouter TEK sınıf: aralarındaki fark yalnızca adres, anahtar ve model adı — üç ayrı sınıf, aynı ayrıştırma ve hata sınıflandırmasının üç kopyası demekti ve o kopyalar er geç ayrışırdı. **Gemini ayrı**, kablosu gerçekten farklı: sistem istemi mesaj değil ayrı alan (`contents` içine konsa kullanıcı mesajı gibi işlenir ve modelin uyma zorunluluğu zayıflar), zorlanmış araç `tool_config.function_calling_config` ile, anahtar `x-goog-api-key` başlığında — sorgu dizisi sunucu erişim kayıtlarına ve vekil loglarına düz metin yazılıyor. **Hata sınıfları kuyruğun kararını belirliyor** (ADR-011): 402 KAYNAK çünkü bakiye bitmesi erteleme, kalıcı sayılsaydı ödeme yapıldıktan sonra bile çalışmayacak bir işe dönüşürdü; Gemini'de 429 da KAYNAK çünkü ücretsiz katmanda bu genelde günlük kota ve dakikalar içinde yeniden denemek sadece kotayı tüketiyor. Araç zorunluyken metin dönen cevap başarı sayılmıyor. Gömme her ikisinde de **768 boyut** istiyor (ADR-003) — varsayılan 1536 vektör kolona hiç yazılamaz ve hata veritabanı katmanında, sebebi görünmeden çıkardı. 25 test
  - *Kalan:* Anahtarla canlı doğrulama — aynı istemin bulut sağlayıcılarda da şemaya uygun çıktı verdiğinin görülmesi
- [x] **P1-03** `2p` — Model katmanlama (cheap/standard/strong) + yönlendirme politikası + fallback
  - *Bitti:* ✔ 27 Ağu 2026 — `TieredLlmProvider` kendisi `ILlmProvider`: çağıran taraf tek sağlayıcıyla mı beş sağlayıcılık zincirle mi konuştuğunu bilmiyor. Bilseydi politika değişikliği her işleyiciye dokunurdu. Asıl karar HANGİ HATADA yedeğe düşüleceği: Transient ve Resource'ta düşülüyor (kota dolunca ücretsiz/yerele düşmek ADR-015'in işlevsel karşılığı), Permanent'ta DÜŞÜLMÜYOR — aynı geçersiz isteği ikinci bir sağlayıcıya göndermek yalnızca ikinci kez para harcamaktı. Tek istisna kimlik hataları: "bu anahtar geçersiz" isteğin değil yapılandırmanın kusuru. Hepsi düşerse hataların TAMAMI bildiriliyor (yalnızca sonuncuyu vermek en yaygın yanlış teşhis sebebi olurdu) ama sınıf İLKİNİN sınıfı kalıyor — kuyruğun kararı birincile göre verilmeli. Tanımsız katman bir alta düşüyor: anahtar yokken Strong boş olacak ve sistemin bu yüzden hiç çalışmaması saçma olurdu. Hangi sağlayıcıya gidildiği ve yedeğe düşülüp düşülmediği node çıktısına yazılıyor — yazılmasaydı birincil sağlayıcı sessizce ölür, kalite düşer, hiçbir şey kırılmadığı için kimse fark etmezdi. 25 test
- [x] **P1-04** `5p` — **Tools-sidecar (Python):** `/search` (SearXNG), `/fetch` (Playwright render), `/align` (WhisperX)
  - *Bitti:* ✔ 27 Ağu 2026 — `/health` yalnızca "ayaktayım" demiyor, HANGİ YETENEĞİN açık olduğunu ve kapalıysa NEDEN kapalı olduğunu söylüyor; kapalı bir yeteneğe çağrı 503 dönüyor ve .NET tarafı bunu Kaynak hatası olarak okuyup erteliyor (ADR-011) — sessizce daha kötü bir yola düşmek bu depoda gerçek videoların altyazısız çıkmasına yol açtı. Ağır bağımlılıklar (Playwright, faster-whisper) İSTEĞE BAĞLI: yalnızca `/search` gereken makinede 1 GB'lık model ağacı indirmek saçma. **Tarayıcı olmak muafiyet değil** — P1-06'nın kapıları burada da, aynı sırada. **Canlı koşuda üç hata bulundu:** (1) ilk metin çıkarıcı naif olduğu için gerçek Vikipedi sayfasından baştan aşağı menü çıkıyordu ("Rastgele madde" bir olgu olarak okunabilirdi) — .NET'teki kurallar birebir taşındı ve o sırada **.NET tarafında da `<head>` içeriğinin gövdeye sızdığı ortaya çıktı, iki tarafta birden düzeltildi**; (2) CUDA torch'a soruluyordu, faster-whisper torch kullanmıyor — 8 GB'lık kart dururken hizalama işlemcide koşuyordu; (3) düzeltince kart göründü ama cuBLAS kurulu değildi ve çağrı modelin ORTASINDA düşüyordu, artık kütüphaneler önceden kontrol ediliyor ve sebep `/health`'te yazıyor. Gerçek seste 9 kelime hizalandı. 54 pytest + 17 .NET testi
- [x] **P1-05a** `2p` — Search adaptörü: **Wikipedia resmî API** (anahtarsız)
  - *Bitti:* ✔ 27 Ağu 2026 — Arama + tam metin çekme tek sağlayıcıda. `extracts` düz metin dönüyor, HTML ayrıştırma gerekmiyor. Wikimedia'nın istediği tanımlayıcı User-Agent gönderiliyor — vermemek engellenme sebebi. 429/5xx geçici, diğerleri kalıcı
- [x] **P1-05b** `1p` — Search adaptörü: **SearXNG** (kendi sunucunuzda, anahtarsız)
  - *Bitti:* ✔ 27 Ağu 2026 — Docker `tools` profiliyle geliyor, JSON API'ye karşı doğrulandı. SearXNG varsayılanı JSON'u kapalı tutuyor; 403 alındığında hata mesajı doğrudan `formats` listesine `json` eklemeyi söylüyor. Alan adına göre kaynak tipi sınıflandırması var
- [x] **P1-05** `2p` — Search adaptörleri: SearXNG (self-host) + DuckDuckGo + **Wikipedia/Wikidata API**
  - *Bitti:* ✔ 27 Ağu 2026 — **Wikidata en değerli parça oldu.** Wikipedia METİN veriyor ve model tarihi o metinden ÇIKARIYOR; Wikidata tarihi bir ALAN olarak veriyor, yani çıkarım adımı ve o adımın hatası hiç yok. Sayılar ve tarihler kısa videoda hem en çok yanlış çıkan hem yanlışlığı en görünür olan şeyler. Seçilmiş 18 özellik (bir varlıkta 50'den fazlası var; hepsi bağlamı doldurup asıl bilgiyi gömerdi), öğe referansları TEK ek çağrıyla etikete çevriliyor. Olgular isteme AYRI bölüm olarak giriyor — karıştırılsaydı model olguyu da yorumlanacak bir metin sanardı, oysa tarih ve sayı tam da yorumlanmaması gerekenler. Olgu yoksa başlık da girmiyor: boş bir "OLGULAR:" başlığı modele "burada bir şey olmalıydı" diye okunup uydurmayı davet ediyor. **Canlı sorguda üç tarih hatası bulundu ve düzeltildi:** gün hassasiyetli tarihin günü düşüyordu (10 Kasım 1938 → "11/1938"), saat kısmı atılmayınca gün "10T00:00:00Z" çıkıyordu, ve İngilizce sıra eki "2th" idi — Türkçe "2. yüzyıl" doğru olduğu için gözden kaçıyordu. DuckDuckGo Instant Answer yedek olarak eklendi ve ne OLMADIĞI kayıtlı: web araması değil, sonuç sayfasını kazımak kullanım şartlarına aykırı. 34 test
- [x] **P1-06** `3p` — WebFetch: robots.txt kontrolü, izinli alan listesi, ana içerik çıkarma, boyut/süre sınırı
  - *Bitti:* ✔ 27 Ağu 2026 — Dört kapı, bu sırayla: şema → alan adı → robots.txt → boyut. Sıra önemli: robots kontrolü boyut sınırından önce, çünkü yasak bir sayfaya "sadece bakmak" da çekmek sayılıyor. Kontroller sağlayıcının İÇİNDE, çağıranın elinde değil — çağırana bırakılan kural er geç bir yerde atlanır. robots.txt 5xx dönerse ÇEKMİYORUZ: "okuyamadım, o hâlde serbesttir" tam ters yönde bir hata olurdu, üstelik sunucu zorlanırken. robots yasağı KALICI hata (yeniden denemek dosyayı değiştirmez). Tarayıcı taklidi REDDEDİLDİ: kimliğimizi veren User-Agent gönderiliyor; kendini gizleyen bir botun robots'a uyması zaten bir şey ifade etmez. Boyut sınırı AKIŞ sırasında (Content-Length'e güvenilmiyor). Gerçek Wikipedia sayfasında denenip iki hata bulundu ve düzeltildi: öznitelik içindeki `>` etiketi erken kapatıp JSON'u gövde metnine sızdırıyordu, ve paragraf içi satır sonu blok sınırı sanılıyordu. 52 test + `bmai fetch <url>`
- [x] **P1-07** `4p` — Prompt registry: dosya bazlı sürümleme, run'a kayıt, eval fixture koşucusu
  - *Bitti:* ✔ 27 Ağu 2026 — İstem KODDUR: `prompts/<anahtar>/v<N>.md`, kod deposunda, `git diff`'te görünür ve kod incelemesine girer. Veritabanında olsaydı üçü de olmazdı. Sürüm numarası yetmiyor — biri numarayı artırmadan metni düzeltebilir; damga içerik özetini taşıyor (`script.generate@2#759d10ff`) ve bu damga node çıktısına yazılıyor, yani "bu video hangi istemle üretildi" cevaplanabiliyor. Dosyalar derlemeye de GÖMÜLÜ: çalışma dizini değiştiğinde kırılmasın; bir test iki kopyanın özetlerini karşılaştırıp kaymayı engelliyor. Eksik yer tutucu HATA, boş değil — boş bırakmak sessizce bozuk bir istem üretir ve teşhisi saatler alır. Fixture'lar MODEL ÇAĞIRMIYOR: doğrulanan şey doldurulmuş istemin kendisi (düşen yer tutucu, silinen kural, taşan bağlam) — üçü de modelsiz yakalanıyor, CI'da milisaniyeler sürüyor ve modelin o günkü keyfine göre kırmızı yanmıyor. 26 test + `bmai prompt list|eval`

### 1.B Konu ve araştırma

- [x] **P1-08** `5p` — Topic Agent + skorlama + havuz durum makinesi + **pgvector tekillik** (çok dilli embedding)
  - *Bitti:* ✔ 27 Ağu 2026 — Altı boyutlu skor, çünkü mimari "tek sayı yetmez" diyor: 72 puan almış bir konunun neden 72 aldığını bilmeden eşik ayarlanamaz. **Kaynak bulunabilirliği en ağır boyut** (%30) — hattımızın kırılma noktası orası, kaynağı olmayan konu senaryoda değil iddia doğrulamada düşüyor ve o noktaya kadar harcanan her şey boşa gidiyor. **Risk CEZA olarak uygulanıyor, boyut olarak değil:** ağırlıklı ortalamaya katsaydık yüksek riskli bir konu diğer boyutlardan telafi edebilirdi, oysa politika ihlali riski telafi edilebilir değil; ayrıca 70 üstü tek başına veto. Aralık dışı değer SIKIŞTIRILMIYOR, REDDEDİLİYOR — 120 veren model muhtemelen boyutu da yanlış anlamış ve sessizce 100'e çekmek o hatayı gizler. Tekillik pgvector kosinüs mesafesiyle, kapsam KANAL + DİL (§20.5): TR'de yayınlanan konu EN'de tekrar değil. Yalnızca `Published` engel — reddedilmiş konu zaten yayınlanmadı. Gömme yoksa boş liste değil HATA dönüyor: "benzer yok" yanlış bir güvence olurdu. Kuyruktan alma `FOR UPDATE SKIP LOCKED` ile, iş kuyruğundaki desenin aynısı — iki worker aynı videoyu iki kez üretmesin. Havuz boşluğu hata değil KAYNAK durumu (ADR-011). Red gerekçesi hangi kuralın devreye girdiğini söylüyor: risk vetosu, tekrar ve düşük skor üç farklı düzeltme gerektiriyor. 43 test (26 saf + 17 veritabanı)
- [x] **P1-09** `5p` — Research Planner + Research Agent (araç döngüsü, adım sayısı + bütçe sınırı)
  - *Bitti:* Bütçe dolunca döngü temiz duruyor, kısmi sonuç kaydediliyor
- [x] **P1-10** `4p` — Claim Extractor + Entailment Checker (farklı model ailesi)
  - *Bitti:* ✔ 27 Ağu 2026 — İKİ AYRI ÇAĞRI ve ayrılığı bu işin bel kemiği: tek çağrıda yapmak cazip ve yanlış, çünkü aynı model hem iddiayı üretip hem kendi ürettiğini onaylıyor ve modeller kendi çıktılarını onaylamaya eğilimli. İkinci çağrıda model iddiayı METİN olarak görüyor. Üç değerli karar: `supported` / `unsupported` / `contradicted` — desteklenmemek "kaynağımız yetersiz", çelişmek "kaynağımız bunun yanlış olduğunu söylüyor" demek ve ikincisi bir kalite değil DOĞRULUK sorunu. **TANINMAYAN karar DESTEKSİZ sayılıyor:** belirsizlikte iyimser davranmak doğrulanmamış bir iddianın yayına çıkması demek, kötümser davranmak yalnızca gereksiz bir düzeltme turu. İddiasız senaryo geçerli — kanca ve kapanış cümleleri olgu taşımıyor ve sıfır iddiayı başarısız saymak onları yasaklamak olurdu. Cümleler modele NUMARALI veriliyor, yoksa indeksi tahmin ediyor ve hedefli düzeltme (P2-07) yanlış cümleye giderdi; uydurma indeks sınıra sıkıştırılıyor. Tek doğrulamanın düşmesi node'u düşürmüyor. "Farklı model ailesi" şu an İSTEM ve SICAKLIK düzeyinde (tek yerel model var); doğrulayıcı ayrı bir parametre, anahtar geldiğinde tek satır. Çıktıda `same_model` bayrağı var — aynı modelse sonuç iyimser olma eğiliminde ve bunu bilmek gerekiyor. 30 test
- [x] **P1-11** `2p` — Knowledge base: `sources` + `claims` yazımı, kaynak güven skoru
  - *Bitti:* ✔ 27 Ağu 2026 — Run bağlamı JSONB olarak zaten duruyordu; ayrı tabloların sebebi SORGULANABİLİRLİK — "bu videonun tüm kaynakları", "şu kaynağa dayanan bütün iddialar", "kaç iddia desteksiz" sorularının hiçbiri JSONB üzerinden makul maliyetle cevaplanamıyor. Kaynak tekilleştirmesi İÇERİK ÖZETİYLE, adresle değil: aynı sayfa iki farklı adresten gelebiliyor (yönlendirme, izleme parametreleri) ve iki kez saklamak kaynak sayımını bozardı. Tam metin tabloda DEĞİL — bir Wikipedia makalesi 50 KB ve bu tablo hızlı sorgulanacak. İddia kaynağa ADRESE göre bağlanıyor, kimliğe değil: iki node birbirinin veritabanı kimliklerini bilmek zorunda kalmasın. Kaynak silinirse iddia KALIYOR (kaynağı boşalıyor) — silmek "bu iddia neye dayanıyordu" sorusunun cevabını tamamen kaybettirirdi. Video kaynakları iddia→kaynak bağı üzerinden listeleniyor: araştırmada çekilip senaryoda kullanılmayan kaynak girmiyor, çünkü "bu video neye dayanıyor"un cevabı KULLANILAN kaynaklar. Güven skoru kaba ve bilerek öyle (P5-04'te kalibre edilecek); değerler değişecek, sıralama kalacak. 20 test

### 1.C Senaryo ve ses

- [x] **P1-12** `4p` — Script Agent + format şablonları (hook–list–payoff), `display_text`/`speech_text` ayrımı
  - *Bitti:* ✔ 27 Ağu 2026 — Üç biçim: `hook-payoff`, `hook-list-payoff`, `explainer`. Yapıyı İSTEM taşıyor, kod değil — yeni bir biçim eklemek bir kayıt yazmak, `switch` koluna dokunmak değil; ayrıca biçim metni istem dosyasında olduğu için `git diff`'te görünüyor ve fixture'la denetleniyor. Cümle SAYISI da biçimin parçası: liste biçimi üç cümleye sığmıyor, her madde kendi cümlesini istiyor. Modele aralık değil TEK hedef sayı veriliyor — aralık dağınık uzunluk üretiyor. Sayı sınırların dışında kalırsa hata GEÇİCİ sınıfında: istek geçerli, model bu sefer uymadı ve ikinci deneme genellikle uyuyor; kalıcı desek run düşerdi, hiç denetlemesek şablon yalnızca bir temenni olurdu. `display_text`/`speech_text` ayrımı P1-13b'de bağlandı. **Fixture koşucusu işini yaptı:** v3 eklenince sürüm sabitlemeyen iki eski fixture yeni yer tutucuda kırıldı ve düzeltildi — istem regresyonu canlı koşuda değil, saniyeler içinde yakalandı
- [x] **P1-13b** `1p` — Normalizasyonun BAĞLANMASI + `display_text`/`speech_text` ayrımı
  - *Bitti:* ✔ 27 Ağu 2026 — P1-13 yazılmıştı ama **bağlanmamıştı**: ham cümle doğrudan TTS'e gidiyordu, yani "1453" harf harf okunuyordu. Artık ekranda görünen ve seslendirilen metin ayrı ve ikisi de kayda giriyor. Altyazı EKRANDAKİ metni gösteriyor: sağlayıcının kelime zamanlaması seslendirilen metne ait ve normalizasyon metni değiştirdiyse o zamanlamalar başka sözcüklere işaret ediyor ("1453" tek kelime, karşılığı beş kelime) — bire bir eşlemeye kalkmak hem altyazıyı kaydırır hem ekranda sayının harfe açılmış hâlini yazardı
- [x] **P1-13** `3p` — `ISpeechNormalizer`: dil başına sayı/tarih/kısaltma/para normalizasyonu
  - *Bitti:* ✔ 27 Ağu 2026 — Türkçe + İngilizce, kural tabanlı (LLM değil: aynı sayı her videoda aynı okunmalı). Türkçe'de bin/yüz önündeki "bir" düşüyor; İngilizce'de 1453 "fourteen fifty-three" okunuyor. Yüzde/para sayıdan önce işleniyor, binlik ayırıcı tek sayı sayılıyor. Desteklenmeyen dil metni olduğu gibi döndürüyor — üçüncü dili engellememek için. 30 test
- [x] **P1-14a** `2p` — TTS adaptörü: **Windows yerel konuşma sentezi** (anahtarsız)
  - *Bitti:* ✔ 27 Ağu 2026 — WinRT üzerinden `Microsoft Tolga` (tr-TR) ile GERÇEK Türkçe seslendirme. PowerShell alt süreci kullanılıyor ki Windows bağımlılığı derleme zamanına değil çalışma zamanına hapsedilsin — Linux CI derlemeye devam ediyor. Metin base64 ile geçiyor: senaryodaki bir tırnak betiği bozamaz
- [~] **P1-14** `2p` — TTS adaptörü + segment üretimi + gerçek süre ölçümü
  - *Kısmen:* 28 Ağu 2026 — ElevenLabs adaptörü yazıldı ve stub HTTP ile sınandı; **anahtar yalnızca canlı doğrulama için**. Asıl değeri **kelime zamanlaması**: `with-timestamps` ucundan TEK ÇAĞRIDA hem ses hem hizalama geliyor — ayrı istemek ikinci kez para harcamak ve iki çağrının farklı ses üretme riski demekti (model deterministik değil). Bu sağlayıcı kullanıldığında ASR adımı **hiç çalışmıyor** (P1-15) ve saniyeler kazanılıyor. Karakter hizalaması kelimeye çevrilirken **boşluk kelime sınırı ama parçası değil**: dahil edilseydi her kelime bir sonrakine kadar uzar ve altyazı geç sönerdi. Dizi uzunlukları tutmazsa en kısası kadar okunuyor — bozuk bir yanıt yüzünden koşunun düşmesi, o yanıtın hiç gelmemesinden kötü. **Kota bitince de 401 dönüyor** ve tek ayırt edici gövdedeki `quota_exceeded`; kalıcı sayılsaydı kota yenilendikten sonra bile çalışmayacak bir işe dönüşürdü. Süre yine ffprobe ile ölçülüyor (ADR-006). 12 test
  - *Kalan:* Anahtarla canlı doğrulama
- [x] **P1-15a** `2p` — Kelime zamanları: **ölçüm yoksa dağıtım** (ara çözüm)
  - *Bitti:* ✔ 27 Ağu 2026 — Bu iş, gerçek bir kusuru kapatmak için öne alındı: Windows TTS kelime zamanlaması vermiyor, zamanlama olmayınca ipucu üretilemiyordu ve **GERÇEK videolar altyazısız çıkıyordu** — sahte hatta altyazı vardı, gerçek hatta yoktu, ve bu fark hiçbir yerde görünmüyordu. Süre kelimelere karakter sayısına göre dağıtılıyor (eşit paylaştırmak "bir" ile "arkeologların"a aynı süreyi verirdi) ve noktalama sonrasına es ağırlığı konuyor — saymazsak altyazı sesin ÖNÜNE geçiyor, ki arkada kalmaktan daha rahatsız edici. Dağıtım bir hizalama DEĞİL ve öyle iddia edilmiyor: çıktıda `timings_estimated` bayrağı var, bir kayma araştırılırken ilk bakılacak şey o
- [x] **P1-15** `3p` — Kelime zamanları: önce TTS'ten, yoksa ASR sidecar
  - *Bitti:* ✔ 27 Ağu 2026 — Öncelik sırası: sağlayıcının kendi zamanlaması (bedava, en doğru) → ASR hizalaması (doğru, saniyeler sürüyor) → karakter bazlı dağıtım (tahmin, P1-15a). Kaynak İPUCU BAŞINA kaydediliyor: bir koşuda bazı cümleler ölçülmüş bazıları tahmin edilmiş olabiliyor ve tek bir bayrak bunu gizlerdi. ASR **Kaynak** hatası dönerse (yan-servis kapalı) o koşuda bir daha denenmiyor — kapalı bir servise cümle sayısı kadar bağlantı denemek hepsi aynı cevabı verirken yalnızca gecikmeydi; **Geçici** hatada denenmeye devam ediliyor çünkü ağ bir cümlede kopup diğerinde düzelebilir. Sıfır kelimeli hizalama başarı sayılmıyor. Normalizasyon metni değiştirdiyse ASR de kullanılmıyor: ses "bin dört yüz elli üç" diyor, ekranda "1453" yazması gerekiyor — beş ölçülen kelimeye bir görünen kelime. **Canlı koşuda bir sınır bulundu ve kapatıldı:** ASR "Türkiye'nin"i iki parçaya böldüğü için kelime sayıları tutmuyordu ve beklenen-metin eşlemesi hiç devreye girmiyordu; Türkçede neredeyse her özel isim kesme işareti aldığından eşleme pratikte hiçbir zaman çalışmayacaktı. Artık bölünen parçalar birleştiriliyor. Aynı sırada eski konum-bazlı eşleme de düzeltildi: kelime sayılarının tesadüfen tutması, kelimelerin aynı olduğu anlamına gelmiyordu. `bmai tools health|align` ile uçtan uca doğrulandı. 10 .NET + 8 pytest testi

### 1.D Görsel, timeline, render

- [x] **P1-16** `3p` — Scene Planner + Visual Director (arama terimi + AI prompt + stil)
  - *Bitti:* ✔ 27 Ağu 2026 — Sahne SINIRLARI senaryodan, SÜRELER ölçülen sesten (ADR-006); regresyon testi ters ölçümle de sabitliyor, yoksa metin uzunluğuyla tesadüfen aynı yönde çıkıp geçerdi. Görsel yönetmen iki AYRI çıktı veriyor: stok araması için kısa terim, AI üretimi için bağlamlı istem — ihtiyaçları zıt, tek metin ikisini de kötüleştirirdi (ilk hâlde istem `"{konu} — sahne {n}"` idi ve kareler cümleyle ilgisizdi). Kural tabanlı, model çağırmıyor: sahne başına LLM çağrısının kazancı belirsiz ve aynı senaryo her koşuda aynı görseli vermeli. Olumsuz yönerge (`no text, no watermark`) her istemde — üretilen görseldeki uydurma yazı en sık kusur ve videoda okunuyor. Çok kısa sahneler İLERİ yönde birleşiyor (geriye birleştirmek ilk cümleyi açıkta bırakırdı) ve toplam süre korunuyor. Birleşme sahne sayısını ses parçası sayısından ayırdığı için `TimelineBuilder` birebir varsayımından kurtarıldı — o varsayım yalnızca kısa cümleli senaryolarda, seyrek ve teşhisi zor bir ses–görsel kayması olarak kırılacaktı. 41 test (26 planlayıcı/yönetmen + 13 timeline + 2 regresyon)
- [x] **P1-17a** `2p` — Stok görsel adaptörü: **Openverse** (Creative Commons, anahtarsız) + lisans filtresi
  - *Bitti:* ✔ 27 Ağu 2026 — Openverse'ün varsayılan sonuçları `by-nc-nd` geliyor ve **NoDerivatives kuralı Ken Burns hareketini bile ihlal eder**. Bu yüzden `license_type=commercial,modification` filtresi koda gömüldü ve konfigürasyondan kapatılamıyor; dönen her sonuç ayrıca `by/by-sa/cc0/pdm` listesine karşı ikinci kez doğrulanıyor — API davranışı değişirse sessizce ihlal etmeyelim. Atıf bilgisi varlıkla birlikte saklanıyor
- [~] **P1-17** `3p` — Stok görsel adaptörü (Pexels) + indirme + **lisans kaydı**
  - *Kısmen:* 28 Ağu 2026 — Adaptör yazıldı ve stub HTTP ile sınandı; **anahtar yalnızca canlı doğrulama için** — anahtarsız yol P1-17a'da açık. Lisans kaydı her sonuçla saklanıyor: Pexels atıf zorunlu kılmıyor ama kuralları değişebiliyor ve "o gün ne yazıyordu" sorusunun cevabı ancak alındığı anda saklanmışsa var — sonradan toplamak imkânsız, fotoğraf silinmiş olabiliyor. Dikey videoda `orientation=portrait` isteniyor: yatay bir kareyi 9:16'ya kırpmak karenin çoğunu atmak demek ve atılan kısım genellikle konunun kendisi. `large2x` tercih ediliyor — `original` bazen 8000 piksel ve indirmesi boşuna uzun, küçük kare ise büyütülünce bulanık. Ölçüsü olmayan sonuç atlanıyor. Kota 429 + `X-Ratelimit-Reset` ile KAYNAK hatası; geçmişte kalmış bir sıfırlama anı saat kayması sayılıp varsayılana düşülüyor. 13 test
  - *Kalan:* Anahtarla canlı doğrulama + indirme yolunun gerçek dosyayla sınanması
- [x] **P1-18a** `1p` — AI görsel adaptörü: **Pollinations** (anahtarsız)
  - *Bitti:* ✔ 27 Ağu 2026 — Ücretsiz kullanıma açık AI görsel üretimi. Tohum veriliyorsa aynı prompt aynı görseli veriyor — render önbelleğini anlamlı kılan şey bu. Sunucu bazen hata sayfasını 200 ile döndürüyor; boyut ve içerik tipi kontrolü bunu yakalıyor
- [x] **P1-18** `1p` — AI görsel adaptörü + stok bulunamazsa fallback
  - *Bitti:* ✔ 27 Ağu 2026 — `StockFirstImageProvider` kendisi `IImageProvider`: çağıran görselin nereden geldiğini bilmiyor. Sıra bir tercih değil, gerekçesi var — stok GERÇEK fotoğraf, üretilen görselde eller/yazılar/mimari detaylar hâlâ güvenilmez ve belgesel anlatıda tek bir hata içeriğin tamamını şüpheli gösteriyor; ayrıca stok ücretsiz, üretim hem para hem 20–40 saniye. **Stok BULAMAMAK hata değil:** soyut bir cümlenin ("kayıtlar bunu doğrulamıyor") stok karşılığı yok ve bu normal — hata saymak her soyut sahnede run'ı düşürürdü. Küçük görseller reddediliyor (1080×1920'ye büyütmek bulanık kare veriyor ve videoda ilk göze çarpan şey o). Ölü bağlantıda sıradaki aday deneniyor; stok servisleri sık sık ölü bağlantı döndürüyor. İkisi de düşerse HER İKİ hata bildiriliyor. Lisans ADAYDAN alınıyor, indirilen bayttan değil — indiren taraf lisansı bilmiyor. Varlık kaydına gerçek rota yazılıyor (`stock` / `generative:no_match`): "stock-first" yazsaydık stok hiç tutmuyorsa bunu kayıttan öğrenemezdik. Anahtarsız hat artık Openverse → Pollinations. 15 test
- [x] **P1-19** `4p` — Timeline derleyici: ölçülen sürelerden sahne/altyazı/ducking üretimi
  - *Bitti:* ✔ 27 Ağu 2026 — Sahne/altyazı üretimi P1-16'da bağlandı (ses–görsel kayması sıfır; `TimelineBuilderTests` sahne toplamının ses toplamına eşit olduğunu ve sahnelerin boşluksuz geldiğini doğruluyor). **Müzik yatağı modelde VAAT EDİLİP render'da yok sayılıyordu** — `AudioTrack.Music` doluyken filtre grafiğine hiç girmiyordu, yani kanal ayarında müzik açık görünüyor, videoda müzik yok ve hiçbir şey hata vermiyordu. Zincir artık kurulu: giriş → döngü → seviye → süreye kırpma → fade → ducking → karışım. Ducking `sidechaincompress` ile; ilk girdi KISILAN (müzik), ikinci girdi TETİK (konuşma) — sıra ters olsa teknik olarak geçerli ama tam tersi bir video çıkardı. **Gerçek FFmpeg'e karşı doğrulandı ve bir varsayımım yanlış çıktı:** `asplit`'i "bir akış yalnızca bir kez tüketilebilir" diye gerekçelendirmiştim; FFmpeg ham girdi pad'lerini kendisi çoğaltıyor, kural yalnızca FİLTRE ÇIKIŞLARI için geçerli. Bizim konuşma akışımız filtre çıktısı olduğu için `asplit` yine gerekli — ama artık doğru gerekçeyle, ve her iki durum da canlı FFmpeg testiyle sabitlendi. 20 test (16 graf + 4 gerçek FFmpeg)
- [x] **P1-20** `3p` — Render preset `shorts-1080x1920`: Ken Burns, fade, yakılmış altyazı, watermark
  - *Bitti:* ✔ 27 Ağu 2026 — Ken Burns, fade ve yakılmış altyazı Faz 0'da vardı; eksik olan **filigrandı** ve müzik yatağıyla aynı sessiz vaat durumundaydı: `PersistentLayers` modelde vardı, render onu hiç görmüyordu. Filigran ALTYAZIDAN SONRA biniyor — altında kalması onu kısmen görünmez yapardı ve filigranın tek işi görünmek. Konum İFADE olarak veriliyor (`W-w-40`), sabit piksel olarak değil: sabit yazsaydık filigranın kendi boyutunu bilmemiz gerekirdi ve o bilgi planlama anında yok, dosyayı açmadan öğrenilemiyor. Saydamlıktan önce `format=rgba` zorlanıyor — bu iddia da gerçek FFmpeg'e karşı ÖLÇÜLDÜ (alfasız görselde `colorchannelmixer` gerçekten etkisiz kalıyor, piksel parlaklığıyla doğrulandı) ve altı bağlantı ifadesinin hepsi FFmpeg tarafından değerlendirildi. 23 test (15 graf + 8 gerçek FFmpeg)

### 1.E Kalite, yayın, panel

- [x] **P1-21** `4p` — Mekanik QC: 12 bloklayıcı kontrol + skor hesaplama + `retry_target`
  - *Bitti:* ✔ 27 Ağu 2026 — §14.1'in on iki kontrolü, hepsi SAF FONKSİYON: model çağırmıyor, para harcamıyor, aynı girdiye aynı cevabı veriyor. Ölçüm YAPMIYOR, ölçülmüş değeri alıyor — ffprobe çağırsaydı testler dış süreç gerektirirdi; şimdi 12 bozuk senaryo milisaniyelerde koşuyor. **"Ölçemedim" ile "geçti" ayrı:** eşitlemek, thumbnail üretilmediği için hiç bakılmayan bir videonun tam puanla geçmesi demek olurdu. Bloklayıcı düşerse skor ANLAMSIZ ve sıfır — yüksek skorla birlikte "ama bloklayıcı düştü" demek ikisinden birinin gözden kaçmasına davetiye. `retry_target` boru hattında EN ERKEN düşen node: senaryo bozukken render'a dönmek iki tur harcar ve ikinci turda yine aynı senaryo hatasına düşer. Şartnamedeki "her sahnede görsel var" kontrolünü tip sistemi zaten garanti ediyordu, o yüzden gerçekte olabilecek üç şeye bakıyor: sıfır sahne, boş varlık referansı, sahneler arası boşluk (= siyah kare). 43 test
- [x] **P1-22** `2p` — SEO Agent + platform sınırlarının kod tarafında uygulanması
  - *Bitti:* ✔ 27 Ağu 2026 — İki katman ve ayrımı önemli: MODEL başlığı/açıklamayı/etiketleri yazıyor (yaratıcı iş), KOD sınırları uyguluyor (mekanik iş). İkinciyi modele bırakmak yaygın ama yanlış — isteme "100 karakteri geçme" yazmak çoğu zaman işe yarıyor, yaramadığı sefer upload REDDEDİLİYOR ve o noktada videonun kalan her adımı zaten yapılmış oluyor. İstem yine de sınırı söylüyor ama 90 karakter olarak, gerçek sınırın altında: modelin sınıra yakın yazması kırpmanın hiç devreye girmemesi demek. Kırpma kelime sınırında ve üç nokta SINIRA DAHİL — eklendikten sonra taşan metin kırpmamışız gibi reddedilirdi. Etiketler SONDAN atılıyor (model en alakalıyı başa yazıyor) ve ayraçlar sayılıyor (platform da öyle sayıyor). Kırpmanın KENDİSİ denetleniyor: sonuçta hâlâ ihlal varsa upload'da değil burada görülüyor. Kırpmanın devreye girip girmediği kayda geçiyor — sürekli kırpılan bir kanal, istemin sınırı yeterince baskılamadığını söylüyor. 48 test
- [x] **P1-23** `3p` — Thumbnail üretimi (Skia şablonu, dile duyarlı metin)
  - *Bitti:* ✔ 27 Ağu 2026 — 1280×720 (16:9). Kısa video dikey olsa bile kapak YATAY: platform onu arama sonuçlarında yatay gösteriyor ve dikey bir kapağı kendisi kırpıyor — kırptığı yer genellikle metnin ortası. Uzun başlık KIRPILMIYOR, yazı tipi küçültülüyor: kırpılmış başlık yarım cümle gösterir ve tıklanmaz. Sahne görseli arka plan olarak KAPLAYARAK yerleşiyor (sığdırmak kenarlarda bant bırakır) ve üstüne karartma geliyor — parlak bir görselde beyaz metin okunmuyor, kontur tek başına yetmiyor. **Üç hata canlı çıktıya bakılınca bulundu:** (1) `SKTypeface.FromFamilyName` kurulu olmayan aile için null DÖNMÜYOR, sistem varsayılanını döndürüp istenen kalınlığı sessizce düşürüyor — kapak ince yüzle çiziliyordu; (2) aynı hatanın izi sürülünce görüldü ki **altyazılar da uzun süredir ince çiziliyormuş** — `TextStyle.Bold` timeline'da `true` olduğu hâlde çizim kalınlığı hiç istemiyordu, yani ayar vardı etkisi yoktu; (3) `SKBitmap.Decode` bozuk baytlarda null dönmüyor İSTİSNA atıyor, yani bozuk bir arka plan görseli kapak node'unu çökertirdi. Ortak `FontResolver` ikisini de kapatıyor. 22 test
- [~] **P1-24** `3p` — YouTube OAuth (Production modu) + **kota rezervasyonu**
  - *Kısmen:* 28 Ağu 2026 — **Kota muhasebesi yazıldı ve saf**: "bu iş bugün çalışabilir mi" sorusu gerçek bir kota tüketilerek öğrenilecek bir şey olmamalı. Günde altı video (6×1600=9600); yedincisi hata değil, ertesi güne kalmış iş. **Rezervasyon harcamadan ayrı**: rezerve edilen kota yükleme başlamadan düşülüyor, gerçek harcama sonra bildiriliyor — yalnızca gerçekleşeni saymak, aynı anda başlayan iki yüklemenin ikisinin de "yer var" görmesi demekti. Tam sığması şart: yarım kotayla başlanan yükleme ortasında reddedilir ve harcanan kısım geri gelmez. **Sıfırlanma Pasifik gece yarısı**, UTC değil — UTC varsayılsaydı 7–8 saat sapma olurdu ve iş ya erken uyanıp boşuna denerdi ya da bir günü kaybederdi; bekleme süresi tam o ana kadar, sabit bir saat değil. Kapak ve oynatma listesi ayrı ücretli: hep kapaklı varsaymak günde bir videoluk kotayı boşuna rezerve etmekti. 11 test
  - *Kalan:* OAuth akışı ve gerçek `youtube.upload` çağrısı — Production modu onayı ve anahtar bekliyor
- [~] **P1-25** `4p` — Resumable upload + idempotency + çökme kurtarma + thumbnail/playlist
  - *Kısmen:* 28 Ağu 2026 — **Sürdürme mantığı yazıldı ve saf**: parça sınırlarının hesabı ve "nereden devam edilecek" kararı, 60 MB'lık bir dosya gerçekten yüklenerek öğrenilecek bir şey olmamalı. Parça boyutu **256 KiB'in tam katı** olmak zorunda — katı olmayan parça son parça değilse 400 ile reddediliyor ve hata mesajı bunu söylemiyor; aşağı yuvarlanıyor, yukarı yuvarlamak istenen sınırın üstüne çıkıp daha büyük tampon ayırmaktı. **Nereden devam edileceğine PLATFORM karar veriyor**, biz değil: gönderdiğimiz bir parça karşı tarafa hiç ulaşmamış olabilir ve oradan devam etmek dosyada delik bırakır — yükleme tamamlanmış görünür, video bozuk çıkardı. `Range` başlığı son baytın İNDİSİNİ veriyor, onaylanan sayı bir fazlası; karıştırmak her sürdürmede bir baytlık kayma üretirdi. Başlık yoksa sıfır: "bilinmiyor" sayıp mevcut değeri korumak tam da o deliği açardı. `Content-Range` bitişi dahil — bir eksik yazmak son parçada "tamamlanmadı" demesine yol açıyor. Oturum ömrü (7 gün) dolmuşsa sürdürme adresi 404 dönüyor; bu önceden biliniyor ve boşuna bir tur harcanmıyor. 13 test
  - *Kalan:* Gerçek yükleme oturumu, `FindExistingAsync` ile çift-yükleme kurtarması ve kapak/liste çağrıları — anahtar bekliyor
- [~] **P1-26** `3p` — İkinci dil: ikinci kanal, font zinciri, dile göre tekillik, dile göre ses
  - *Kısmen:* 27 Ağu 2026 — **Dile göre ses tamam.** Gerçek bir engel bulundu: bu makinede yalnızca `Microsoft Tolga` (tr-TR) kurulu ve İngilizce içerik **sessizce Türkçe sesle** okunuyordu. Windows sağlayıcısı artık dil eşleşmezse üretim yapmıyor (Kaynak hatası, kurulum adımlarıyla). İkinci dilin anahtarsız yolu Piper: yan-servise `/tts` eklendi, tamamen çevrimdışı, ses başına ~63 MB ONNX ve işlemcide gerçek zamanın ~15 katı hızında — filodaki 2 GB'lık makineler de seslendirebiliyor. `FallbackTtsProvider` sırayı kuruyor: önce Windows (bedava, hızlı, kurulu), Kaynak/Geçici hatada Piper. Kalıcı hatada geçilmiyor. **Gerçek makinede doğrulandı:** tr-TR → Microsoft Tolga (3,8 sn, −21 dB), en-US → en_US-amy-medium (4,0 sn, −16 dB). Kullanılan ses artık `TtsResponse` ile taşınıyor ve node çıktısına yazılıyor — istenen ile kullanılan farklı olabiliyor ve fark kayıtlı olmasa yanlış seslendirilmiş bir video teşhis edilemezdi. 13 .NET + 7 pytest testi
  - *Kalan:* İkinci kanal + font zinciri + dile göre tekillik uçtan uca koşusu — veritabanı gerektiriyor (Docker bu makinede çökük)
- [x] **P1-27** `3p` — Onay akışı: `human.approval` node, run parkı, onay kuyruğu
  - *Bitti:* ✔ 28 Ağu 2026 — Kabul kriteri doğrudan sınanıyor: park edilmiş run'ın kuyrukta **sıfır** işi var. Beklemeyi bir işin içinde yapmak — uyuyup tekrar bakmak — bir worker'ı saatlerce tutar ve o sınıftaki bütün işleri bekletirdi. Motor node TİPİNE değil ÇIKTIYA bakıyor (`awaiting_approval`): aynı kapı bir koşuda insana sorup diğerinde sormuyor, karar QC skoruna ve kanal kipine bağlı — tipe bakılsaydı otomatik geçen kapılar da run'ı park ederdi. Kararın node'da verilmesinin sebebi de bu: motora gömülseydi her kanal için motoru değiştirmek gerekirdi. **Skor yoksa insana soruluyor** — "ölçülmedi" ile "iyi" aynı şey değil ve kalitesi bilinmeyen bir videoyu kimse görmeden yayına vermek en kötü seçenekti. Tanınmayan kip adı da onaya düşüyor: yapılandırma hatası yüzünden bir kanalın sessizce otonom hâle gelmesi tersinden çok daha pahalı. Ret, run'ı **İptal** ediyor Başarısız değil — insan kararları hata panelini doldurup gerçek arızaları görünmez kılmamalı. İkinci karar reddediliyor: iki kişi aynı anda onaylarsa video iki kez render edilip iki kez yüklenirdi. Kuyruğa atmayı motor yapıyor; onay servisinin kendi eşlemesini yazması ilk tasarımdı ve ayrışma riski taşıyordu. Kısmi indeksler: bekleyen onaylar zamanla birikmiyor, aynı node'da ikinci bekleyen kayıt açılamıyor. 32 saf + 8 veritabanı testi
- [x] **P1-28** `4p` — API: run / topic / approval / cost uç noktaları + SSE canlı durum
  - *Bitti:* ✔ 28 Ağu 2026 — SSE seçildi, WebSocket değil: akış TEK YÖNLÜ, panonun sunucuya söyleyeceği bir şey yok ve yeniden bağlanmayı tarayıcı kendisi yapıyor. Akış **yalnızca değişince** gönderiyor — her saniye aynı belgeyi yollamak, istemcinin "bir şey oldu" ile "bağlantı ayakta" arasındaki farkı görmesini engellerdi; bağlantıyı ayakta tutan şey ayrı bir yorum satırı (`: ping`), sahte olay değil. **Bekleyen durumlar akışı kapatmıyor**: `WaitingApproval` kapatılsaydı onay verildiği anda pano bunu göremezdi, oysa panonun asıl işi o — bitmiş run'ın akışı ise kapanıyor, yoksa panoyu açık bırakan biri bağlantıları tüketirdi. Maliyet `provider_calls`'tan kırılıyor, `runs.actual_cost`'tan değil: tek bir sayı "neden bu kadar" sorusuna cevap vermiyor, ve **başarısız çağrılar da sayılıyor** çünkü "maliyet yüksek ama video yok" durumunun tek açıklaması onlar. Entity'ler doğrudan dönülmüyor; şema ile sözleşme ayrı. **Sessiz bir delik kapatıldı:** DI'daki `NodeRegistry` boştu ve onay verildiğinde motor sonraki node'un tipini tanımayıp hiçbir şey kuyruğa atmayacaktı — run "Running" görünür, kuyruk boş kalır, kimse fark etmezdi. Kayıt gerçekleştirildi ve motor artık kayıtsız node'u sessizce atlamak yerine hata olayı yazıyor. 16 saf + 7 veritabanı testi
- [x] **P1-29** `6p` — Dashboard v1: run listesi, **run detay (node zaman çizelgesi + log + maliyet)**, onay kuyruğu, konu havuzu, DLQ, maliyet paneli
  - *Bitti:* ✔ 28 Ağu 2026 — Tek HTML dosyası, derleme adımı yok: bir npm ağacı, panelin çalışması için ikinci bir araç zinciri ve ikinci bir CI adımı demekti. API ile aynı sunucudan geliyor — ayrı sunucu CORS ayarı ve ikinci bir dağıtım adımı getirirdi. Kabul kriteri **canlı doğrulandı**: run detayında üçü bir arada — `ses` node'u 2. denemede `windows_speech.no_voice` ile düştü, 3. denemede geçti, log "Piper'a düşüldü" diyor, maliyet kırılımı elevenlabs'ta 1 düşen çağrı gösteriyor. Panel **her zaman hata gösteriyor**: sessizce boş kalan bir tablo, "hiç veri yok" ile "API'ye ulaşılamadı" arasındaki farkı gizlerdi — tarayıcıda `fetch` düşürülerek sınandı. XSS kaçışı sınandı: başlıktaki `<script>` metin olarak çıkıyor. SSE göstergesi canlı çalışıyor, sayaç sunucudan güncelleniyor, bitmiş koşuda akış kapanıyor. DLQ ayrı bölüm ve **en yeni önce** — onay kuyruğunun tersi, çünkü oraya bakmanın sebebi "az önce ne düştü". Onay butonları tıklanınca kilitleniyor: ikinci tıklama 409 alırdı ama kullanıcı o hatayı hak etmiyor. 19 saf + 10 veritabanı testi (`/dlq` dahil)

### 1.F Kilometre taşı

- [ ] **P1-30** `2p` — 🏁 **Faz 1 kabul:** her iki dilde birer gerçek Shorts yayında, video başına gerçek maliyet ölçülmüş
  - *Bitti:* İki video linki + maliyet raporu

---

## Faz 2 — Otonomi ve dayanıklılık

**Amaç:** Sistem gece boyunca kendi başına çalışıp sabaha video hazırlasın.

- [ ] **P2-01** `4p` — Topic Pool otomatik doldurma: eşik altına düşünce arka planda üretim
  - *Bitti:* Havuz hiç boşalmıyor; content run konu beklemiyor
- [~] **P2-02** `5p` — Scheduler: kanal tempo, günlük hedef, saat pencereleri, **kota farkındalığı**
  - *Kısmen:* 28 Ağu 2026 — **Karar saf ve sınandı**: "şimdi yeni video başlatmalı mıyız" sorusu, bir gün boyu sistem koşturularak öğrenilecek bir şey olmamalı. **Kota ile yayın temposu ayrı** (§15.3): kota gündüz harcanıyor, video gizli yükleniyor ve `publishAt` ile istenen saatte açılıyor — ikisini bağlamak, kotanın bittiği saatte yayın yapmak zorunda kalmaktı. **Kota önce bakılıyor**: kota yoksa üretime hiç başlanmıyor, çünkü videoyu üretip yükleyememek harcanan her şeyi ertesi güne taşımak ve o gün yeniden ödemek demek. Toplu yükleme `MinimumGap` ile engelleniyor — beş videoyu arka arkaya yüklemek kanalı spam gibi gösteriyor ve platform hepsinin erişimini birden kısıyor; bekleme kalan süre kadar, sabit değil. Bugünün penceresi bittiyse yarının ilki alınıyor: yalnızca bugüne bakmak, akşam karar veren bir kanalın durması demekti. Pencere ofseti **hedef tarihten** okunuyor — yaz saati geçişinde bugünün ofsetini kullanmak yayını bir saat kaydırırdı. Bozuk saat dilimi UTC'ye düşüyor: yapılandırma hatasının bedeli yanlış saat olmalı, hiç yayın olmaması değil. 16 test
  - *Kalan:* Zamanlayıcı döngüsünün kendisi — kanalları tarayıp run başlatması (veritabanı gerekiyor)
- [~] **P2-03** `4p` — Bütçe kapıları: run tahmini, kanal günlük, global aylık + `action_on_exceed`
  - *Kısmen:* 28 Ağu 2026 — **Karar saf ve sınandı**: "bu çağrı yapılabilir mi" sorusu gerçek para harcanarak öğrenilecek bir şey olmamalı. En önemli ayrım yeni run ile **yarım kalmış** run arasında: yarım bir videoyu bütçe yüzünden durdurmak, o ana kadar harcanan her kuruşu çöpe atmak — senaryo yazılmış, ses üretilmiş, görseller indirilmiş ve hiçbiri kullanılmayacak; üstelik ertesi gün devam edilse o adımlar **ikinci kez** para harcayacak. Varsayılan `FinishInFlight`: kapı yeni işlere kapanıyor, çalışanlar bitiyor. Pencereler sırayla bakılıyor ve gerekçe **hangi limitin** çarptığını yazıyor — "bütçe aşıldı" tek başına, kanal limitini mi global aylığı mı büyütmek gerektiğini söylemiyor. Aylık pencerede bekleme ay sonuna kadar: bir saat sonra denemek anlamsız, ayın kalanında hiçbir şey değişmeyecek. Tanınmayan `action_on_exceed` varsayılana düşüyor, `StopEverything`'e değil — bir yazım hatası yüzünden yarım videoların çöpe gitmesi, bütçenin biraz aşılmasından pahalı. 16 test
  - *Kalan:* `BudgetGate` bu politikayı kullanacak şekilde bağlanmalı (veritabanı gerekiyor)
- [ ] **P2-04** `2p` — Global kill-switch + kanal duraklatma + provider devre kesici paneli
  - *Bitti:* Tek tıkla tüm kuyruklar duruyor, çalışan işler temiz kapanıyor
- [~] **P2-05** `3p` — Kanal adaleti (fair scheduling)
  - *Kısmen:* 28 Ağu 2026 — Kabul kriteri (3 kanallı yükte hiçbiri aç kalmıyor) **gerçek yük koşturmadan** sınanıyor: karar saf. **Test bir tasarım açığı buldu** — ilk kural "en az koşan, sonra en uzun bekleyen" idi ve işler hızlı bittiğinde koşan sayısı hep sıfır kalıyor, ölçüt hiçbir şey ayırt etmiyor ve seçim son çare olan kimlik sırasına düşüyordu: en küçük kimlikli kanal her turu kazanıp diğerlerini aç bırakıyordu. Kural üç ölçütlü oldu — anlık yük → **geçmiş pay** → bekleme süresi. Round-robin seçilmedi (sıradaki kanalın işi yoksa tur boşa gidiyor), ağırlıklı adalet de seçilmedi (yanlış ayarlanan ağırlık yine açlık üretiyor). Kanal başına tavan worker sayısından türetiliyor ve en az 1 — sıfır olsaydı hiçbir kanal iş alamaz, sistem sessizce dururdu. Açlık ölçülebilir: bekleyen işi olan ama hiç koşanı olmayan ve eşiği aşan kanal. 12 test
  - *Kalan:* `JobQueue.LeaseAsync`'e bağlanması (veritabanı gerekiyor)
- [ ] **P2-06** `4p` — Semantik QC: görsel alaka (VLM, örneklemeli), ton, yanıltıcı başlık, politika sınıflandırıcı
  - *Bitti:* Alakasız görsel yerleştirilen test videosu yakalanıyor
- [~] **P2-07** `3p` — Hedefli retry: `retry_target` ile yalnız ilgili node'dan yeniden koşma + `max_loops`
  - *Kısmen:* 28 Ağu 2026 — Plan saf ve sınandı; maliyet farkı **sayı olarak** ölçülüyor (`Saved`): render'a dönmek senaryoyu yeniden üretmiyor. Hedefin kendisi ve sonrası koşuyor, öncesi hiç dokunulmuyor — araştırma yeniden yapılmıyor, kaynaklar yeniden çekilmiyor. **Yalnızca `Retry` kararı yeniden koşuyor**: `NeedsApproval` bir düşüş değil bir yönlendirme (P2-08) ve onu da koşturmak, insanın zaten kabul edeceği videoyu bir kez daha üretmekti. Hedefsiz düşüş yeniden koşmuyor — ölçülemeyen bir süre, aynı adım tekrarlanınca yine ölçülemez. Döngü sınırı 3: iki tur düzeltmeyen kusur genelde üçüncüde de düzelmiyor ve sınırsız döngü aynı hatayı sonsuza kadar para harcayarak tekrarlıyor. **Sınır dolunca run başarısız değil**, hedef korunarak insana gidiyor — başarısız saymak, üç turdur düzelmeyen ama belki kabul edilebilir bir videoyu çöpe atmaktı. 13 test
  - *Kalan:* Motorun bu planı uygulaması — hedeften sonraki node'ları yeniden kuyruğa atması (veritabanı gerekiyor)
- [x] **P2-08** `3p` — Selective approval: yalnız skoru eşiğin altındakiler insana
  - *Bitti:* ✔ 28 Ağu 2026 — P1-27 ile birlikte geldi: `ChannelMode.Selective` + `min_score` eşiği. Ölçeklenmenin tek yolu bu — her videoyu insana göstermek, günde 50 video değil günde 5 video demek. **Skor yoksa insana soruluyor**: "ölçülmedi" ile "iyi" aynı şey değil ve kalitesi bilinmeyen bir videoyu kimse görmeden yayına vermek en kötü seçenekti. Eşiğe eşit olmak geçiyor (`>=`), aksi hâlde eşik değerinin kendisi hiçbir zaman geçemezdi. Gerekçe skoru ve eşiği birlikte yazıyor — "eşiğin altında" tek başına, eşiğin yanlış ayarlandığını mı videonun gerçekten kötü olduğunu mu söylemiyor. 17 test
- [~] **P2-09** `3p` — Arka plan müziği + ducking + **lisans kanıtı kaydı**
  - *Kısmen:* 28 Ağu 2026 — Anahtarsız müzik kaynağı: **Openverse ses API'si** (ADR-015). Lisans kuralı görsellerden **daha sert** — Content ID müziği otomatik tanıyor ve bir talep kanalın o videodan gelen gelirinin tamamını götürüyor, bazen kanalın tamamına ihtar geliyor; görselde atıf eksikliği düzeltilebilir bir kusur, müzikte düzeltilemez bir hasar. **`by-sa` listede değil** (görsellerden farklı): ShareAlike türev eserin aynı lisansla yayılmasını istiyor ve arka plan müziği videonun tamamını türev hâline getiriyor — kanalın kendi içeriğini o lisansa bağlamak demek. Seçim **atıf istemeyeni** tercih ediyor: CC BY'de atıf açıklamaya girmek zorunda ve o açıklama sonradan kısalırsa lisans ihlal ediliyor. `MusicBed.License` nullable ve bilinçli öyle — eksikliği görülebilir olmalı; **bloklayıcı QC kontrolü** (13.) bunun dolu olmasına bakıyor ve atıf gerekiyorsa yazar adı da şart, yoksa atıf yapılamaz. Müzik yoksa kontrol geçiyor: müziksiz video tamamen geçerli. **Canlı sorguda iki hata bulundu:** (1) `license_version` snake_case olduğu için hiç okunmuyordu ve lisans sürümü sessizce düşüyordu — oysa CC BY 2.0 ile 4.0'ın gereklilikleri farklı; (2) Openverse terimleri **VE** ile birleştiriyor, "ambient documentary underscore" sıfır sonuç veriyor ve boş sonuç sessizce "müzik yok" olarak geçiyordu — terimler tek kelimeye indirildi. Altı ruh hâlinin altısı da canlı olarak parça buluyor. 15 test
  - *Kalan:* İndirme + timeline'a bağlama; ducking zaten var (P0)
- [ ] **P2-10** `3p` — DLQ triyaj ekranı: yeniden dene / node atla / run iptal
  - *Bitti:* Takılan run insan müdahalesiyle 3 tıkta kurtarılıyor
- [~] **P2-11** `3p` — Bölüm bazlı render + segment önbelleği
  - *Kısmen:* 28 Ağu 2026 — Önbellek anahtarı yazıldı ve saf: "bu segment değişti mi" kararı, gerçek bir render yapılarak öğrenilecek bir şey olmamalı — render bu hattın en yavaş adımı. **Anahtar neye bağlı olmalı**: segmentin görüntüsünü belirleyen her şey (süre, görsel, Ken Burns, üst yazı, geçiş, tuval, font zinciri). Eksik bırakılan tek alan, o alan değiştiğinde **bayat** bir segmentin kullanılması demek — ve bayat kare, sessiz olduğu için hiç önbellek olmamasından kötü. **Anahtar neye bağlı olmamalı**: mutlak zaman ve sıra numarası. Girselerdi, önündeki bir sahne uzayınca ya da sıra değişince görüntüsü hiç değişmemiş bütün segmentler geçersiz olurdu — önbellek hiç yokmuş gibi davranırdı. Üst yazının zamanı sahneye göre ölçülüyor, aynı sebeple. Kabul kriteri **sayı olarak** sınanıyor: üç sahneli bir timeline'da ortadakinin görseli değişince yalnız 1 segment bayat, 2'si yeniden kullanılıyor. Anahtar sürümü var — düzeltilen bir çizim hatası eski segmentlerde yaşamaya devam ederse, o hata artık kodda görünmediği için teşhis edilemez. 14 test
  - *Kalan:* Segmentlerin ayrı render edilip birleştirilmesi (ffmpeg concat)
- [~] **P2-12** `2p` — Sürekli mod: `continuous` strateji, günlük hedef, tür karışımı
  - *Kısmen:* 28 Ağu 2026 — Strateji saf: "sıradaki video hangi türden" kararı, on iki saat sistem koşturularak öğrenilecek bir şey olmamalı. **En çok geride kalan tür** seçiliyor, paya göre zar atılmıyor: rastgele seçim uzun vadede doğru orana yakınsıyor ama kısa vadede sapıyor ve günde beş video üreten bir kanalda "uzun vade" haftalar demek — o haftalarda oran gözle görülür biçimde yanlış oluyor. Yirmi videoluk koşuda %60/%30/%10 gerçekten tutuyor (test). İlk video en büyük paylı türden ve **karar kararlı**: rastgele seçmek, aynı yapılandırmanın iki koşuda farklı başlaması ve bir sorunun tekrarlanabilirliğinin bozulması demekti. Paylar normalleştiriliyor — "%60, %30, %10" ile "6, 3, 1" aynı anlama gelmeli, toplamın tam 1 olmasını şart koşmak her ayar değişikliğinde elle toplama yaptırırdı. Kalan hedef **negatif olamıyor**: bir döngüde geriye sayarsa sonsuza kadar üretim tetikler. Sapma ölçülebilir ve panelde görünmeli — büyük sapma ya hedeflerin yeni değiştiğini ya da bir türün sürekli üretilemediğini (konu havuzu boş) söylüyor, ikincisi sessizce olabilecek bir arıza. 9 test
  - *Kalan:* 12 saatlik kesintisiz koşu — veritabanı ve zamanlayıcı döngüsü gerekiyor
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
