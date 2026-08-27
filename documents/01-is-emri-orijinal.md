# İş Emri — Orijinal

**Tarih:** 27 Ağustos 2026
**Kaynak:** Proje sahibi
**Durum:** Birebir kopya — değiştirilmedi, kısaltılmadı

---

> Bu projeyi analiz ettirmek, mimariyi çıkarttırmak ve daha sonra adım adım geliştirmeye başlamak amacıyla düzenledim. Sadece "n8n benzeri sistem" demek yerine; AI araştırması, içerik üretimi, görsel, ses, video, kuyruklama, paralel işler ve YouTube yayınlama taraflarını ayrı ayrı tanımladım.

# AI Destekli Otomatik Video İçerik Üretim ve Yayınlama Platformu

## 1. Projenin Amacı

n8n benzeri, ancak özellikle AI destekli otomatik video içerik üretimi üzerine uzmanlaşmış bir otomasyon platformu geliştirmek istiyorum.

Sistemin temel amacı şu olmalı:

Ben sisteme yalnızca bir ana konu / kategori / içerik stratejisi vereceğim.

Örneğin:

"En iyi 10'lar"

Sistem bu ana konuyu analiz edecek, insanların ilgisini çekebilecek potansiyel video konularını kendi belirleyecek, konuları önceliklendirecek ve seçtiği konular üzerinde tamamen otomatik olarak araştırma → senaryo → görsel → ses → video → YouTube yayınlama sürecini gerçekleştirecek.

Sistem aynı anda birden fazla konu üzerinde çalışabilmeli.

Örneğin:

* En iyi 10'lar
* Tarihin en ilginç olayları
* Dünyadaki gizemler
* Teknoloji
* Bilim
* Yapay zeka
* Finans
* Tarih
* Uzay
* İlginç bilgiler

gibi farklı içerik kanalları veya içerik kategorileri sisteme tanımlanabilmeli.

Sistem bu konularda durmaksızın yeni içerik üretmeye devam edebilmeli.

## 2. Sistemin Genel Çalışma Mantığı

Sistemi klasik bir workflow otomasyonundan ziyade AI destekli bir içerik fabrikası olarak tasarlamak istiyorum.

Temel akış:

```text
ANA KONU
   ↓
AI KONU ÜRETİMİ
   ↓
KONU PUANLAMA / ÖNCELİKLENDİRME
   ↓
ARAŞTIRMA
   ↓
BİLGİ TOPLAMA
   ↓
DOĞRULAMA
   ↓
SENARYO OLUŞTURMA
   ↓
SAHNE / ZAMAN ÇİZELGESİ OLUŞTURMA
   ↓
GÖRSEL ARAŞTIRMA
   ↓
GEREKİRSE AI GÖRSEL ÜRETİMİ
   ↓
SES OLUŞTURMA
   ↓
ARKA PLAN MÜZİĞİ
   ↓
VIDEO JSON / TIMELINE
   ↓
VIDEO RENDER
   ↓
KALİTE KONTROL
   ↓
YOUTUBE UPLOAD
   ↓
SONUÇ / ANALİZ
   ↓
YENİ KONU
```

Bu yapı mümkün olduğunca modüler olmalı.

Bir aşamadaki servis daha sonra başka bir servisle değiştirilebilmeli.

Örneğin:

* OpenAI → başka bir LLM
* ElevenLabs → başka bir TTS
* Pexels → Pixabay
* Google → Bing
* bir AI image servisi → başka bir AI image servisi
* FFmpeg → başka bir video engine

gibi değişiklikler sistemi bozmamalı.

## 3. n8n Benzeri Workflow Motoru

Sistemin merkezinde n8n mantığına benzeyen bir workflow engine olmasını istiyorum.

Workflow içerisinde node'lar bulunmalı.

Örneğin:

```text
[Topic Input]
      ↓
[AI Topic Generator]
      ↓
[Research]
      ↓
[Fact Check]
      ↓
[Script Generator]
      ↓
[Scene Generator]
      ↓
[Image Search]
      ↓
[AI Image Generator]
      ↓
[TTS]
      ↓
[Timeline Generator]
      ↓
[Video Renderer]
      ↓
[YouTube Upload]
```

Ancak sistem sadece sabit bir workflow çalıştırmamalı.

Kullanıcı workflow'ları oluşturabilmeli, değiştirebilmeli ve farklı içerik kanallarına farklı workflow'lar atayabilmeli.

