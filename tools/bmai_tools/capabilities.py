"""Hangi yeteneklerin gercekten kullanilabilir oldugu.

Bu dosyanin var olma sebebi somut bir hata sinifi: bir yetenegin
EKSIK olmasi ile CALISMAMASI farkli seyler, ve fark gorunmezse sistem
sessizce daha kotu bir yola duser. Bu depoda tam olarak bu oldu -
gercek videolar altyazisiz cikti cunku kelime zamanlamasi yoktu ve
kimse fark etmedi.

Bu yuzden /health yalnizca "ayaktayim" demiyor: her yetenegin acik
olup olmadigini ve KAPALIYSA NEDEN kapali oldugunu soyluyor.
"""

from __future__ import annotations

import importlib.util
import os
import shutil
import time

from .models import Capability


def _module(name: str) -> bool:
    return importlib.util.find_spec(name) is not None


def search_capability(searxng_url: str) -> Capability:
    # SearXNG ayri bir servis; burada yalnizca yapilandirilmis olup
    # olmadigina bakiliyor. Ayakta mi sorusunun cevabi cagri aninda
    # geliyor - saglik kontrolunde her seferinde aga cikmak, saglik
    # kontrolunu yavas ve kirilgan yapardi.
    return Capability(name="search", available=bool(searxng_url), detail=searxng_url)


# Tarayici yoklamasinin onbellegi: (zaman, yol, var_mi).
#
# Yoklama surucuyu baslatiyor ve OLCULDU: 0,29 sn. /health'i her
# cagrida bu kadar yavaslatmak, saglik kontrolunu kullanilmaz
# yapardi. TTL kisa cunku eksik tarayici KURULABILIR bir sey:
# birisi `playwright install chromium` calistirdiginda /health'in
# bir dakika icinde dogruyu soylemesi gerekiyor.
_BROWSER_PROBE: tuple[float, str, bool] | None = None
_BROWSER_PROBE_TTL = 60.0


def browser_probe(now: float | None = None) -> tuple[str, bool] | None:
    """Chromium GERCEKTEN diskte mi.

    ***PAKETIN KURULU OLMASI TARAYICININ VAR OLMASI DEMEK DEGIL.***
    `pip install playwright` yalnizca python paketini getiriyor;
    tarayicinin kendisi ayri bir indirme
    (`python -m playwright install chromium`) ve EN SIK ATLANAN adim
    bu.

    Yalnizca ice aktarmaya bakan eski kontrol bu durumu goremiyordu:
    /health "fetch acik" diyordu, cagri ise "Executable doesn't
    exist" ile dusuyordu. OLCULDU (30 Agu 2026): tarayici dizini bos
    birakildiginda saglik "True" demeye devam etti.

    Yoklama TARAYICIYI ACMIYOR: yalnizca beklenen yolu soruyor ve o
    yolun diskte olup olmadigina bakiyor. Acmak saniyeler surerdi ve
    bir saglik kontrolunun butcesi degil.

    None donerse yoklama yapilamadi (surucu baslamadi); o zaman
    "bilmiyorum" demek, "bozuk" demekten dogru.

    ***YOKLAMA YAKLASIK.*** `executable_path` basli chrome'u
    gosteriyor; `launch(headless=True)` yeni surumlerde ayri bir
    "headless shell" ikilisini kullanabiliyor. Ikisi normalde birlikte
    kuruluyor, ama yalnizca biri varsa bu yoklama yanilabilir.
    Kesin cevap /fetch'te: orada tarayici GERCEKTEN aciliyor ve
    acilmazsa hata siniflandiriliyor. Buradaki yoklama TESHIS icin --
    "neden calismiyor" sorusuna bir kosu yapmadan cevap veriyor.
    """
    global _BROWSER_PROBE

    stamp = time.monotonic() if now is None else now

    if _BROWSER_PROBE is not None and stamp - _BROWSER_PROBE[0] < _BROWSER_PROBE_TTL:
        return _BROWSER_PROBE[1], _BROWSER_PROBE[2]

    try:
        from playwright.sync_api import sync_playwright

        with sync_playwright() as playwright:
            path = playwright.chromium.executable_path
    except Exception:  # noqa: BLE001 - surucu baslamadiysa sebebi onemli degil
        return None

    exists = bool(path) and os.path.exists(path)
    _BROWSER_PROBE = (stamp, path, exists)

    return path, exists


