# Araçlar Yan-Servisi

Üç uç nokta. Hepsi .NET tarafının **yapamadığı** ya da **iyi yapamadığı**
işler — yan-servis bir mimari tercih değil, teknik bir zorunluluk
(ADR-002r).

| Uç nokta | Ne yapar | Neden .NET'te değil |
|---|---|---|
| `POST /search` | SearXNG üzerinden web araması | Tek dış kapı olsun diye; .NET'te de var (P1-05b) |
| `POST /fetch` | Sayfayı **tarayıcıyla** açıp metnini çıkarır | JS ile kurulan sayfalar düz HTTP ile alınamıyor |
| `POST /align` | Sesten **kelime zamanlarını ölçer** | Doğru hizalama wav2vec2 istiyor; .NET'te karşılığı yok |
| `GET /health` | Ayakta mı **ve neyi yapabiliyor** | — |

Üçü de **durumsuz**: iki çağrı arasında hiçbir şey hatırlanmıyor. Servis
her an yeniden başlatılabilir ve birden fazla örneği yan yana koşabilir.

---

## `/health` neden yetenekleri de söylüyor

Çünkü "ayaktayım" tek başına yanıltıcı. Playwright kurulu olmayan bir
servis de sağlıklı görünür ve `/fetch` çağrısı ancak çalışma anında
patlar.

Bu depoda tam olarak bu sınıftan bir hata yaşandı: Windows TTS kelime
zamanlaması vermiyordu, zamanlama olmayınca ipucu üretilemiyordu ve
**gerçek videolar altyazısız çıkıyordu** — sahte hatta altyazı vardı,
gerçek hatta yoktu, ve fark hiçbir yerde görünmüyordu.

O yüzden:

- `/health` her yeteneğin açık olup olmadığını **ve kapalıysa nedenini**
  söylüyor.
- Kapalı bir yeteneğe yapılan çağrı **503** dönüyor. .NET tarafı bunu
  `Resource` hatası olarak okuyor (ADR-011): iş başarısız değil,
  **ertelenmiş** — biri Playwright'ı kurduğunda çalışacak.

---

## Kurulum

Zorunlu bağımlılıklar yalnızca sunucunun kendisi. Ağır olanlar isteğe
bağlı: bir makinede yalnızca `/search` gerekiyorsa 1 GB'lık bir model
ağacı indirmek saçma olurdu.

```bash
pip install -e "tools[dev]"
```

Tarayıcıyla çekme için:

```bash
pip install -e "tools[fetch]" && python -m playwright install chromium
```

Hizalama için (ayrıca `ffmpeg` gerekiyor):

```bash
pip install -e "tools[align]"
```

### Hizalamayı ekran kartında koşturmak

`BMAI_ALIGN_DEVICE=auto` kartı **gerçekten kullanılabiliyorsa** kullanıyor.
Kartın görünmesi yetmiyor: `faster-whisper` arkasında CTranslate2 var ve
CTranslate2 CUDA için cuBLAS ile cuDNN istiyor. Sürücü kurulu, kart
sayılıyor, ama bu iki kütüphane yoksa çağrı **modelin ortasında** düşüyor
(`cublas64_12.dll is not found`).

`/health` bunu açıkça söylüyor:

```
align  true  small / auto -> cpu (kart var ama CUDA kutuphaneleri eksik: cublas64_12.dll, cudnn_ops64_9.dll)
```

Açmak için CUDA 12 çalışma zamanı + cuDNN 9 kurulmalı. En kolay yol:

```bash
pip install nvidia-cublas-cu12 nvidia-cudnn-cu12
```

Kurulduktan sonra `auto` kendiliğinden `cuda`ya geçiyor — yeniden
başlatmak yeterli, kod değişikliği yok. 8 GB'lık bir kartta `small`
model rahat sığıyor; `large-v3` sığmıyor.

Çalıştırma:

```bash
python -m uvicorn bmai_tools.main:app --host 127.0.0.1 --port 8099
```

Docker ile (hizalama kapalı gelir):

```bash
docker compose --profile tools up -d tools
```

---

## Yapılandırma

| Değişken | Varsayılan | Açıklama |
|---|---|---|
| `BMAI_SEARXNG_URL` | `http://localhost:8888` | SearXNG adresi |
| `BMAI_FETCH_TIMEOUT` | `30` | Sayfa açma sınırı (saniye) |
| `BMAI_USER_AGENT` | `BytemountsAiStudio/0.1 …` | Kimliğimizi veren ajan |
| `BMAI_ALIGN_MODEL` | `small` | Whisper modeli — VRAM'e göre |
| `BMAI_ALIGN_DEVICE` | `auto` | `auto` / `cuda` / `cpu` |
| `BMAI_ALIGN_COMPUTE` | `int8` | Niceleme |

.NET tarafında servisin adresi `BMAI_TOOLS_URL`. Uzak olabilir ve
genelde uzak olacak — sebebi `docs/DONANIM-VE-MODEL.md` ile aynı:
hizalama bir model koşturuyor ve filodaki 2 GB'lık kartlara sığmıyor.

---

## Tarayıcı olmak muafiyet değil

`/fetch` gerçek bir tarayıcı koşuyor. En kolay hata şu olurdu: çekme
işi ayrı bir sürece taşındığı için P1-06'nın dört kapısının burada
uygulanmaması. O zaman bir çağrıyı .NET çekicisinden yan-servise
taşımak, farkında olmadan politikayı devre dışı bırakmak olurdu.

Kapılar burada da var ve **aynı sırada**:

1. **Şema** — `file:`, `ftp:`, `data:` reddediliyor. Bir tarayıcıda
   `file:` şeması sunucunun diskini okutur.
2. **robots.txt** — okunamıyorsa **çekilmiyor**. "Okuyamadım, o hâlde
   serbesttir" ters yönde bir hata olurdu, üstelik sunucu zorlanırken.
3. **Boyut** — metin `max_chars`'ta kırpılıyor ve kırpma **görünür**
   (`truncated`).

Sıra önemli: robots kontrolü boyut sınırından önce, çünkü yasak bir
sayfaya "sadece bakmak" da çekmek sayılıyor.

---

## `/align` neden metni de istiyor

Zamanlama **modelden**, yazım **metinden** alınıyor.

ASR kelimeyi yanlış duyabiliyor ve özellikle özel isimlerde duyuyor. Ama
biz metni zaten biliyoruz — senaryoyu kendimiz ürettik. Sayılar bu
ayrımın en görünür olduğu yer: metinde "1453" yazıyor, model "bin dört
yüz elli üç" duyuyor. Altyazıya modelin duyduğu yazılsaydı ekranda yazan
şey senaryodan farklı olurdu.

Kelime sayıları tutmuyorsa ölçüm **olduğu gibi** dönüyor: hizalı olmayan
bir metni zorla eşleştirmek, hepsini kaydırmaktan daha kötü.

---

## Test

```bash
cd tools && python -m pytest -q
```

Testler ağır bağımlılıklara **dokunmuyor**: ne tarayıcı açılıyor ne
model yükleniyor. Sınanan şey saf mantık — kapılar, ayrıştırma, bozuk
aralıkların düzeltilmesi. Bunlar CI'da milisaniyeler sürüyor ve bir
modelin o günkü keyfine göre kırmızı yanmıyor.