Örneğin:

**Shorts Workflow**

```text
Topic
→ Research
→ 30-60 saniye Script
→ 9:16 Scenes
→ TTS
→ Video
→ YouTube Shorts
```

**Long Video Workflow**

```text
Topic
→ Deep Research
→ 8-15 dakika Script
→ Scene Planning
→ Images
→ TTS
→ Background Music
→ Video
→ YouTube
```

## 4. Ana Konudan Otomatik Konu Üretme

Örneğin kullanıcı:

"En iyi 10'lar"

girerse sistem doğrudan tek bir video üretmemeli.

Önce AI'ye:

"Bu kategori altında insanların ilgisini çekebilecek, tıklanma potansiyeli yüksek, araştırılabilir ve içerik üretmeye uygun video konuları üret."

denmeli.

AI örneğin:

1. Dünyanın En İlginç 10 Kaybolmuş Şehri
2. Tarihin En Büyük 10 Gizemi
3. Dünyanın En Tehlikeli 10 Yeri
4. En Zengin 10 İmparatorluk
5. Dünyanın En Garip 10 Yasası
6. İnsanlığın Çözemediği 10 Gizem
7. En Pahalı 10 Madde
8. Dünyanın En İzole 10 Yeri
9. Tarihin En Büyük 10 Dolandırıcılığı
10. En İlginç 10 Bilimsel Keşif

gibi konular üretebilmeli.

Fakat sadece konu üretmesi yeterli değil.

Her konuya bir skor vermeli.

Örneğin:

```json
{
  "topic": "Dünyanın En Tehlikeli 10 Yeri",
  "interest_score": 92,
  "trend_score": 87,
  "researchability_score": 94,
  "visual_score": 91,
  "competition_score": 68,
  "evergreen_score": 89,
  "overall_score": 88
}
```

Böylece sistem önce en yüksek potansiyelli konuyu işleyebilmeli.

## 5. Konu Havuzu

Sistemde merkezi bir Topic Pool / Konu Havuzu bulunmasını istiyorum.

Her konu için durum tutulmalı:

```text
NEW
QUEUED
RESEARCHING
SCRIPTING
ASSET_SEARCH
VOICE_GENERATING
VIDEO_RENDERING
QUALITY_CHECK
UPLOADING
PUBLISHED
FAILED
RETRY
REJECTED
```

Aynı konu tekrar üretilmemeli.

Daha önce işlenmiş konular AI tarafından kontrol edilmeli.

Benzer konular da tespit edilmeli.

Örneğin:

"Dünyanın En Tehlikeli 10 Yeri"

daha önce üretildiyse:

"Dünyanın En Tehlikeli 10 Bölgesi"

gibi küçük değişikliklerle aynı içeriğin tekrar üretilmesi engellenmeli.

## 6. Araştırma Motoru

Konu seçildikten sonra AI internet üzerinde araştırma yapmalı.

Araştırma sadece tek bir kaynaktan yapılmamalı.

Birden fazla kaynak kullanılmalı.

Örneğin:

* Web siteleri
* Haber kaynakları
* Wikipedia
* Resmi kurumlar
* Akademik kaynaklar
* Araştırma makaleleri
* Güvenilir veri tabanları
* YouTube
* Reddit gibi kullanıcı toplulukları
* gerektiğinde sosyal medya

Araştırma sonuçları ham şekilde senaryoya aktarılmamalı.

Önce:

```text
Research
↓
Source Collection
↓
Fact Extraction
↓
Fact Verification
↓
Confidence Score
↓
Knowledge Base
```

oluşturulmalı.

Her bilgi için mümkünse kaynak tutulmalı:

```json
{
  "fact": "...",
  "source": "...",
  "source_type": "official",
  "confidence": 0.94,
  "retrieved_at": "...",
  "verified": true
}
```

Bu yapı özellikle yanlış bilgi üretimini azaltmak için önemli.

## 7. Senaryo Motoru

Araştırma tamamlandıktan sonra AI senaryo oluşturmalı.

Senaryo video tipine göre farklı olmalı.

Örneğin:

**Shorts**

30-60 saniye.

Daha hızlı giriş:

```text
HOOK
↓
INFORMATION
↓
CURIOSITY
↓
PAYOFF
↓
CTA
```

**Long Video**

Örneğin 10 dakika:

