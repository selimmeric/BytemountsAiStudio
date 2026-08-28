# Yerel Veritabanı

> **Kısa cevap:** Veri deposu değişmiyor. PostgreSQL kalıyor — sadece
> Docker'sız koşuyor.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/yerel-postgres.ps1
```

Yönetici yetkisi gerekmiyor, sisteme hiçbir şey kurulmuyor, bağlantı
dizesi değişmiyor.

---

## Neden SQLite değil

Sorulan soru "hangi veritabanı" gibi görünüyor ama değil. Şemanın
**değerli kısmı** SQLite'ta ifade edilemiyor:

| Şemada kullanılan | SQLite'ta karşılığı |
|---|---|
| `FOR UPDATE SKIP LOCKED` | **Yok.** SQLite'ta satır kilidi yok, tek yazıcı var |
| pgvector `<=>` benzerlik + indeks | **Yok.** Her satırı çekip elle kosinüs hesaplamak gerekir |
| JSONB + `@>` operatörü | Kısmen; farklı sözdizimi, indeks davranışı başka |
| Kısmi indeksler | Var ama davranış farklı |
| ICU sıralama | Yok |

Bunlar süs değil, sistemin çalışma biçimi:

- **`SKIP LOCKED`** iş kuyruğunun ve konu havuzunun tamamı. İki worker
  aynı işi almasın diye var. SQLite'ta bunu taklit etmek, sınadığımız
  şeyi ortadan kaldırmak demek.
- **pgvector** konu tekilliği (ADR-003). Aynı konuyu iki kez üretmemek
  buna bağlı.
- **JSONB** node çıktılarının tamamı.

SQLite'a geçmek ikinci bir şema demek. İki şema, biri er geç diğerinden
ayrışır — ve o gün **yerelde geçen testler üretimde çalışmaz**. CI zaten
gerçek PostgreSQL koşuyor; yerelde farklı bir şey koşturmak, yeşil ama
hiçbir şey kanıtlamayan testler üretirdi.

JSON dosyaları daha da kötü: işlem (transaction) yok, eşzamanlılık yok.

---

## Neden Docker değil (şu an)

Docker Desktop bu makinede açılmıyor. `%LOCALAPPDATA%\Docker\run`
altında dört adet sahipsiz AF_UNIX soket dosyası var:

```
dockerEthernetVfkit        ReparsePoint
dockerInference            ReparsePoint
sailor-ingest.sock         ReparsePoint
userAnalyticsOtlpHttp.sock ReparsePoint
```

Docker açılışta tam bunları silmeye çalışıp "dosyaya erişilemiyor"
diyerek çöküyor. Denenip **işe yaramayanlar**: `Remove-Item`, yeniden
adlandırma, klasörü komple silme, `fsutil reparsepoint delete`,
`wsl --shutdown`.

Çözüm **yeniden başlatma**. Ama veritabanı gerektiren işin bir yeniden
başlatmayı beklemesi gerekmiyor — bu yüzden portatif kurulum var.

Docker geri geldiğinde ikisi de 5432'yi isteyecek; birini durdurmak
gerekiyor:

```bash
powershell -File scripts/yerel-postgres.ps1 -Action stop
```

---

## Ne kuruluyor

`%LOCALAPPDATA%\bmai-postgres` altında, tek klasörde:

- **PostgreSQL 16.10** — EDB'nin *kurulumsuz* ikili paketi (~320 MB zip).
  CI ile aynı ana sürüm.
- **pgvector 0.8.0** — MSVC ile derlenip aynı klasöre kuruluyor.
- Veri klasörü, `initdb` ile `--locale-provider=icu --icu-locale=und-x-icu`
  — `docker-compose.yml` ile birebir aynı.

Eklentiler `template1`'e kuruluyor, CI'daki gibi: her yeni test
veritabanı onlarla doğuyor ve `CREATE EXTENSION` yetkisi gerekmiyor.

Sunucu yalnızca **127.0.0.1**'i dinliyor. Varsayılan olsaydı bu
veritabanı yerel ağa açılırdı ve parola zaten geliştirme parolası.

Silmek için tek komut — sistemde iz kalmıyor:

```bash
powershell -File scripts/yerel-postgres.ps1 -Action remove
```

---

## pgvector zorunlu

**Birkaç test meselesi değil: pgvector olmadan şema hiç kurulmuyor.**
İlk migration `CREATE EXTENSION vector` çalıştırıyor; eklenti yoksa tek
bir tablo bile oluşmuyor.

Bu, denemeden anlaşılmadı — ilk hâlde bu belge "yalnızca konu havuzu
testleri koşmaz" diyordu ve gerçek ancak `bmai db migrate` çalıştırınca
ortaya çıktı:

```
SqlState: 0A000
MessageText: extension "vector" is not available
```

pgvector derlemek **Windows SDK + MSVC** istiyor. Bu makinede ikisi de
yok (`C:\Program Files (x86)\Windows Kits` boş, `corecrt.h` hiçbir
yerde bulunamıyor), o yüzden betik uyarı verip duruyor — sunucu ayakta
kalıyor ama şema kurulamıyor.

Açmak için:

```bash
winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

