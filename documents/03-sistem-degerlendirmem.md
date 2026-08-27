# Sistem Hakkında Değerlendirmem

**Yazan:** Claude (mimari analiz oturumu)
**Tarih:** 27 Ağustos 2026
**Niteliği:** Teknik görüş ve itirazlar. Tasarımın kendisi `docs/Icerik-Fabrikasi-Mimarisi.md`'de; burada tasarımın *arkasındaki yargılar* var.

Bu dosya kasten açık sözlü yazıldı. Bir mimari dokümanı "ne yapacağız"ı anlatır; bu dosya "neden endişeliyim" ve "nerede yanılıyor olabiliriz"i anlatır. İkisi ayrı tutulmalı, çünkü karar verirken ikincisine ihtiyaç duyulur.

---

## 1. İş emrinin kalitesi hakkında

Açık konuşayım: bu iş emri, gördüğüm benzer taleplerin çok üstünde. Sebebi şu üç şey:

1. **Problemi doğru yerden tanımlamışsınız.** "AI ile video yapan uygulama" değil, "provider bağımsız içerik üretim platformu" demişsiniz. Bu ikisi arasında mimari olarak beş yıllık fark var. §34'te bunu ayrıca vurgulamanız, soyutlamaların nereye kadar gitmesi gerektiğini baştan belirledi.
2. **Kendi tanımınıza güvenmemişsiniz.** §26'da "bu tabloları listeledim ama gerçekten gerekli mi analiz et" demişsiniz; §33/A'da "eksik bıraktığım noktaları tespit et" demişsiniz. Bu, çoğu teknik brief'te olmayan bir olgunluk. Nitekim 28 maddelik bir eksik listesi çıktı ve bunların birkaçı projenin şeklini değiştirdi.
3. **Kod istememişsiniz.** "Önce mimari, sonra kod" disiplinine uymak, bu ölçekte bir sistemde iki-üç aylık yeniden yazma tasarrufudur.

Buna karşılık iş emrinin **fazla iyimser** olduğu tek alan ölçek: "günde 20 video", "1000 video/gün" gibi hedefler teknik olarak konuşulabilir ama platform tarafındaki sert duvarlar (§2) hesaba katılmamış.

---

## 2. Bu projenin kaderini belirleyecek üç şey

Yüzlerce teknik detay var ama sonucu bunlar belirleyecek:

### 2.1 YouTube kotası ve politikası (teknik değil, platform riski)

Bu projede en çok bunu düşündüm. Durum şu:

- **Kota:** Google Cloud projesi başına günde 10.000 birim, upload 1.600 birim → **günde ~6 video**. Bu bir ayar değil, bir duvar. Kota artırımı için audit süreci var; onay garantili değil ve haftalar sürüyor.
- **Politika:** YouTube'un 2025'te netleştirdiği "inauthentic / mass-produced content" kuralı tam olarak "günde 20 AI videosu üreten kanal" profilini tarif ediyor. Sistem teknik olarak mükemmel çalışsa bile kanal demonetize olabilir ya da kapanabilir.

**Görüşüm:** Bu projeyi "günde 20 video" hedefiyle kurmak yerine **"günde 3-5 video, ama ayırt edilebilir kalitede"** hedefiyle kurmak hem kota duvarına çarpmaz hem politika riskini düşürür hem de aynı mimariyi gerektirir. Mimari değişmiyor — hedef değişiyor. Ölçeği sonra açmak, kapanmış bir kanalı geri almaktan kolaydır.

Bu bir itiraz, karar değil. Karar sizin; sistem her iki hedefi de kaldırabilecek şekilde tasarlandı.

### 2.2 İçeriğin doğruluğu

Otomatik içerik üretiminde en sinsi hata bu. Bir LLM "Dünyanın en tehlikeli 10 yeri" listesini akıcı, ikna edici ve **kısmen uydurma** olarak yazabilir. İzleyici fark etmez, siz fark etmezsiniz, sonra bir gün biri yorumlarda fark eder.

Bu yüzden mimaride tek bir kural bloklayıcı yapıldı: **kaynağı olmayan iddia senaryoya giremez.** Fact-checker'a "bu doğru mu" diye sormuyoruz — bu soruyu bir LLM güvenilir cevaplayamaz. "Bu alıntı bu iddiayı destekliyor mu" diye soruyoruz; bu cevaplanabilir bir sorudur.

**Endişem:** Bu kural maliyeti ve karmaşıklığı artırıyor, ve ilk sıkışmada "şimdilik kapatalım" demek çok cazip olacak. Kapatılırsa proje ilk altı ayda sorunsuz görünür, sonra itibar sorunu olarak geri döner. Bunu bilerek bloklayıcı QC kuralı yaptım.

### 2.3 Maliyetin sessizce büyümesi

