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
import shutil

from .models import Capability


def _module(name: str) -> bool:
    return importlib.util.find_spec(name) is not None


def search_capability(searxng_url: str) -> Capability:
    # SearXNG ayri bir servis; burada yalnizca yapilandirilmis olup
    # olmadigina bakiliyor. Ayakta mi sorusunun cevabi cagri aninda
    # geliyor - saglik kontrolunde her seferinde aga cikmak, saglik
    # kontrolunu yavas ve kirilgan yapardi.
    return Capability(name="search", available=bool(searxng_url), detail=searxng_url)


def fetch_capability() -> Capability:
    if not _module("playwright"):
        return Capability(
            name="fetch",
            available=False,
            detail="playwright kurulu degil: pip install 'bmai-tools[fetch]'",
        )

    # Paket kurulu ama TARAYICI indirilmemis olabilir; en sik atlanan
    # adim bu ve hata ancak ilk cagrida cikiyor.
    try:
        from playwright.sync_api import sync_playwright  # noqa: F401
    except ImportError as error:  # pragma: no cover - kurulum bozuksa
        return Capability(name="fetch", available=False, detail=f"playwright ice aktarilamadi: {error}")

    return Capability(name="fetch", available=True, detail="playwright chromium")


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
