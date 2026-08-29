# Donanım ve Model Seçimi

Bu belge iki soruya cevap veriyor:

1. Elimizdeki donanımda **hangi model koşar**?
2. Model koşturamayan makineler **ne işe yarar**?

İkincisi birincisinden önemli. Filodaki makinelerin çoğu LLM koşturamıyor
ama sistemin en pahalı işi olan **render**'ı koşturabiliyor — ve render
ekran kartı değil, işlemci istiyor.

---

## Kural: bir model VRAM'e sığmazsa kullanılamaz

Ollama modeli ekran kartına sığdıramazsa RAM'e taşırıyor ve işlemciden
koşturuyor. "Çalışmıyor" değil, **10-40 kat yavaş çalışıyor** — bir
senaryo üretimi saniyeler yerine dakikalar sürüyor. Bu sessiz bir
başarısızlık: hiçbir hata mesajı yok, yalnızca kuyruk büyüyor.

Kaba hesap, Q4 nicelemesi (Ollama'nın varsayılanı) için:

| Model boyutu | Ağırlıklar | + bağlam (8K) | Gereken VRAM |
|---|---|---|---|
| 7B  | ~4,4 GB | ~0,8 GB | **~5,5 GB** |
| 9B  | ~5,5 GB | ~0,9 GB | ~6,8 GB |
| 14B | ~8,5 GB | ~1,2 GB | ~10 GB |
| 30B | ~18 GB  | ~2 GB   | ~21 GB |
| 70B | ~40 GB  | ~3 GB   | ~45 GB |

Bunun üstüne Windows'un masaüstü için ayırdığı **0,5-1 GB**'ı ekleyin.

---

## Elimizdeki donanım

### 1. Ana makine — RTX 4060 Laptop, 8 GB VRAM

Filodaki **tek** LLM koşturabilen makine.

- **Sığan en büyük sınıf: 7B (Q4).** 8 GB'ın ~6,5 GB'ı kullanılabilir;
  7B rahat, 9B sınırda, 14B hiç sığmıyor.
- **Seçim: `qwen2.5:7b-instruct`.**
  - Türkçesi bu boyut sınıfındaki alternatiflerden belirgin biçimde iyi.
  - Yapılandırılmış çıktı (`format` ile zorlanan JSON) güvenilir —
    sistemin bütün LLM çağrıları zorunlu araç çağrısı kullanıyor, bu
    özellik pazarlık konusu değil.
  - 32K bağlam: araştırma çıktısını senaryo istemine sığdırmaya yetiyor.

> **Coder modelleri kullanılmıyor.** Makinede `qwen2.5-coder:7b`,
> `yi-coder:9b`, `deepseek-coder` kurulu; hepsi kod için eğitilmiş ve
> anlatı yazmakta zayıflar. Aynı VRAM'e sığan genel bir instruct modeli
> senaryo kalitesinde belirgin fark yaratıyor.

> **Kurulu büyük modeller kullanılamaz.** `deepseek-r1:70b`,
> `deepseek-coder:33b`, `qwen3.5:35b`, `qwen3-coder:30b`, `llama2:13b`
> — hiçbiri 8 GB'a sığmıyor. Ollama bunları yine de çalıştırır, RAM'den,
> kabul edilemez bir hızda.

**Gömme modeli: `paraphrase-multilingual`** (768 boyut).

İki zorunluluk:

- **Çok dilli olmak zorunda** (§20.5). Konu tekilliği kanal + dil
  kapsamında ölçülüyor ama diller arası karşılaştırma da gerekiyor;
  dile özel bir model kullanılırsa TR ve EN vektörleri aynı uzayda
  olmaz ve karşılaştırma anlamsızlaşır.
- **768 boyutlu olmak zorunda** — şema öyle (ADR-003). `bge-m3` ve
  `arctic-embed2` daha iyi ama 1024 boyutlu; geçmek şema göçü demek.

### 2. Filo — 6-7 eski dizüstü, 16 GB RAM, GTX 1050 2 GB

**Bunlar LLM koşturamaz.** 2 GB VRAM'e en küçük kullanışlı model bile
sığmıyor; 3B bir model sığsa dahi çıktı kalitesi bu sistem için yetersiz.
Bu makineleri "yardımcı model sunucusu" yapmaya çalışmak zaman kaybı.

**Yapabildikleri iş: render.** FFmpeg encode işlemi işlemci-yoğun;
`libx264` ekran kartını hiç kullanmıyor. 16 GB RAM ve 4 çekirdekli bir
i5/i7, 60 saniyelik bir dikey videoyu makul sürede kodluyor. Bu makineler
**render worker** olarak koşuyor (P4-01) ve LLM'e ihtiyaç duydukları
noktada ana makineye bağlanıyorlar.

Yapabildikleri diğer işler: TTS (Windows yerel sentezi, işlemci),
araştırma (ağ), görsel indirme (ağ), yükleme (ağ). Hattın LLM dışındaki
her adımı bu makinelerde koşuyor.

---

## Yapılandırma: yerelde varsa yerel, yoksa dışarıdan

> "varsa modelimiz kurarız bizimkine, yoksa dışarıdan servis alırız"

Filodaki her makine **aynı ikiliyi** koşuyor. Farklı olan yalnızca ortam
değişkenleri. Üç senaryonun hiçbiri kod değişikliği gerektirmiyor.

| Değişken | Varsayılan | Ne işe yarıyor |
|---|---|---|
| `BMAI_OLLAMA_URL` | `http://localhost:11434` | Ollama'nın adresi — **uzak olabilir** |
| `BMAI_OLLAMA_MODEL_CHEAP` | `qwen2.5:7b-instruct` | Hacimli işler: sınıflandırma, normalizasyon |
| `BMAI_OLLAMA_MODEL_STANDARD` | `qwen2.5:7b-instruct` | Araştırma planı, iddia çıkarma |
| `BMAI_OLLAMA_MODEL_STRONG` | `qwen2.5:7b-instruct` | Senaryo — anahtar gelince ücretli sağlayıcıya geçecek |
| `BMAI_OLLAMA_EMBEDDING` | `paraphrase-multilingual` | Konu tekilliği (768 boyut, çok dilli) |
| `BMAI_OLLAMA_TIMEOUT` | `300` (saniye) | Model yükleme süresi — 14B model ilk çağrıda beşi aşabiliyor |
| `BMAI_OLLAMA_CPU` | kapalı | **Modeli ekran kartına hiç yükleme** (`num_gpu: 0`) |

### Senaryo D — ekran kartı sorunlu makine

**Bu senaryo bu depoda gerçek bir olaydan doğdu:** model yüklenirken
makine mavi ekran verdi. Sorun modelin kendisi değil, **GPU yolu**.

```
BMAI_OLLAMA_CPU=1
BMAI_OLLAMA_MODEL_CHEAP=qwen2.5:0.5b-instruct
BMAI_OLLAMA_MODEL_STANDARD=qwen2.5:0.5b-instruct
BMAI_OLLAMA_MODEL_STRONG=qwen2.5:1.5b-instruct
```

`num_gpu: 0` katmanların **tamamını** CPU'da tutuyor — kart hiç
açılmıyor. Bedeli hız ve bu kabul edilebilir bir takas: 0,5B bir model
CPU'da saniyeler mertebesinde cevap veriyor ve fabrikanın hızını
belirleyen şey zaten render. Alternatif — hiç yerel model olmaması —
senaryo üretimini tamamen durduruyor.

**Kalite düşüyor ve bu gizlenmiyor:** 0,5B bir model 7B'nin yazdığı
senaryoyu yazmıyor. Bu kip, kartı olmayan bir makinede hattın
*çalışmasını* sağlıyor; *iyi* çalışmasını değil.


### Senaryo A — ana makine (model bizde)

Hiçbir şey ayarlanmıyor. Varsayılanlar zaten yerel Ollama'yı ve 8 GB'a
sığan modelleri gösteriyor.

### Senaryo B — filo makinesi (model bizde ama başka makinede)

Zayıf makine, ana makinedeki Ollama'ya bağlanıyor:

```bash
setx BMAI_OLLAMA_URL http://192.168.1.40:11434
```

Ana makinede Ollama'nın ağ dinlemesi gerekiyor — varsayılan yalnızca
localhost:

```bash
setx OLLAMA_HOST 0.0.0.0:11434
```

> Bu, Ollama'yı **yerel ağa açıyor** ve Ollama'da kimlik doğrulama yok.
> Yalnızca güvenilen bir ağda yapın; makine doğrudan internete açıksa
> önüne kimlik doğrulaması olan bir ters vekil koymak gerekir.

### Senaryo C — model bizde yok (dışarıdan servis)

Ollama API'siyle uyumlu herhangi bir uç nokta kullanılabilir:

```bash
setx BMAI_OLLAMA_URL https://ollama.ornek.com
setx BMAI_OLLAMA_MODEL_STRONG llama3.1:70b
```

Burada model artık VRAM'imize sığmak zorunda değil: 70B bir model
kiralanmış bir makinede koşuyor, biz yalnızca çağırıyoruz. Senaryo
katmanının ücretli bir sağlayıcıya geçmesi de aynı yerden yapılıyor
(`config/providers.json` → `openai` / `gemini` kaydı, ADR-015).

### Senaryo D — daha güçlü bir makine alınırsa

24 GB'lık bir kart geldiğinde yalnızca güçlü katman büyütülüyor,
diğerleri 7B kalıyor — ucuz işler için 14B koşturmak israf:

```bash
setx BMAI_OLLAMA_MODEL_STRONG qwen2.5:14b-instruct
```

---

## Hizalama modeli (ASR)

Kelime zamanları sesten ölçülüyor (P1-04 `/align`, P1-15). Model
`faster-whisper`, arkasında CTranslate2 — Ollama'dan **ayrı** bir
çalışma zamanı ve ayrı bir VRAM bütçesi.

| Model | VRAM (int8) | Not |
|---|---|---|
| `small` | ~1 GB | **Varsayılan.** Türkçede yeterli, 8 GB kartta Ollama ile birlikte sığıyor |
| `medium` | ~2,5 GB | Belirgin bir kazanç yok; hizalamada asıl iş zamanlama, tanıma değil |
| `large-v3` | ~5 GB | Ollama ile aynı anda **sığmıyor** |

`small` seçilmesinin sebebi kalite değil **birlikte yaşama**: aynı
kartta bir 7B LLM koşuyor ve ikisi aynı 8 GB'ı paylaşıyor.

> **Kart görünüyor olması yetmiyor.** CTranslate2 CUDA için cuBLAS ve
> cuDNN istiyor; sürücü kurulu ve kart sayılıyor olsa bile bu iki
> kütüphane yoksa çağrı modelin ortasında düşüyor. Yan-servis bunu
> önceden kontrol edip CPU'ya düşüyor **ve nedenini `/health`'te
> söylüyor** — sessiz bir düşüş, 10 kat yavaş koşan bir servisi
> gizlerdi. Açmak için: `pip install nvidia-cublas-cu12 nvidia-cudnn-cu12`.

Hizalama CPU'da da koşuyor: 4 saniyelik bir ses ~3 saniye sürüyor.
Filodaki 2 GB'lık makineler bu işi yapabilir — yavaş ama yapabilir.

---

## Kurulum

Ana makinede, bir kez:

```bash
ollama pull qwen2.5:7b-instruct
```

```bash
ollama pull paraphrase-multilingual
```

Doğrulama:

```bash
ollama list
```

---

## Neden ölçüm yerine tablo

Yukarıdaki VRAM sayıları ölçüm değil, hesap. ADR-006 süreleri ölçmeyi
şart koşuyor ama o karar **çalışma zamanı** çıktıları için: bir videonun
süresi tahmin edilemez, ölçülür. Model boyutu ise dosya boyutundan
bilinen bir sabit; koşturmadan önce bilinmesi gereken bir şey ve zaten
koşturamadığımız bir modeli ölçemeyiz.

Buna karşılık **model çıktısının kalitesi** ölçülüyor: mekanik QC
(P1-21) her koşuda senaryoyu puanlıyor ve model değiştirildiğinde farkı
gösteren şey o puan, bu tablo değil.

---

## ⚠️ ANA MAKİNENİN EKRAN KARTI ŞU AN GÜVENİLİR DEĞİL

**28 Ağu 2026 — bu bölüm bir tercih değil, bir arıza kaydı.**

Ana makinede ekran kartına büyük bir model yüklemek sistemi
**çökertiyor**. Bu tahmin değil, olay günlüğünden okunan bir sayı:

| Bulgu | Sayı |
|---|---|
| `0x00000113` mavi ekran (VIDEO_DXGKRNL_FATAL_ERROR) | 5 kez |
| `0x00000133` mavi ekran (DPC watchdog) | 1 kez |
| WHEA "düzeltilmiş donanım hatası" — son 8 gün | 47 olay |
| WHEA — **son 24 saat** | **25 olay** |

Sonuncusu en önemlisi: hata **hızlanıyor**. Düzeltilmiş hatalar
düzeltilemeyen hataların habercisi.

Son çökme 28 Ağu 04:34'te, 6 GB'lık bir görme modeli (`qwen2.5vl:7b`)
karta yüklenirken oldu. `0x113` kodu doğrudan ekran kartı sürücüsünü
işaret ediyor ve önceki dördünde de parametre NVIDIA'yı (`0x10de`)
gösteriyordu.

### Bunun geliştirmeye etkisi

**Ekran kartında model koşturulmuyor.** Bu, ADR-015'in (ücretsiz/yerel
önce) geçici olarak askıya alınması değil — yerel model hâlâ tercih,
ama **işlemcide** ve **küçük**.

