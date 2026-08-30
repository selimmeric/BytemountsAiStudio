"""Araclar yan-servisi (P1-04).

Uc uc nokta, ucu de DURUMSUZ: iki ardisik cagri arasinda hicbir sey
hatirlanmiyor. Bu bilincli - servis her an yeniden baslatilabilsin ve
birden fazla ornegi yan yana kosabilsin. Durum veritabaninda, burada
degil.

Yeteneklerin hangisinin acik oldugu /health'te. Kapali bir yetenek
CAGRIDA da acikca hata donuyor: sessizce daha kotu bir yola dusmek,
bu depoda gercek videolarin altyazisiz cikmasina yol acti.
"""

from __future__ import annotations

import logging
import os
from typing import Any

import httpx
from fastapi import FastAPI, HTTPException

from . import __version__, align, capabilities, extract, fetch, search, tts
from .config import SETTINGS, Settings
from .models import (
    AlignRequest,
    AlignResponse,
    FetchRequest,
    FetchResponse,
    HealthResponse,
    SearchRequest,
    SearchResponse,
    TtsRequest,
    TtsResponse,
)

logger = logging.getLogger("bmai.tools")

app = FastAPI(title="BytemountsAiStudio Tools", version=__version__)


def settings() -> Settings:
    return SETTINGS


@app.get("/health", response_model=HealthResponse)
def health() -> HealthResponse:
    """Ayakta miyim VE neyi yapabiliyorum.

    Ikincisi olmadan birincisi yaniltici: playwright kurulu olmayan bir
    servis de "saglikli" gorunur ve /fetch cagrisi ancak calisma
    aninda patlar.
    """
    config = settings()

    return HealthResponse(
        status="ok",
        version=__version__,
        capabilities=[
            capabilities.search_capability(config.searxng_url),
            # DERIN: /health'in isi zaten dogruyu soylemek. Yuzeysel
            # kontrol "acik" derken cagri "Executable doesn't exist"
            # ile dusuyordu.
            capabilities.fetch_capability(deep=True),
            capabilities.align_capability(config.align_model, config.align_device),
            capabilities.tts_capability(config.piper_voices_dir),
            capabilities.ffmpeg_capability(),
        ],
    )


@app.post("/search", response_model=SearchResponse)
async def search_endpoint(request: SearchRequest) -> SearchResponse:
    config = settings()

    params: dict[str, Any] = {
        "q": request.query,
        "format": "json",
        "language": request.language.split("-")[0],
    }

    try:
        async with httpx.AsyncClient(timeout=20.0) as client:
            response = await client.get(config.searxng_url.rstrip("/") + "/search", params=params)
    except httpx.HTTPError as error:
        raise HTTPException(status_code=502, detail=f"SearXNG'e ulasilamadi: {error}") from error

    # SearXNG varsayilan olarak JSON'u KAPALI tutuyor ve 403 donuyor.
    # Hata mesaji dogrudan cozumu soyluyor; "403" tek basina saatler
    # aldiran bir teshis.
    if response.status_code == 403:
        raise HTTPException(
            status_code=502,
            detail="SearXNG JSON ciktisini reddetti (403). settings.yml icindeki "
            "search.formats listesine 'json' eklenmeli.",
        )

    if response.status_code >= 400:
        raise HTTPException(status_code=502, detail=f"SearXNG {response.status_code} dondu")

    try:
        payload = response.json()
    except ValueError as error:
        raise HTTPException(status_code=502, detail=f"SearXNG yaniti JSON degil: {error}") from error

    return search.parse_results(payload, request.max_results, request.query)


@app.post("/fetch", response_model=FetchResponse)
async def fetch_endpoint(request: FetchRequest) -> FetchResponse:
    config = settings()

    capability = capabilities.fetch_capability()

    if not capability.available:
        raise HTTPException(status_code=503, detail=capability.detail)

    # KAPILAR, P1-06 ILE AYNI SIRADA. Ayri bir surecte kosuyor olmak
    # muafiyet degil.
    gate = fetch.scheme_gate(request.url, config.fetch_blocked_schemes)

    if not gate.allowed:
        raise HTTPException(status_code=400, detail=gate.reason)

    robots_text = await robots_for(request.url, config)
    gate = fetch.robots_gate(robots_text, request.url, config.fetch_user_agent)

    if not gate.allowed:
        # 403: KALICI hata. Yeniden denemek robots.txt'yi degistirmez,
        # o yuzden .NET tarafi bunu tekrar denemiyor.
        raise HTTPException(status_code=403, detail=gate.reason)

    return await render_page(request, config)


