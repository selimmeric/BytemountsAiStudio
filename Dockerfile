# İçerik Fabrikası — API ve Worker imajı (P4-05).
#
# TEK DOSYA, İKİ SERVİS: `--build-arg PROJECT=...` ile hangi projenin
# çalıştırılacağı seçiliyor. İki ayrı Dockerfile, aynı taban katmanı
# (ffmpeg, yazı tipleri, ICU) iki kez tarif etmek ve birinde
# güncelleyip diğerinde unutmak demekti.

# ---- derleme ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG PROJECT=BytemountsAiStudio.Worker

WORKDIR /src

# BAĞIMLILIK DOSYALARI KODDAN ÖNCE: kod her değiştiğinde `restore`
# yeniden koşmasın. Katman önbelleği ancak bu sırayla işe yarıyor.
# `.editorconfig` DE GEREKİYOR ve unutmak sessiz değil, gürültülü bir
# hata veriyor: analiz kuralları orada bastırılıyor ve uyarılar hata
# sayıldığı için kap içindeki derleme, yerelde geçen kodu reddediyor.
# İki derlemenin farklı kurallarla koşması tam olarak bu depoda
# tekrar eden hata sınıfı.
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props BytemountsAiStudio.slnx ./
COPY src/ ./src/

# İSTEM DOSYALARI DA GEREKİYOR ve unutmak sessizdi: `Contracts`
# projesi `../../prompts/**` globuyla istemleri derlemeye gömüyor.
# Dizin kopyalanmadığında glob hiçbir şey bulmuyor, MSBuild tek bir
# uyarı bile vermiyor, imaj başarıyla derleniyor ve SAĞLIKLI
# başlıyor — ama hiçbir video üretemiyor. İlk run'da `prompts.empty`
# olarak görüldü. Artık boş glob derlemeyi düşürüyor.
COPY prompts/ ./prompts/

RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"

RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" \
    -c Release -o /app --no-restore

# ---- çalıştırma ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# FFMPEG ZORUNLU, İSTEĞE BAĞLI DEĞİL. Render onsuz hiç çalışmıyor ve
# eksikliği ancak ilk video üretilmeye çalışıldığında — yani dağıtımdan
# sonra — ortaya çıkardı.
#
# YAZI TİPLERİ DE ZORUNLU ve bu daha sessiz bir tuzak: altyazı
# `drawtext` ile çiziliyor ve boş bir Linux imajında hiç yazı tipi
# yok. Video ÜRETİLİR, sadece yazısı olmaz ya da tofu kutuları çıkar —
# QC bunu yakalamıyor, çünkü süre, çözünürlük ve ses doğru.
#
#   dejavu   : Latin, geniş kapsama, `drawtext` varsayılanı
#   liberation: Arial/Georgia/Verdana metrik karşılıkları — kanal
#               ayarlarında bu adlar geçiyor
#   noto-core: Arapça dahil (P3-09 ile eklenen üçüncü dil)
#
# CURL SAĞLIK KONTROLÜ İÇİN. `aspnet` imajında ne curl ne wget var ve
# bu, API'nin sağlık kontrolünü SESSİZCE İMKÂNSIZ kılıyordu: `wget`
# yazan ilk sürüm her kontrolde "not found" ile düşüyor, kap
# `unhealthy` oluyor ve `restart: unless-stopped` onu sonsuza kadar
# yeniden başlatıyordu — API'nin kendisi gayet sağlıklıyken. Kabı
# çalıştırınca görüldü.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg \
      curl \
      fonts-dejavu-core \
      fonts-liberation \
      fonts-noto-core \
 && rm -rf /var/lib/apt/lists/* \
 && fc-cache -f

# KÜLTÜR DUYARLILIĞI AÇIK KALMALI.
#
# `InvariantGlobalization=true` imajı küçültüyor ama Türkçe i/I
# dönüşümünü bozuyor: "İSTANBUL".ToLower() invariant kültürde "i̇stanbul"
# veriyor ve tekillik kontrolü sessizce yanlış çalışıyor (P3-09).
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# KÖK OLMAYAN KULLANICI: kap kaçışı durumunda kök yetkisi vermemek
# için. Yazılan tek yerler `storage` ve `output`, ikisi de bu
# kullanıcının.
RUN useradd --create-home --uid 10001 fabrika \
 && mkdir -p /veri/storage /veri/output /veri/durum \
 && chown -R fabrika:fabrika /veri

WORKDIR /app
COPY --from=build --chown=fabrika:fabrika /app ./

ENV BMAI_STORAGE=/veri/storage \
    BMAI_OUTPUT=/veri/output \
    BMAI_HEARTBEAT=/veri/durum/worker.json

USER fabrika

ARG PROJECT=BytemountsAiStudio.Worker
ENV BMAI_ENTRY="${PROJECT}.dll"

# `sh -c` gerekiyor çünkü hangi dll'in çalışacağı derleme argümanından
# geliyor; exec biçimi değişken genişletmiyor.
ENTRYPOINT ["sh", "-c", "exec dotnet /app/$BMAI_ENTRY"]