Kod tarafında bunun karşılığı zaten var ve olması gereken de buydu:
her model çağrısı bir arayüzün arkasında (`ILlmProvider`,
`IVisionProvider`) ve testler sahte sağlayıcıyla koşuyor. Yani model
koşturamamak **geliştirmeyi durdurmuyor**; yalnızca "canlı modelle
doğrulandı" diyemiyoruz ve diyemediğimiz yerde bunu açıkça yazıyoruz.

Ollama'yı işlemciye zorlamak gerekirse:

```bash
set OLLAMA_NUM_GPU=0
```

### Donanım tarafında sırayla denenecekler

1. **NVIDIA sürücüsünü temiz kur** (DDU ile eskisini sil, sonra kur).
   `0x113` çoğu zaman sürücü kaynaklı.
2. **Dell BIOS ve chipset güncellemesi** — PCIe bağlantı hataları
   sıklıkla ana kart yazılımından geliyor.
3. **PCIe Link State Power Management → Off** (Güç Seçenekleri).
   Bağlantının uyku durumuna girip çıkması WHEA hatalarının bilinen bir
   kaynağı.
4. Hiçbiri düzeltmezse kart donanım olarak arızalı; garanti/servis.

Bunlar denenene kadar **ana makinede GPU'ya model yüklenmeyecek.**