@app.post("/align", response_model=AlignResponse)
async def align_endpoint(request: AlignRequest) -> AlignResponse:
    config = settings()

    capability = capabilities.align_capability(config.align_model, config.align_device)

    if not capability.available:
        raise HTTPException(status_code=503, detail=capability.detail)

    if not os.path.isfile(request.audio_path):
        raise HTTPException(status_code=400, detail=f"ses dosyasi yok: {request.audio_path}")

    return await transcribe(request, config)


@app.post("/tts", response_model=TtsResponse)
async def tts_endpoint(request: TtsRequest) -> TtsResponse:
    config = settings()

    capability = capabilities.tts_capability(config.piper_voices_dir)

    if not capability.available:
        raise HTTPException(status_code=503, detail=capability.detail)

    directory = tts.voices_dir(config.piper_voices_dir)
    voices = tts.installed_voices(directory)
    voice = tts.voice_for(request.language, voices, request.voice or config.piper_voice_default or None)

    if voice is None:
        # DIL ICIN SES YOKSA URETIM YAPILMIYOR.
        #
        # Baska bir dilin sesine dusmek, Ingilizce metni Turkce sesle
        # okutmak demekti - hicbir yerde gorunmeyen bir kusur. 503:
        # eksik olan sey bir indirme ve yapilinca ayni is calisacak.
        raise HTTPException(
            status_code=503,
            detail=f"'{request.language}' icin ses yok (kurulu: {', '.join(voices) or 'hicbiri'}). "
            f"python -m piper.download_voices <ses> --data-dir {directory}",
        )

    import asyncio
    import base64

    # Sentez BLOKLAYICI: dogrudan cagirmak tum olay dongusunu durdurur
    # ve saglik kontrolu bile cevap veremezdi.
    data, rate, duration_ms = await asyncio.to_thread(
        tts.synthesize, directory, voice, request.text, request.speed
    )

    return TtsResponse(
        audio_base64=base64.b64encode(data).decode("ascii"),
        sample_rate=rate,
        duration_ms=duration_ms,
        voice=voice,
    )


async def robots_for(url: str, config: Settings) -> str | None:
    """robots.txt'yi ceker. Cekilemezse None - "okuyamadim" izin degil."""
    try:
        async with httpx.AsyncClient(
            timeout=10.0,
            follow_redirects=True,
            headers={"User-Agent": config.fetch_user_agent},
        ) as client:
            response = await client.get(fetch.robots_url(url))
    except httpx.HTTPError:
        return None

    # 404 = robots.txt YOK = kural yok = serbest. 5xx ise sunucu
    # sorunu: o zaman cekmiyoruz.
    if response.status_code == 404:
        return ""

    if response.status_code >= 400:
        return None

    return response.text


def _browser_missing(error: Exception) -> bool:
    """Hata tarayicinin YOKLUGUNDAN mi geliyor.

    Metne bakiliyor cunku playwright bu durum icin ayri bir istisna
    tipi vermiyor - hepsi `Error`. Iki kalip da araniyor: ingilizce
    mesaj surumler arasinda degisebiliyor ama ikisinden biri
    duruyor.

    ***SURUM UYUSMAZLIGI DA BURAYA DUSUYOR.*** Imajdaki tarayicilar
    bir playwright surumune ait; pip daha yeni bir surum kurarsa
    aranan dizin adi (`chromium-<derleme>`) tutmuyor ve hata yine
    "Executable doesn't exist" oluyor. Sinif ayni: eksik kurulum.
    """
    text = str(error).lower()

    return "executable doesn" in text or "playwright install" in text


