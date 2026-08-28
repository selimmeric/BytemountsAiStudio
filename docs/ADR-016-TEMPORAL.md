# ADR-016 — Temporal'a geçilmiyor

**Tarih:** 29 Ağustos 2026 · **Durum:** Kabul edildi · **Madde:** P4-08

ADR-004 kendi ince DAG motorumuzu seçerken şunu yazmıştı:

> `IWorkflowEngine` arayüzü arkasında saklanır ki Faz 4'te Temporal'a
> geçiş bir implementasyon değişimi olsun.

Faz 4 geldi. Karar: **geçilmiyor.**

## Ölçüm

Geçiş gerekçesi ölçek olacaktı. Ölçüldü (P4-09, 29 Ağustos):

| | |
|---|---|
| Sürdürülen hız | **1.625 video/gün** |
| Düşen koşu | 0 |
| Ortalama retry turu | 0,00 |
| Donanım | tek makine, bir hafif + bir render worker |

ADR-004'te Temporal'ı gündeme getiren ölçek 1.000 video/gündü. Mevcut
motor bunu **tek makinede** aşıyor.

**Ve darboğaz orkestrasyon değil.** Video başına 45,6 saniye render
(ffmpeg), render eşzamanlılığı 1. Temporal iş akışlarını yönetiyor;
ffmpeg'i hızlandırmıyor. Bugün ölçülen kusurların ikisi de render
tarafındaydı — altyazı katmanlarının videonun tamamı boyunca
üretilmesi (280 sn → 44 sn) ve ffmpeg belleği (23,6 GB). Temporal
bunların hiçbirine dokunmazdı.

## Motorun bugün taşıdığı şeyler

Geçişin bedelini bunlar belirliyor. Hepsi ölçülmüş ve çalışıyor:

- **Hedefli yeniden koşma** (P2-07): QC düşen videoyu baştan değil
  hedeften koşturuyor; tur sayacı `node_executions` eşsizliğine bağlı
- **Onay kapısı** (P1-27): run park ediliyor ve *hiçbir worker kaynağı
  tüketmiyor* — bu, işin kirasını kapatmaktan geliyor
- **Kaynak hatası ≠ başarısızlık** (ADR-011): hız sınırı ve açık devre
  işi ileri tarihe atıyor, deneme sayacını artırmıyor
- **Maliyet defteri ve bütçe kapısı**: her node çalıştırması para
  kaydıyla birlikte yazılıyor
- **Panel**: koşular, zaman çizelgesi, loglar ve maliyet tek sorguyla
  okunuyor, çünkü hepsi bizim şemamızda

Temporal'da bunların hiçbiri hazır gelmiyor; hepsi workflow
fonksiyonlarının içine yeniden yazılırdı. Görünürlük ise Temporal'ın
kendi deposundan gelirdi — panelin bugün Postgres'ten yaptığı
sorgular yeniden yazılmak zorunda kalırdı.

## ADR-004'ün iddiası doğru değildi

"Arayüz arkasında saklanır" cümlesi ölçüldüğünde tutmadı:
`ApprovalService` ve `DeadLetterTriage` **somut** `WorkflowEngine`
sınıfına bağlıydı, çünkü kullandıkları `EnqueueAfterAsync` arayüzde
yoktu. Motoru değiştirmek o iki servisi de yeniden yazmak demekti.

Metot arayüze taşındı ve servisler arayüze bağlandı; artık iddia
doğru. Ama **geçiş kolaylaşmadı** ve bunu yazmak dürüstlük gereği:
imza `Run` ve `WorkflowGraph` taşıyor, ikisi de bizim modelimiz.
Temporal'da "şu node'dan sonrasını kuyruğa at" diye bir kavram yok —
devam kararı workflow fonksiyonunun içinde veriliyor. Arayüz gerçek
yüzeyi **gösteriyor**, küçültmüyor.

Bir test (`EngineSeamTests`) sızıntının geri gelmesini engelliyor. Bir
mimari kararın doğru **kalması**, yazılmasıyla değil sınanmasıyla
oluyor.

## Kararı değiştirecek koşullar

Bu bir "asla" değil. Şunlardan biri olursa yeniden bakılır:

1. **Günler süren iş akışları.** Bugün en uzun run on dakika. Bir
   video haftalarca sürecek bir onay/yayın zincirine girerse, dayanıklı
   zamanlayıcılar kendi kuyruğumuzda yazmaktan ucuza gelir.
2. **Koşan run'ların sürüm geçişi.** Bugün bir run başladığı grafla
   bitiyor (P3-06) ve bu bilinçli. Koşan bir run'ı yeni graf sürümüne
   taşımak gerekirse, Temporal'ın versiyonlama modeli gerçek bir
   kazanç.
3. **Orkestrasyonun darboğaz olması.** Bugün değil: render darboğaz.
   Kuyruk gecikmesi render süresine yaklaşırsa ölçüm tekrarlanır.
4. **Birden fazla ekip.** Tek bir motoru birden çok ekip beslerse,
   hazır bir platformun sözleşmesi kendi motorumuzun teamüllerinden
   ucuza gelir.

## Bu kararın maliyeti

Kendi motorumuzu sürdürmek demek: dayanıklılık, yeniden deneme,
zamanlayıcı ve görünürlük **bizim işimiz**. Bugüne kadar bu işin
bedeli ödendi ve çalışıyor — ama bedava değil. Bugün bulunan üç
ciddi hata (EF yürütme stratejisi çakışması, paralel dalların
birbirini silmesi, bölüm bakımı) tam da bu katmanın hataları.

Temporal o hataların bir kısmını hiç yaşatmazdı. Karşılığında
yukarıdaki beş özelliği yeniden yazmak gerekirdi. Ölçülen ölçek bu
takası bugün gereksiz kılıyor.
