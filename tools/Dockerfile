# Araclar yan-servisi (P1-04).
#
# TABAN IMAJ playwright'in resmi imaji: chromium'un ihtiyac duydugu
# onlarca sistem kutuphanesi hazir geliyor. Duz python imajina bunlari
# elle kurmak, her taban imaj guncellemesinde tekrar kirilan bir
# apt-get listesi demekti.
FROM mcr.microsoft.com/playwright/python:v1.49.0-noble

WORKDIR /app

# Bagimliliklar KODDAN ONCE kopyalaniyor: kod her degistiginde
# yeniden pip install calismasin.
COPY pyproject.toml ./
COPY bmai_tools ./bmai_tools

# Hizalama (faster-whisper) ISTEGE BAGLI ve varsayilan imajda YOK:
# ~1 GB'lik bir bagimlilik agaci ve yalnizca ses hizalayan makinede
# gerekiyor. Acmak icin:
#   docker build --build-arg EXTRAS=fetch,align .
ARG EXTRAS=fetch

# ***PLAYWRIGHT SURUMU TABAN IMAJLA AYNI OLMAK ZORUNDA.***
#
# Taban imaj tarayicilari kendi surumune gore yerlestiriyor
# (`chromium-<derleme>`). `pyproject.toml` ise `playwright>=1.45`
# diyor ve pip EN YENIYI kuruyor -- olculdu (30 Agu 2026): duz
# `pip install` 1.62.0 getiriyor, imajdaki tarayicilar 1.49.0'a ait.
# Aranan dizin bulunamiyor ve /fetch "Executable doesn't exist" ile
# dusuyor. YANI IMAJIN BAS YETENEGI, IMAJ SAGLIKLI GORUNURKEN BOZUK.
#
# Alt sinir `pyproject.toml`'da kaliyor (yerel kurulumda kullanici
# kendi tarayicisini indiriyor); KAPTA surum sabitleniyor. Taban
# imaj etiketi degistirilirse bu da degismeli.
ARG PLAYWRIGHT_VERSION=1.49.0
RUN pip install --no-cache-dir ".[${EXTRAS}]" "playwright==${PLAYWRIGHT_VERSION}"

# ffmpeg hizalama icin gerekiyor (ses cozme). Taban imajda yok.
RUN apt-get update && apt-get install --no-install-recommends -y ffmpeg \
    && rm -rf /var/lib/apt/lists/*

# Kap ICINDE 0.0.0.0 dinleniyor; disariya acilmasi compose'daki port
# eslemesiyle kontrol ediliyor ve orada 127.0.0.1'e bagli.
EXPOSE 8099

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD python -c "import urllib.request;urllib.request.urlopen('http://localhost:8099/health').read()"

CMD ["python", "-m", "uvicorn", "bmai_tools.main:app", "--host", "0.0.0.0", "--port", "8099"]