async def render_page(request: FetchRequest, config: Settings) -> FetchResponse:
    """Sayfayi tarayiciyla acar.

    Playwright'in senkron API'si asyncio dongusunde CALISMIYOR; async
    API kullaniliyor.
    """
    from playwright.async_api import Error as PlaywrightError
    from playwright.async_api import async_playwright

    timeout_ms = config.fetch_timeout_seconds * 1000

    def block_heavy(route: Any) -> Any:
        # Gorseller, ses/video ve yazi tipleri ENGELLI: metin
        # cikariyoruz, bir sayfanin 3 MB'lik kahraman gorselini
        # indirmek yalnizca sure ve bant genisligi.
        if route.request.resource_type in {"image", "media", "font"}:
            return route.abort()

        return route.continue_()

    try:
        async with async_playwright() as playwright:
            browser = await playwright.chromium.launch(headless=True)

            try:
                context = await browser.new_context(user_agent=config.fetch_user_agent)
                page = await context.new_page()
                await page.route("**/*", block_heavy)

                response = await page.goto(request.url, timeout=timeout_ms, wait_until="domcontentloaded")

                if request.wait_for_selector:
                    await page.wait_for_selector(request.wait_for_selector, timeout=timeout_ms)

                html = await page.content()
                final_url = page.url
                title = await page.title()
                status = response.status if response is not None else 200
            finally:
                await browser.close()
    except PlaywrightError as error:
        # ***TARAYICI EKSIKLIGI SAYFA HATASI DEGIL.***
        #
        # `pip install playwright` tarayiciyi GETIRMIYOR; ayri bir
        # indirme gerekiyor ve en sik atlanan adim bu. Playwright bu
        # durumda "Executable doesn't exist" diyor ve eski kod onu
        # 502 ile "sayfa acilamadi" diye bildiriyordu.
        #
        # Iki ayri zarar:
        #   - .NET tarafi 502'yi GECICI okuyor (ADR-011): kuyruk,
        #     insan mudahalesi olmadan asla duzelmeyecek bir isi
        #     tekrar tekrar deniyordu.
        #   - Mesaj URL'yi isaret ediyordu. Teshis eden kisi siteye,
        #     robots.txt'ye, aga bakar - kurulumu asla akla getirmez.
        #
        # 503 KAYNAK: is basarisiz degil, ERTELENMIS. Biri tarayiciyi
        # kurdugunda ayni is calisacak.
        if _browser_missing(error):
            raise HTTPException(
                status_code=503,
                detail=f"chromium indirilmemis: python -m playwright install chromium ({error})",
            ) from error

        raise HTTPException(status_code=502, detail=f"sayfa acilamadi: {error}") from error

    if status >= 400:
        raise HTTPException(status_code=502, detail=f"sayfa {status} dondu")

    text, truncated = extract.extract_text(html)

    return FetchResponse(
        url=request.url,
        final_url=final_url,
        title=title or extract.extract_title(html),
        text=text,
        html_length=len(html),
        rendered=True,
        truncated=truncated,
    )


def cuda_device_count() -> int:
    """Kac CUDA cihazi var.

    CTranslate2'ye SORULUYOR, torch'a degil. faster-whisper torch
    kullanmiyor - arkasinda CTranslate2 var. Ilk hal torch'a soruyordu
    ve torch kurulu olmadigi icin "CUDA yok" diyordu: 8 GB'lik bir
    RTX 4060 dururken hizalama islemcide kosuyordu. Hicbir hata
    vermeden, yalnizca kat kat yavas.
    """
    try:
        import ctranslate2

        return int(ctranslate2.get_cuda_device_count())
    except (ImportError, AttributeError, OSError):
        return 0


async def transcribe(request: AlignRequest, config: Settings) -> AlignResponse:
    """Sesi cozer ve kelime zamanlarini olcer."""
    import asyncio

    from faster_whisper import WhisperModel

    usable, _ = align.cuda_usable(cuda_device_count())
    device = align.choose_device(config.align_device, usable)

    def run() -> tuple[list[align.RawWord], str]:
        model = WhisperModel(config.align_model, device=device, compute_type=config.align_compute_type)
        segments, info = model.transcribe(
            request.audio_path,
            language=request.language.split("-")[0],
            word_timestamps=True,
            # Metni BILIYORUZ: modele ipucu olarak veriliyor. Ozel
            # isimlerde ve sayilarda belirgin fark yaratiyor.
            initial_prompt=request.text,
        )

        words: list[align.RawWord] = []

        for segment in segments:
            for word in segment.words or []:
                words.append(align.RawWord(word.word, word.start, word.end, getattr(word, "probability", 1.0)))

        return words, info.language

    # Model cagrisi BLOKLAYICI: dogrudan cagirmak tum olay dongusunu
    # durdururdu ve saglik kontrolu bile cevap veremezdi.
    words, detected = await asyncio.to_thread(run)

    timings = align.to_timings(words)

    if request.text:
        timings = align.match_expected(timings, request.text)

    return AlignResponse(
        words=timings,
        language=detected,
        duration_ms=align.duration_ms(timings),
        model=config.align_model,
        device=device,
    )