```text
HOOK
INTRO
SECTION 10
SECTION 9
SECTION 8
...
SECTION 1
CONCLUSION
CTA
```

AI'nin sadece metin üretmesini istemiyorum.

Senaryo aynı zamanda video üretim planı olmalı.

## 8. Script → Scene / Timeline Sistemi

Senaryo daha sonra sahnelere bölünmeli.

Örneğin:

```json
{
  "video_type": "short",
  "aspect_ratio": "9:16",
  "duration": 52,
  "scenes": [
    {
      "start": 0,
      "end": 4,
      "text": "...",
      "visual_prompt": "...",
      "visual_type": "image",
      "voice": "...",
      "transition": "fade"
    },
    {
      "start": 4,
      "end": 9,
      "text": "...",
      "visual_prompt": "...",
      "visual_type": "image"
    }
  ]
}
```

Bu JSON bizim geliştireceğimiz ayrı bir Video Rendering Engine tarafından kullanılacak.

Yani AI doğrudan video üretmeyecek.

AI:

"Video nasıl oluşturulmalı?"

sorusunun cevabını JSON olarak verecek.

Video engine ise:

"Bu JSON'a göre gerçek videoyu oluşturacak."

## 9. Görsel Sistemi

Her sahne için uygun görsel bulunmalı.

Öncelik sırası configurable olmalı.

Örneğin:

```text
1. Kullanılabilir ücretsiz stok görsel
2. Kullanılabilir ücretsiz video
3. Web'den uygun görsel
4. AI tarafından oluşturulan görsel
```

Kullanıcı isterse yalnızca belirli kaynakları aktif edebilmeli.

Örneğin:

```text
Pexels      ON
Pixabay     ON
Unsplash    ON
Web Search  ON
AI Image    ON
```

Görsellerin lisans / kullanım koşulları mümkün olduğunca kayıt altına alınmalı.

Her asset için:

```json
{
  "url": "...",
  "source": "pexels",
  "license": "...",
  "author": "...",
  "downloaded": true
}
```

gibi bilgiler tutulmalı.

## 10. AI Görsel Üretimi

Eğer uygun görsel bulunamazsa sistem AI görsel oluşturabilmeli.

Ancak bunu tek bir sağlayıcıya bağımlı yapmak istemiyorum.

Örneğin:

```text
AI Image Provider
├── OpenAI
├── Gemini
├── Flux
├── Stability
└── Custom API
```

gibi provider mimarisi olmalı.

Ayrıca API olmayan servislerde gerektiğinde browser automation kullanılabilmeli.

Örneğin:

```text
AI Image Web UI
↓
Login
↓
Prompt
↓
Generate
↓
Download
```

Ancak browser automation sistemin ana mimarisine gömülmemeli.

Plugin / provider mantığında olmalı.

## 11. Görsel Boyutlandırma

Video tipine göre görseller otomatik hazırlanmalı.

**YouTube Shorts**

```text
1080 × 1920
9:16
```

**Normal YouTube Video**

```text
1920 × 1080
16:9
```

Görsel yatay veya dikey olsa bile sistem:

* crop
* zoom
* pan
* Ken Burns effect
* blur background
* fit
* cover

gibi yöntemlerle uygun kadraja dönüştürebilmeli.

## 12. Seslendirme Sistemi

Senaryo TTS sistemine gönderilmeli.

Kullanıcı panelden ses ayarlarını belirleyebilmeli.

Örneğin:

```text
Gender:
Male / Female

Voice:
...

Speed:
0.8 - 1.3

Pitch:
...

Emotion:
Neutral / Serious / Emotional / Energetic

Language:
Turkish / English / German / ...
```

TTS provider'ları değiştirilebilir olmalı.

Örneğin:

```text
ElevenLabs
OpenAI
Google
Azure
Custom TTS
```

Sistem her sahnenin ses süresini bilmeli.

Örneğin:

```text
Scene 1
Text duration = 4.8 sec

Scene 2
Text duration = 7.2 sec
```

Böylece görseller ve ses senkronize edilebilmeli.

## 13. Arka Plan Müziği

Videoya arka plan müziği eklenebilmeli.

Kullanıcı:

```text
Music: ON/OFF
Volume: 0-100
Music Type:
  - Cinematic
  - Documentary
  - Suspense
  - Emotional
  - Energetic
  - Ambient
```

gibi seçenekleri belirleyebilmeli.

Konuşma sırasında müzik otomatik olarak kısılabilmeli.

