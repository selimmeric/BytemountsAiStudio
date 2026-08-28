# Dağıtım — Docker ve Linux (P4-05)

Geliştirme Windows'ta, üretim Linux kaplarında. Bu belge ikisinin
arasındaki farkları ve kap içinde çalıştırırken **gerçekten ortaya
çıkan** sorunları anlatıyor.

## Çalıştırma

Altyapı (Postgres, Seq) ve uygulama ayrı dosyalarda:

```bash
docker compose -f docker-compose.yml -f docker-compose.uygulama.yml up -d --build
```

Zamanlayıcı **varsayılan kapalı** — kap ayağa kalkar kalkmaz üretim
başlatmamalı, çünkü bu döngü gerçek para harcayabiliyor:

```bash
BMAI_ORCHESTRATOR=on docker compose -f docker-compose.yml -f docker-compose.uygulama.yml up -d worker
```

Panel: `http://127.0.0.1:8080`

## Kabı çalıştırmak dört gerçek hata buldu

Hepsi yerelde geçen, imajı başarıyla derlenen ve **sağlıklı başlayan**
bir sistemde vardı.

### 1. `.editorconfig` derleme bağlamında yoktu

Analiz kuralları orada bastırılıyor ve uyarılar hata sayıldığı için
kap içindeki derleme, yerelde geçen kodu reddetti. İki derlemenin
farklı kurallarla koşması bu depoda tekrar eden hata sınıfı.

Bu hata **gürültülü**: derleme düştü, hemen görüldü.

### 2. İstem dosyaları imaja hiç girmedi — sessiz

`Contracts` projesi istemleri `../../prompts/**` globuyla derlemeye
gömüyor. Dockerfile yalnızca `src/` kopyalıyordu.

Glob hiçbir dosya bulmadı. **MSBuild tek bir uyarı bile vermedi.**
İmaj başarıyla derlendi, kap sağlıklı başladı, ve hiçbir video
üretemedi: her run senaryo adımında `prompts.empty` ile düştü.

İki düzeltme yapıldı, çünkü tek başına birincisi aynı hatanın tekrar
etmesini engellemiyor:

- `COPY prompts/ ./prompts/`
- Boş glob artık **derlemeyi düşürüyor** (`IstemlerGomuluMu` hedefi).
  Bu depoda istemsiz bir derlemenin geçerli bir kullanımı yok; boş bir
  glob "istem yok" demek değil, "bir şey yanlış" demek.

### 3. `aspnet` imajında ne curl ne wget var

API'nin sağlık kontrolü `wget` ile yazılmıştı ve her seferinde
"not found" ile düştü. Kap `unhealthy` oldu, `restart: unless-stopped`
onu yeniden başlattı — **API'nin kendisi gayet sağlıklıyken.**

Sağlık kontrolü, kontrol ettiği şeyden daha kırılgan olmamalı.

### 4. Sağlık kontrolü yazmak "otomatik yeniden başlatma" demek değil

Docker'ın `restart` politikası yalnızca **çıkan** kabı yeniden
başlatıyor. `unhealthy` işaretlenmiş ama çalışmaya devam eden bir kabı
Compose kendi başına yeniden başlatmıyor: sağlık kontrolü yalnızca
rapor veriyor.

Çözüm olarak sağlıksız kapları yeniden başlatan bir yardımcı kap
(autoheal deseni) kullanılmadı: o kap Docker soketine erişmek zorunda
ve **o soket makinede kök yetkisine denk**. Üretim hattının yanında
böyle bir yetki taşımaktansa süreç kendisi çıkıyor (bkz. aşağısı).

## Sağlık sinyali neyi ölçüyor

Süreç canlılığı **yetmiyor** ve bunun sebebi bu depoda yaşandı: süreç
ayaktaydı, bütün kuyruk döngüleri her turda istisna atıyordu, saniyede
bir hata satırı basılıyordu ve hiçbir video üretilmiyordu. Kap
sağlıklı görünüyordu.

`QueueWorker` hatayı bilerek yutuyor — tek bir işin hatası o kuyruğu
durdurmamalı. Doğru karar; bedeli, dışarıdan bakan hiçbir şeyin "bu
döngü hiç iş bitiremiyor" diyememesiydi.

Ölçülen şey **"iş yapıldı mı" değil, "sürekli düşüyor mu"**. Kuyruğu
boş bir worker hiç iş yapmıyor ve tamamen sağlıklı; ayıran şey
**aralıksız hata**.

| süre | ne oluyor |
|---|---|
| 0–60 sn aralıksız hata | henüz sağlıklı — geçici hata beklenen şey |
| 60 sn+ | kalp atışı `healthy:false`, kap `unhealthy` |
| 5 dk+ | süreç kendini kapatıyor, `restart` politikası yeniden başlatıyor |

Sıra kasıtlı: **önce bildir, sonra harekete geç.** Eşit olsalardı,
geçici bir aksaklıkta iş yapan bir süreç durup dururken öldürülür ve
devam eden bir render çöpe giderdi.

Kalp atışı dosyası iki şeyi birden taşıyor ve ikisi de gerekli:

- `at` — yazma zamanı. Süreç dondu ya da öldüyse dosya eskir.
- `healthy` — döngüler koşuyor mu.

Yalnızca zaman damgası yazsaydık yukarıdaki arızayı kaçırırdık: süreç
canlıydı, dosya tazeydi. Yalnızca `healthy` yazsaydık donmuş bir süreç
sonsuza kadar "sağlıklı" kalırdı, çünkü dosyadaki son değer `true`'da
donardı.

## İmajda ne var ve neden

| paket | neden |
|---|---|
| `ffmpeg` | Render onsuz hiç çalışmıyor |
| `curl` | Sağlık kontrolü (yukarıdaki 3. madde) |
| `fonts-dejavu-core` | Latin, geniş kapsama |
| `fonts-liberation` | Arial/Georgia/Verdana metrik karşılıkları — kanal ayarlarında bu adlar geçiyor |
| `fonts-noto-core` | Arapça dahil (P3-09 üçüncü dil) |

**Yazı tipleri sessiz bir tuzak:** altyazı SkiaSharp ile PNG olarak
çiziliyor ve boş bir Linux imajında hiç yazı tipi yok. Video
**üretilir**, sadece yazısı olmaz — QC bunu yakalamıyor, çünkü süre,
çözünürlük ve ses doğru. Kap içinde ölçüldü: altyazı bandında
parlaklık 5–198 aralığında, yani yazı gerçekten çiziliyor.

`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false` **zorunlu**: invariant
kültür Türkçe i/I dönüşümünü bozuyor ve tekillik kontrolü sessizce
yanlış çalışıyor (P3-09).

Kap **kök olmayan** kullanıcıyla koşuyor; yazılan tek yerler
`/veri/storage`, `/veri/output` ve `/veri/durum`.

## Kap içinde ölçülen sonuç

Dört hata düzeltildikten sonra, Linux kaplarında, insan müdahalesi
olmadan:

| kanal | iş akışı | süre | tuval | QC | tur |
|---|---|---|---|---|---|
| Sahte Kanal (TR) | `shorts-fake` | 47,9 sn | 1080×1920 | 0,97 | 0 |
| Fake Channel (EN) | `video-uzun` | 603,4 sn | 1920×1080 | 0,97 | 0 |