Otonom sistemlerde para, insan bakmadığı zaman harcanır. Retry döngüleri, QC'nin tetiklediği yeniden üretimler, başarısız run'lar — bunların hepsi ödenmiş ama çıktısı olmayan işlerdir.

Mimarideki üç mekanizma bunun içindir: **idempotency** (retry ikinci kez ödeme yaptırmaz), **`retry_target`** (QC tüm pipeline'ı değil yalnız bozuk node'u tekrarlatır), **bütçe kapıları + kill-switch**.

Bunların üçü de MVP'de. Sonraya bırakılırsa "neden bu ay 400 dolar gitti" sorusunu cevaplayacak veri olmaz.

---

## 3. İtirazlarım ve tavsiyelerim

### 3.1 "Günde 20 video" hedefine itiraz

Yukarıda (§2.1) açıkladım. Özet: aynı mimari, daha düşük hedef, çok daha düşük risk.

### 3.2 Görsel workflow editörüne MVP'de itiraz

İş emri §24'te n8n benzeri görsel editör isteniyor. MVP'den çıkardım, Faz 3'e koydum. Sebep: editör, workflow *tanımının* oturmasından sonra yapılmalı. Şu an node tiplerinin ne olacağı, hangi ayarları alacağı, hangi çıktıları üreteceği henüz gerçek kullanımla sınanmadı. Editörü önce yaparsanız, sonra node modelini değiştirdiğinizde editörü de baştan yazarsınız.

JSON ile başlayıp editörü sonra eklemek tersine göre çok daha ucuz.

### 3.3 Browser automation ile AI görsel üretimine itiraz

İş emri §10'da "API olmayan servislerde browser automation kullanılabilmeli" deniyor. Bunu yapmamayı öneriyorum ve gerekçem pratik: bu yaklaşım kırılgandır (her arayüz değişiminde bozulur), bakımı sürekli iş çıkarır, ilgili servislerin kullanım şartlarına aykırıdır ve hesap kapanmasına yol açabilir. Kazancı ise sınırlı — ücretsiz/ucuz görsel üretimi için meşru API'ler zaten var.

Provider mimarisinde yeri açık bırakıldı; ileride gerçekten gerekirse eklenebilir. Ama MVP'de yazılmaması gereken kod bu.

### 3.4 "Öğrenen sistem" beklentisi hakkında uyarı

İş emri §30 çok makul görünüyor ama içinde gizli bir tuzak var: YouTube metriklerindeki farkın nedenini belirlemek istatistiksel bir problemdir, AI problemi değil. Bir video 10 bin, diğeri 800 izlenme aldıysa sebep konu mu, başlık mı, thumbnail mı, yayın saati mi, yoksa algoritmanın o gün nasıl davrandığı mı — ayırt edemezsiniz.

Ayırt etmeden yapılan "öğrenme" batıl inanç üretir ve sistemi yanlış yöne sürükler. Bu yüzden Faz 5'e **deney çerçevesi** (tek değişkenli varyantlar, minimum örneklem) koydum ve otomatik strateji değişimini o faz sonuna kadar kapalı tuttum.

Ayrıca YouTube Analytics verisi 24–72 saat gecikmeli; bu döngü doğası gereği yavaştır. "AI öğrenip kendini geliştirsin" beklentisi haftalar değil aylar ölçeğindedir.

### 3.5 Kendi workflow engine'imiz konusunda dikkat

Bu kararı ben önerdim (ADR-004) ve arkasındayım — ama en büyük kapsam kayması riski burada (R7). Kendi engine'ini yazan projelerin çoğu, farkında olmadan bir programlama dili yazmaya başlar: koşullar, döngüler, değişkenler, ifadeler, derken bir yorumlayıcı.

Bunu önlemek için tek bir kural koydum: **node = kuyruğa atılan iş, iş mantığı domain servisinde, ifade dili kasten kısıtlı.** Bu kural gevşetilirse engine büyür ve proje asıl işini yapmayı bırakır. İleride "şu node'un içinde küçük bir script çalıştırsak" teklifi geldiğinde bu paragrafı hatırlatın.

### 3.6 İki dille MVP kararı — katılmadığım ama doğru bulduğum yer

Mimari sunumunda "TR+EN paralel MVP'yi büyütür, Faz 3'e bırakalım" demiştim; karar aksi yönde çıktı. Şimdi düşününce **kararın daha iyi olduğunu** kabul ediyorum: dil soyutlaması, tek dille çalışan bir sistemde asla sınanmaz. İkinci dil sonradan eklendiğinde ortaya çıkan iş, baştan eklendiğinde çıkacak işin birkaç katıdır. İtirazım kapsam kaygısıydı; kazanç ondan büyük.

Bu, iş emrindeki kararların benim ilk önerimden iyi çıktığı ikinci örnek (birincisi render motorunu sıfırdan yazma kararı — o karar tek dilli mimariye giden yolu açtı).

---