Örneğin:

```text
Voice
100%

Background Music
8-15%
```

Ayrıca ducking uygulanabilmeli.

## 14. Video Rendering Engine

Bu sistemin ayrı bir yazılım olmasını istiyorum.

AI tarafından oluşturulan JSON'u alacak.

Örneğin:

```text
AI
 ↓
Video JSON
 ↓
Video Rendering Engine
 ↓
FFmpeg / MoviePy / Remotion / başka engine
 ↓
MP4
```

Video engine:

* görselleri yerleştirecek
* crop/zoom yapacak
* seslendirmeyi yerleştirecek
* background music ekleyecek
* subtitle ekleyebilecek
* transition uygulayacak
* intro/outro ekleyebilecek
* watermark/logo ekleyebilecek
* gerektiğinde text overlay oluşturacak
* final MP4 oluşturacak

## 15. Subtitle / Caption Sistemi

Seslendirmeden otomatik subtitle oluşturulabilmeli.

Örneğin:

```text
WORD / PHRASE TIMING
```

üzerinden:

* normal subtitle
* animated subtitle
* Shorts caption
* highlight word
* kinetic typography

gibi seçenekler desteklenebilmeli.

## 16. YouTube Upload Sistemi

Video tamamlandığında sistem YouTube'a otomatik yükleyebilmeli.

Upload sırasında:

```text
Title
Description
Tags
Category
Playlist
Thumbnail
Language
Visibility
Publish Date
```

gibi bilgiler AI tarafından oluşturulabilmeli.

Kullanıcı:

```text
Publish immediately
```

veya:

```text
Schedule
```

seçebilmeli.

## 17. Thumbnail Sistemi

Video için ayrıca thumbnail üretilebilmeli.

Thumbnail:

* AI tarafından oluşturulabilir
* mevcut görsellerden oluşturulabilir
* template kullanılabilir

AI thumbnail için:

```text
Title
Main Visual
Emotion
Composition
Text
Color
```

belirleyebilmeli.

## 18. Paralel Çalışma

Sistemin en önemli özelliklerinden biri aynı anda birden fazla içerik üzerinde çalışabilmesi.

Örneğin:

```text
Worker 1 → Konu A → Research
Worker 2 → Konu B → Script
Worker 3 → Konu C → Image Search
Worker 4 → Konu D → TTS
Worker 5 → Konu E → Rendering
```

olabilir.

Fakat sistem sınırsız API isteği göndererek servisleri aşırı yüklememeli.

Bu nedenle:

* Queue
* Worker
* Retry
* Rate Limit
* Concurrency Limit
* Priority
* Dead Letter Queue
* Job Status

mekanizmaları olmalı.

Örneğin:

```text
Redis
   ↓
Job Queue
   ↓
Workers
```

gibi bir yapı değerlendirilmeli.

## 19. Channel / Kanal Yönetimi

Tek bir YouTube kanalıyla sınırlı olmak istemiyorum.

Sistem ileride birden fazla kanal yönetebilmeli.

Örneğin:

```text
Channel A
├── History
├── 10-Lists
└── Mystery

Channel B
├── Technology
└── AI

Channel C
└── Finance
```

Her kanalın kendine ait:

* workflow
* AI prompt
* voice
* language
* visual style
* music
* intro
* outro
* YouTube hesabı
* publishing schedule

ayarları olabilmeli.

## 20. AI Agent Mimarisi

Sistemin tek bir AI promptundan oluşmasını istemiyorum.

Mümkün olduğunca uzmanlaşmış AI agent'lar kullanılmalı.

Örneğin:

```text
Topic Agent
Research Agent
Fact Checker Agent
Script Agent
Scene Agent
Visual Agent
Voice Agent
SEO Agent
Thumbnail Agent
Quality Control Agent
```

Her agent yalnızca kendi görevinden sorumlu olmalı.

Örneğin:

**Topic Agent** — İlgi çekici konu bulur.
**Research Agent** — İnternetten bilgi toplar.
**Fact Checker** — Bilgileri doğrular.
**Script Agent** — Senaryo oluşturur.
**Scene Agent** — Senaryoyu zaman ve sahnelere böler.
**Visual Agent** — Her sahne için uygun görsel belirler.
**SEO Agent** — YouTube başlığı, açıklaması, tag ve metadata üretir.
**Quality Control Agent** — Videoyu yayınlamadan önce kontrol eder.