def fetch_capability(deep: bool = False) -> Capability:
    """`deep=True` tarayicinin diskte olup olmadigina da bakiyor.

    /health derin yokluyor (senkron uc, is parcaciginda kosuyor ve
    dogruyu soylemesi sart). /fetch yoklamiyor: orada tarayici zaten
    aciliyor ve acilmazsa hata SINIFLANDIRILIYOR - iki kez bakmak
    her cagriya bedava olmayan bir gecikme eklerdi.
    """
    if not _module("playwright"):
        return Capability(
            name="fetch",
            available=False,
            detail="playwright kurulu degil: pip install 'bmai-tools[fetch]'",
        )

    try:
        from playwright.sync_api import sync_playwright  # noqa: F401
    except ImportError as error:  # pragma: no cover - kurulum bozuksa
        return Capability(name="fetch", available=False, detail=f"playwright ice aktarilamadi: {error}")

    if not deep:
        return Capability(name="fetch", available=True, detail="playwright chromium")

    probe = browser_probe()

    if probe is None:
        # YOKLAMA YAPILAMADI: surucu baslamadi. "Acik" demek yaniltici
        # olurdu ama "kapali" demek de yanlis olabilir; belirsizlik
        # detayda YAZIYOR.
        return Capability(
            name="fetch",
            available=True,
            detail="playwright chromium (tarayici dogrulanamadi)",
        )

    path, exists = probe

    if not exists:
        return Capability(
            name="fetch",
            available=False,
            detail=f"chromium indirilmemis ({path}): python -m playwright install chromium",
        )

    return Capability(name="fetch", available=True, detail=f"playwright chromium ({path})")


def align_capability(model: str, device: str) -> Capability:
    if not _module("faster_whisper"):
        return Capability(
            name="align",
            available=False,
            detail="faster-whisper kurulu degil: pip install 'bmai-tools[align]'",
        )

    # Cozumlenmis cihaz da yaziliyor: "auto" tek basina hicbir sey
    # soylemiyor ve asil merak edilen kartin kullanilip
    # kullanilmadigi.
    return Capability(name="align", available=True, detail=f"{model} / {device} -> {resolved_device(device)}")


def resolved_device(requested: str) -> str:
    """"auto" ne anlama geldi ve nedenl.

    Kartin GORUNMESI yetmiyor: surucu var, kart sayiliyor, ama cuBLAS
    kurulu degilse cagri modelin ortasinda dusuyor. Burada gercek
    kullanilabilirlik soruluyor.
    """
    from .align import cuda_usable

    usable, reason = cuda_usable(_cuda_device_count())
    wanted = (requested or "auto").strip().lower()

    if wanted == "auto":
        return "cuda" if usable else f"cpu ({reason})"

    if wanted == "cuda" and not usable:
        return f"cuda ISTENDI AMA KULLANILAMIYOR: {reason}"

    return wanted


def _cuda_device_count() -> int:
    try:
        import ctranslate2

        return int(ctranslate2.get_cuda_device_count())
    except (ImportError, AttributeError, OSError):
        return 0


def ffmpeg_capability() -> Capability:
    # Hizalama ses dosyasini cozmek icin ffmpeg istiyor; yoksa model
    # yuklense bile cagri basarisiz oluyor.
    path = shutil.which("ffmpeg")

    return Capability(name="ffmpeg", available=path is not None, detail=path or "PATH'te bulunamadi")


def tts_capability(voices_directory: str) -> Capability:
    """Seslendirme yapabiliyor muyuz VE hangi dillerde.

    Sesin hangi DILLER icin oldugu da yaziliyor: "tts: evet" tek basina
    yaniltici - Turkce sesi olan bir servis Ingilizce icin ise
    yaramiyor ve fark ancak cagri aninda ortaya cikardi.
    """
    if not _module("piper"):
        return Capability(
            name="tts",
            available=False,
            detail="piper-tts kurulu degil: pip install 'bmai-tools[tts]'",
        )

    from pathlib import Path

    from .tts import installed_voices

    voices = installed_voices(Path(voices_directory).expanduser())

    if not voices:
        return Capability(
            name="tts",
            available=False,
            detail=f"hic ses indirilmemis ({voices_directory}): "
            "python -m piper.download_voices en_US-amy-medium",
        )

    return Capability(name="tts", available=True, detail=", ".join(voices))
