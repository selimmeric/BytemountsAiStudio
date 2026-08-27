# Kararlar ve Ek Talimatlar

Orijinal iş emrinden sonra gelen talimatlar, sorulan sorular ve verilen cevaplar. Tarih sırasıyla.

---

## 27 Ağustos 2026 — Mimari kararları

Mimari analiz sunulduktan sonra üç soru soruldu, üçü de cevaplandı.

### K-01 · Kalite / maliyet ekseni

**Soru:** Ucuz kurgu (~$0.06/Shorts, stok görsel + ucuz TTS) mı, premium kurgu (~$0.56/Shorts, AI görsel + ElevenLabs) mı?

**Cevap:** *"Karma: ucuz başla, ölç, yükselt"*

**Etkisi:** MVP'de ucuz adaptörler. Provider yönlendirme politikası (mimari §9.3) yükseltmeyi bir konfigürasyon değişikliğine indirger. Maliyet defteri (`provider_calls`) birinci gün devrede — "ölç" kısmı buna dayanıyor.

### K-02 · Render motoru ve Bytemounts-Studio ilişkisi

**Soru:** Studio'nun render çekirdeğini ortak pakete ayıralım mı, kopyalayalım mı, sıfırdan mı yazalım?

**Cevap:** *"sıfırdan yaz ama ders al Studio'dan .. daha temiz nasıl yazılır düşün araştır bul ve yap."*

**Etkisi — bu karar mimarinin en büyük değişikliğini tetikledi:**

- **ADR-001r:** Render motoru sıfırdan yazılır. Studio bir ders kaynağıdır, kod kaynağı değil. `render/service.py` (2.360 satır, içinde 435 satırlık tek `build_filter_graph`) okundu; yedi somut ders çıkarıldı (mimari §12.1).
- **ADR-005r:** FFmpeg ham string birleştirmeyle değil, tipli bir **Filter Graph IR** üzerinden sürülür. Metin yalnız SkiaSharp + HarfBuzz ile; `drawtext` hiç kullanılmaz.
- **ADR-002r:** Studio'yu paketleme kararı düşünce "medya düzlemi Python olsun" gerekçesi de düştü → **tek dil: .NET**. Tek istisna, kelime hizalaması için küçük bir Python ASR yan servisi.
- **ADR-014:** Bölüm bazlı render + segment önbelleği.
- Faz 0 süresi 2–3 haftadan **4–5 haftaya** çıktı. Bilinçli ödenen bedel.

### K-03 · Dil ve pazar

**Soru:** Sadece Türkçe mi, sadece İngilizce mi, paralel mi?

**Cevap:** *"2 dilde olsun hatta daha fazla dilde ekleyebiliriz."*

**Etkisi:** Dil, sonradan eklenen bir alan değil **birinci sınıf boyut** oldu (ADR-013, mimari §20). Dört ayrı kavram ayrıştırıldı: `research_language` / `content_language` / `audience_locale` / `ui_language`. MVP bilerek iki dille çıkıyor — soyutlamanın gerçekten çalıştığını kanıtlamanın tek yolu ikinci dili baştan koşturmak.

> **Not:** Mimari dokümanında "TR+EN paralel MVP'yi büyütür, Phase 3'e bırakılması önerilir" demiştim. Karar aksi yönde verildi; itiraz kayda geçti ve plan buna göre kuruldu.

---

## 27 Ağustos 2026 — İş planı ve API sınırı

**Talimat:** *"iş planı yap. bana göster adım adım yapıldıkca işaretlenen birşey olsun ve bu işplanının %100 degerini göstersin yani ilerleyişi görelim. ayrıca benim elimde çok fazla AI apisi yok sınırlarım var burda bunu başka türlü yapabilecek yada aramaları AI ile browser açarak yapabilecek bir python koduda lazım olabilir ?"*

**Yapılan:**

1. `docs/IS-PLANI.md` — 7 faz, 103 görev, 354 puan. Her görevde kod, efor puanı ve kabul kriteri.
2. `scripts/plan_progress.py` — plandan yüzdeleri hesaplar, `docs/plan-dashboard.html` panosunu üretir. Plan tek gerçek kaynak; pano ondan türer.
3. Pano iki ayrı yüzde gösterir: **MVP (Faz 0–2)** ve **tüm plan**. MVP %100 olduğunda toplam %68'dir; tek sayı yakın hedefi gizlerdi.

**API sınırı kararı — ADR-015 (mimari §9.5):** Sınırlı API bütçesi bir kısıt değil, tasarım girdisi.

| İhtiyaç | Ücretsiz / yerel yol |
|---|---|
| Arama | SearXNG (kendi sunucunuzda), DuckDuckGo, Brave ücretsiz kotası |
| Ansiklopedik bilgi | Wikipedia + Wikidata API |
| Sayfa içeriği | Playwright ile tarayıcı render'lı çekme |
| Ucuz LLM işleri | Ollama ile yerel model |
| Orta LLM | Gemini / OpenRouter ücretsiz kotaları |
| Kelime zamanlaması | WhisperX yerel veya TTS'in kendi timing'i |

Para yalnız senaryo üretiminde harcanır — video başına 1–2 çağrı.

**Reddedilen yaklaşım:** AI web arayüzlerini (ChatGPT / Gemini paneli) tarayıcı otomasyonuyla sürmek. Kullanım şartlarına aykırı, bot tespitine takılır, her arayüz değişiminde kırılır, hesabı riske atar. Tarayıcı otomasyonu yalnız açık web sayfalarının içeriğini çekmek için, robots.txt'e uyarak kullanılır.

Python kodu tek yerde toplandı: **`tools-sidecar`** (plan görevi P1-04) — `/search`, `/fetch`, `/align`.

---

## 27 Ağustos 2026 — Belge düzeni ve inşaata başlama

**Talimat:** *"proje icine documents diye bir klasör aç. ve her verilen açıklama yada iş emri dökümanını oraya kopyala. sistem hakkındaki yorumlarınıda buraya yaz. sistemin temelini oluşturmaya başla"*

**Yapılan:**

1. `documents/` klasörü açıldı; iş emri birebir kopyalandı, kararlar bu dosyaya işlendi, sistem değerlendirmesi `03-sistem-degerlendirmem.md`'ye yazıldı.
2. Faz 0 inşaatı başladı (P0-01…).

**Ortam tespiti:**

| Araç | Durum |
|---|---|
| .NET SDK | 10.0.400 (LTS) + 9.0.101 → **hedef `net10.0`** |
| git | 2.52.0 |
| Python | 3.14.0 |
| FFmpeg | 8.0.1 |
| Docker | **Yok** — PostgreSQL için karar gerekiyor (bkz. §Açık konular) |

**Açık konular:**

- **PostgreSQL kurulumu:** Docker olmadığı için ya Docker Desktop kurulacak ya da PostgreSQL 16 + pgvector Windows'a doğrudan kurulacak. Karar bekliyor (P0-02 bu karara bağlı).
- **Python 3.14:** WhisperX/PyTorch tekerlekleri bu sürümde henüz olmayabilir; ASR yan servisi için ayrı bir 3.11/3.12 sanal ortamı gerekebilir. P1-04'te doğrulanacak.
- Mimari dokümanındaki `.NET 9` referansları `net10.0` olarak güncellendi.