## 21. Quality Control

Sistem video oluşturdum diye doğrudan yayınlamamalı.

Önce otomatik kalite kontrol yapılmalı.

Kontrol edilecekler:

```text
Script var mı?
Kaynaklar var mı?
Bilgi tutarlı mı?
Görseller mevcut mu?
Görseller sahneyle alakalı mı?
TTS başarılı mı?
Ses dosyaları tamam mı?
Ses süreleri doğru mu?
Video süresi doğru mu?
Subtitle senkronize mi?
Video çözünürlüğü doğru mu?
Audio clipping var mı?
Background music fazla mı?
YouTube metadata var mı?
Thumbnail var mı?
```

Sorun varsa ilgili node tekrar çalıştırılmalı.

## 22. İnsan Onayı

Sistemin tamamen otomatik çalışabilmesini istiyorum fakat kullanıcı isterse belirli aşamalarda insan onayı verebilmeli.

Örneğin:

```text
AUTO MODE

Konu
↓
Araştırma
↓
Senaryo
↓
Video
↓
Upload
```

ve:

```text
APPROVAL MODE

Konu
↓
[USER APPROVAL]
↓
Araştırma
↓
[USER APPROVAL]
↓
Senaryo
↓
[USER APPROVAL]
↓
Video
↓
[USER APPROVAL]
↓
Upload
```

gibi çalışma modları olmalı.

## 23. Dashboard

Sistem için web tabanlı bir yönetim paneli istiyorum.

Dashboard üzerinde:

```text
Active Jobs
Queued
Researching
Scripting
Generating Images
Generating Voice
Rendering
Completed
Failed
Uploaded
```

görülebilmeli.

Ayrıca:

```text
Today:
Videos Generated: 24
Videos Uploaded: 18
Failed: 2
Processing: 4
```

gibi istatistikler gösterilmeli.

## 24. Workflow Editor

n8n benzeri görsel workflow editorü istiyorum.

Node'lar birbirine bağlanabilmeli.

Örneğin:

```text
[Topic]
   ↓
[AI]
   ↓
[Research]
   ↓
[Fact Check]
   ↓
[Script]
   ↓
[Scene]
   ↓
[Image]
   ↓
[TTS]
   ↓
[Render]
   ↓
[YouTube]
```

Her node'un kendi ayarları olmalı.

Örneğin TTS node:

```text
Provider
Voice
Gender
Speed
Pitch
Emotion
Language
```

Research node:

```text
Search Engine
Max Sources
Allowed Domains
Research Depth
```

gibi.

## 25. Provider / Plugin Mimarisi

Sistemin gelecekte farklı servislerle genişletilebilmesi çok önemli.

Bu nedenle provider interface yaklaşımı kullanılmasını istiyorum.

Örneğin:

```text
ILLMProvider
ITTSProvider
IImageProvider
ISearchProvider
IVideoProvider
IStorageProvider
IYoutubeProvider
```

gibi abstraction'lar oluşturulmalı.

Böylece:

```text
OpenAIProvider
GeminiProvider
ClaudeProvider
```

veya:

```text
ElevenLabsProvider
OpenAITTSProvider
AzureTTSProvider
```

gibi implementasyonlar eklenebilmeli.

## 26. Veri Modeli

Sistem için uygun database yapısı tasarlanmalı.

Örneğin:

```text
Channels
Workflows
WorkflowNodes
Topics
Researches
Sources
Facts
Scripts
Scenes
Assets
Voices
AudioFiles
Videos
RenderJobs
YouTubeUploads
Jobs
Workers
Providers
Settings
Logs
```

gibi tablolar değerlendirilmeli.

Ancak bunların gerçekten gerekli olup olmadığını analiz et ve gereksiz tablolar oluşturmaktan kaçın.

## 27. Teknoloji Seçimi

Benim mevcut yazılım geliştirme tecrübemi de dikkate alarak uygun teknoloji stack'ini öner.

Özellikle şu konuları karşılaştır:

**Backend**

* C# / .NET
* Node.js
* Python

**Workflow Engine**

* Kendi workflow engine'imizi yazmak
* n8n'i kullanmak / fork etmek
* Temporal
* BullMQ
* Hangfire
* Quartz
* başka alternatifler

**Database**

* MSSQL
* PostgreSQL
* Redis

**Video**

* FFmpeg
* Remotion
* MoviePy
* başka alternatifler