Yönetici yetkisi ve ~5 GB yer istiyor. Kurulduktan sonra betiği tekrar
çalıştırmak yeterli; kalan her şey hazır bekliyor.

> **Prebuilt DLL indirilmiyor.** İnternette hazır `vector.dll`
> derlemeleri var ama bunlar veritabanı sürecine yüklenen yerel kod;
> kaynağı doğrulanmamış bir ikiliyi oraya koymak, kazandığı zamana
> değmez. Ya resmî kaynaktan derlenir ya da hiç.

---

## Şu an ne çalışıyor

| | Durum |
|---|---|
| PostgreSQL 16 sunucusu | ✔ çalışıyor, 127.0.0.1:5432 |
| `bmai` veritabanı, `pg_trgm`, `uuid-ossp` | ✔ hazır |
| pgvector | ✘ MSVC yok |
| Şema (migration) | ✘ pgvector'e bağlı |

Yani bu kurulum **yarım**: sunucu hazır, eksik olan tek şey eklenti.
Veritabanı gerektiren testler bu arada CI'da gerçek pgvector ile
koşmaya devam ediyor.

---

## Komutlar

| Komut | Ne yapar |
|---|---|
| `-Action setup` | İndir, kur, başlat (tekrar çalıştırılabilir) |
| `-Action start` | Sunucuyu başlat |
| `-Action stop` | Sunucuyu durdur |
| `-Action status` | Durum + pgvector var mı |
| `-Action remove` | Her şeyi sil |

---

## Docker açılmıyorsa: yetim soket dosyaları

**28 Ağu 2026 — çözüldü, ama tekrar edecek bir arıza.**

Docker Desktop açılışta şu hatayla çöküyordu ve arayüz yalnızca
"unexpected error" diyordu:

```
backend cancelling with error: starting services: initializing Ingest server:
listening on unix://.../Docker/run/sailor-ingest.sock:
remove .../sailor-ingest.sock: The file cannot be accessed by the system.
```

### Sebep

Sert çökmelerden (mavi ekran) sonra Docker'ın AF_UNIX soket dosyaları
diskte **yetim** kalıyor. Bunlar sıradan dosya değil, `ReparsePoint`:
arkalarındaki çekirdek nesnesi öldüğü için Windows onlara erişemiyor —
okunamıyor, **silinemiyor**. Docker açılışta üzerlerine yazmayı deniyor,
silemiyor ve vazgeçiyor.

`Remove-Item -Force` çalışmıyor: "Sistem dosyaya erişemiyor."

### Çözüm: dosyayı değil DİZİNİ yeniden adlandır

Dosyaya erişilemiyor ama **dizin girdisi** hâlâ değiştirilebiliyor:

```powershell
Get-Process -Name "*docker*","com.docker*" | Stop-Process -Force
Rename-Item "$env:LOCALAPPDATA\Docker\run" "run-bozuk"
New-Item -ItemType Directory "$env:LOCALAPPDATA\Docker\run"
```

**İKİ AYRI DİZİNE BAKIN.** İlk denemede yalnızca `Docker\run`
temizlendi ve Docker bu kez `docker-secrets-engine` dizinindeki başka
bir yetim sokette çöktü — aynı arıza, farklı yer:

- `%LOCALAPPDATA%\Docker\run`
- `%LOCALAPPDATA%\docker-secrets-engine`

Her ikisinde de `ReparsePoint` özniteliği taşıyan dosya varsa dizini
yeniden adlandırın. Docker açılışta yenisini oluşturuyor; bu dizinlerde
kullanıcı verisi yok, yalnızca çalışma zamanı soketleri.

Adlandırılan dizinler silinemiyor ama zararsız: içindeki dosyalar
erişilemez olduğu için yer kaplamıyorlar (0 bayt).

### Neden "sıfırlama" gerekmiyor

Docker'ın kendi hata ekranı "Reset to factory defaults" öneriyor. O
seçenek **bütün imajları ve volume'leri siliyor** — yani veritabanı
verisini de. Yetim soket sorununda gereksiz: sorun yapılandırmada değil,
dört tane erişilemez dosyada.