## 4. Zor sanılan ama zor olmayanlar / kolay sanılan ama zor olanlar

| Konu | İlk izlenim | Gerçek |
|---|---|---|
| Video render | Zor | **Orta.** Timeline tam çözümlenmişse FFmpeg mekanik bir iştir |
| Workflow engine | Zor | **Orta.** Kapsam dar tutulursa 3-5 bin satır |
| Ses–görsel senkronu | Kolay | **Zor.** TTS süresi öngörülemez; tüm akış sırası bunun yüzünden değişti |
| Konu tekilliği | Kolay | **Zor.** "Aynı konu" tanımı bulanık; embedding eşiği ampirik ayarlanacak |
| Görsel bulma | Kolay | **Zor.** Stok arama soyut konularda alakasız sonuç verir; asıl kalite darboğazı burası |
| YouTube upload | Orta | **Kolay** (kod), **zor** (kota ve politika) |
| Maliyet takibi | Detay | **Kritik.** Sonradan eklenemeyen tek şey |
| Çok dillilik | Ek özellik | **Kesişen tasarım.** Sonradan eklemek en pahalı dönüşüm |

**Öngörüm:** Bu projede en çok vakit **görsel kalitesinde** ve **konu tekilliğinde** geçecek. Render ve engine tahmin edilebilir mühendislik; asıl belirsizlik "üretilen video izlenmeye değer mi" sorusunda ve bu soruyu kod cevaplamıyor.

---

## 5. Mimarideki en kritik üç karar

Geri dönüşü en pahalı olanlar, sırayla:

1. **PostgreSQL + pgvector (ADR-003).** Sonradan değişmez. pgvector olmadan konu tekilliği düzgün çözülemez.
2. **Timeline'ın tamamen çözümlenmiş belge olması (ADR-007).** Render'ın ağa çıkmaması; tekrarlanabilirlik, önbellek ve test bu karara dayanıyor. Bir kez "render sırasında şu görseli indiriversin" denirse üçü birden gider.
3. **Filter Graph IR (ADR-005r).** String birleştirmeye geri dönmek, test edilebilirliği tamamen kaybetmek demek. Studio'da bunun bedelinin ne olduğu görülüyor.

---

## 6. Dürüst risk değerlendirmesi

**Teknik olarak bu sistem kurulabilir mi?** Evet. Belirsizlik düşük; parçaların hepsi bilinen mühendislik.

**Planlanan sürede biter mi?** Faz 0–2 (MVP) için tahminim 3–4 ay, tek geliştirici yarı zamanlı çalışıyorsa. Plan puanlarına göre 239 puan; deneyimle kalibre edilecek.

**Ticari olarak işe yarar mı?** Burası belirsiz ve teknik değil. İki soru belirleyecek:
- Üretilen video, insanın izlemeye değer bulduğu kalitede mi? (Görsel seçimi ve senaryo kalitesi — §4'teki asıl darboğaz)
- YouTube bu içeriği dağıtır ve gelirlendirir mi? (Politika riski — §2.1)

İkisi de kodla çözülmüyor. Sistemin değeri, bu iki soruyu **ucuza ve hızlı deneyebilmenizde**. Mimari bunun için kuruldu: deneme maliyetini düşürmek, sonucu ölçmek, hızlı yön değiştirmek.

**En büyük tek risk:** sistemin çalışması ama üretilen içeriğin ilgi görmemesi. Buna karşı savunma, mimaride değil, MVP'nin erken gerçek video yayınlamasında. Bu yüzden Faz 1'in kilometre taşı "sistem çalışıyor" değil, **"her iki dilde birer gerçek video yayında ve maliyeti ölçülmüş"** olarak yazıldı.

---

## 7. Kendi projem olsaydı ne yapardım

Aynı mimariyi kurardım, iki farkla:

1. **Faz 1'i ikiye bölerdim.** Önce *tek dilde, tek kanalda, elle tetiklenen, 5 video*. Onları izlerdim. Sonra otomasyonu açardım. Sebep: 5 videoyu izlemek, 50 sayfa QC kuralından fazla şey öğretir.
2. **İlk günden "insan editör modu" koyardım.** Sistem videoyu hazırlar, ben 2 dakikada bakar onaylarım. Tam otonomi bir hedef olsun ama varsayılan olmasın. Tam otonom sistem, hata yaptığında bunu 40 video sonra fark ettirir.

Bunların ikisi de plana zaten yakın (selective approval, Faz 1 kilometre taşı) — ama vurguyu daha da o yöne kaydırırdım.

---

## 8. Bu dosyanın güncellenmesi

Yeni bir karar alındığında veya bir öngörüm yanlış çıktığında **buraya yazılacak** — özellikle yanlış çıkanlar. "Şunu zor sanmıştım, kolaymış" kaydı, sonraki tahminleri kalibre eden tek şeydir.