**Frontend**

* React
* Vue
* mevcut kullandığım teknolojiler

Benim için en önemli kriterler:

1. Uzun vadede sürdürülebilirlik
2. Modülerlik
3. Provider değiştirilebilirliği
4. Paralel işlem
5. Hata yönetimi
6. Maliyet kontrolü
7. Kolay geliştirme
8. Büyük ölçeğe çıkabilme

## 28. Maliyet Yönetimi

Her işlem maliyetli olabilir.

Bu nedenle sistem her job için maliyet hesaplayabilmeli.

Örneğin:

```text
Research Cost
LLM Cost
Image Cost
TTS Cost
Rendering Cost
Storage Cost
```

ve:

```text
Total Cost
```

hesaplanmalı.

Bir konu için:

```text
Estimated Cost: $0.42
```

gibi tahmin gösterilebilmeli.

Ayrıca kullanıcı:

```text
Maximum cost per video
Maximum daily budget
Maximum monthly budget
```

belirleyebilmeli.

Limit aşılırsa sistem otomatik olarak durmalı.

## 29. Sonsuz İçerik Üretim Modu

Sistemin önemli özelliklerinden biri de:

"Bu kategori için sürekli içerik üret."

komutu verebilmek.

Örneğin:

```text
Category:
En İyi 10'lar

Mode:
Continuous

Daily Video:
20

Video Types:
70% Shorts
30% Long

Language:
Turkish
```

Sistem sürekli:

```text
Konu bul
↓
Konu puanla
↓
Araştır
↓
Senaryo
↓
Görsel
↓
Ses
↓
Video
↓
Kalite kontrol
↓
YouTube
↓
Yeni konu bul
```

döngüsünü sürdürebilmeli.

## 30. Öğrenen Sistem

Daha ileri aşamada YouTube'dan gelen sonuçları sisteme geri beslemek istiyorum.

Örneğin:

```text
Video
↓
YouTube
↓
Views
CTR
Watch Time
Retention
Likes
Comments
Subscribers
```

verileri tekrar AI sistemine gönderilebilir.

AI zaman içerisinde:

```text
Hangi konular daha fazla izleniyor?
Hangi başlıklar daha fazla tıklanıyor?
Hangi thumbnail'lar başarılı?
Hangi video süreleri daha iyi?
Hangi ses tonu daha iyi?
Hangi konular izleyiciyi videoda tutuyor?
```

gibi analizler yaparak yeni konu üretim stratejisini değiştirebilmeli.

Yani sistem zaman içerisinde:

```text
CONTENT GENERATION
        ↓
PUBLISH
        ↓
ANALYTICS
        ↓
LEARNING
        ↓
BETTER CONTENT
```

döngüsüne dönüşmeli.

## 31. Güvenlik ve Dayanıklılık

Sistem tamamen otomatik çalışacağı için aşağıdakiler mutlaka düşünülmeli:

* API key güvenliği
* Provider credentials
* OAuth token yönetimi
* YouTube OAuth
* Rate limits
* Retry
* Timeout
* Queue recovery
* Worker crash recovery
* Duplicate prevention
* Idempotency
* Logging
* Audit log
* Dosya depolama
* Backup
* Database backup
* Job recovery

Bir worker çöktüğünde bütün sistem durmamalı.

Job kaldığı yerden devam edebilmeli.

## 32. Nihai Hedef

Uzun vadede ortaya çıkmasını istediğim sistem şuna benzemeli:

```text
                    ┌──────────────────────┐
                    │     USER / ADMIN     │
                    └──────────┬───────────┘
                               │
                               ▼
                    ┌──────────────────────┐
                    │   WORKFLOW ENGINE    │
                    └──────────┬───────────┘
                               │
             ┌─────────────────┼─────────────────┐
             │                 │                 │
             ▼                 ▼                 ▼
       Topic Agent       Research Agent     Analytics
             │                 │                 │
             ▼                 ▼                 │
       Topic Pool         Knowledge Base         │
             │                 │                 │
             └────────┬────────┘                 │
                      ▼                          │
                Script Agent                     │
                      ▼                          │
                 Scene Agent                     │
                      ▼                          │
              Visual / Image Agent               │
                      ▼                          │
                  TTS Agent                      │
                      ▼                          │
                Video Engine                     │
                      ▼                          │
                Quality Control                  │
                      ▼                          │
                 YouTube                         │
                      │                          │
                      └──────────┬───────────────┘
                                 ▼
                              ANALYTICS
                                 │
                                 ▼
                            AI LEARNING
                                 │
                                 └──────► Topic Agent
```

## 33. Senden Beklediğim Çalışma

Bu projeyi hemen kodlamaya başlama.

Öncelikle kıdemli yazılım mimarı gibi projeyi analiz et.

Önce aşağıdaki konularda kapsamlı bir mimari çalışma yap:

**A. Gereksinim Analizi** — Benim anlattığım sistemde eksik bıraktığım noktaları tespit et.
**B. Mimari** — Sistemin genel mimarisini çıkar.
**C. Modüller** — Hangi ana modüllerin olması gerektiğini belirle.
**D. Workflow Engine** — Kendi workflow engine'imizi mi yazmalıyız, yoksa n8n / Temporal / Hangfire / BullMQ gibi mevcut çözümlerden biri mi kullanılmalı? Detaylı karşılaştır.
**E. AI Agent Architecture** — Agent'ların nasıl çalışması gerektiğini belirle.
**F. Queue / Worker Architecture** — Paralel çalışan job'ların nasıl yönetileceğini tasarla.
**G. Database** — Veri modelini oluştur.
**H. Provider Architecture** — LLM, TTS, Image, Search, Storage, Video ve YouTube provider sistemini tasarla.
**I. Video Rendering** — JSON → Timeline → Video mimarisini tasarla.
**J. Cost Control** — Maliyet kontrol sistemini tasarla.
**K. Quality Control** — AI tarafından üretilen içeriğin otomatik kontrol mekanizmasını tasarla.
**L. YouTube** — YouTube upload, scheduling ve analytics mimarisini değerlendir.
**M. Scalability** — 10 video/gün, 100 video/gün ve 1000 video/gün seviyelerinde sistemin nasıl değişeceğini değerlendir.
**N. MVP** — İlk versiyonda hangi özellikleri kesinlikle yapmamız gerektiğini belirle. Gereksiz özellikleri MVP'den çıkar.
**O. Roadmap** — Projeyi aşamalara böl:

```text
Phase 1
Phase 2
Phase 3
Phase 4
...
```

Her fazın amacını ve çıktısını belirle.

## 34. Özellikle Dikkat Etmeni İstediğim Nokta

Bu projeyi basit bir "AI ile video oluşturma uygulaması" olarak değerlendirme.

Ben aslında:

AI destekli, sürekli çalışan, paralel iş yapabilen, workflow tabanlı, provider bağımsız bir dijital içerik üretim platformu

oluşturmak istiyorum.

Dolayısıyla mimarinin ileride yeni AI servisleri, yeni içerik türleri, yeni sosyal medya platformları ve yeni workflow'lar eklenmesine izin vermesi gerekiyor.

Örneğin ileride:

```text
YouTube
TikTok
Instagram
Facebook
X
```

gibi platformlara da içerik gönderilebilmeli.

Aynı şekilde sadece video değil:

```text
Video
Short
Podcast
Blog
Article
Social Post
Thumbnail
```

gibi farklı içerik türleri de üretilebilmeli.

Bu nedenle sistemi baştan doğru abstraction'larla tasarla.

## 35. İlk Çıktı

İlk aşamada benden herhangi bir kod isteme.

Önce bana şu çıktıları ver:

1. Sistemin genel mimari diyagramı
2. Modül listesi
3. Workflow mimarisi
4. AI Agent mimarisi
5. Database mimarisi
6. Queue / Worker mimarisi
7. Provider mimarisi
8. Video JSON / Timeline yapısı
9. Teknoloji karşılaştırması
10. MVP kapsamı
11. Uzun vadeli roadmap
12. Riskler ve kritik mimari kararlar
13. Benim mevcut tanımımda eksik olan noktalar
14. Önerdiğin nihai sistem mimarisi

Sonrasında mimariyi birlikte onayladıktan sonra projeyi gerçek bir yazılım projesi olarak adım adım geliştirmeye başlayacağız.

Her geliştirme aşamasında:

* önce plan oluştur,
* yapılacak değişiklikleri tanımla,
* uygula,
* test et,
* hata/log kontrolü yap,
* değişiklikleri doğrula,
* ardından bir sonraki aşamaya geç.

Amaç hızlıca kod yazmak değil, uzun vadede büyüyebilecek doğru mimariyi kurmak.
