"""Piper ile yerel konusma sentezi.

VAR OLMA SEBEBI SOMUT: Windows'un yerel sentezi yalnizca KURULU dil
paketleri icin ses veriyor. Bu makinede yalnizca `Microsoft Tolga`
(tr-TR) var, yani ikinci dil (P1-26) hic uretilemiyordu - ve daha
kotusu, duzeltilmeden once sessizce Turkce sesle okunuyordu.

Piper tamamen CEVRIMDISI ve ANAHTARSIZ (ADR-015). Ses basina ~63 MB
ONNX modeli; islemcide gercek zamanin ~15 katı hizinda kosuyor, yani
filodaki 2 GB'lik makineler de seslendirme yapabiliyor
(docs/DONANIM-VE-MODEL.md).

Modeller BELLEKTE TUTULUYOR: ilk yukleme saniyeler suruyor, sonraki
cagrilar milisaniye. Her cumlede yeniden yuklemek, on cumlelik bir
senaryoyu bir dakika uzatirdi.
"""

from __future__ import annotations

import io
import wave
from pathlib import Path
from typing import Any

# Yuklenmis modeller. Anahtar ses adi.
_LOADED: dict[str, Any] = {}


def voices_dir(configured: str) -> Path:
    return Path(configured).expanduser()


def installed_voices(directory: Path) -> list[str]:
    """Indirilmis seslerin adlari."""
    if not directory.is_dir():
        return []

    return sorted(path.stem for path in directory.glob("*.onnx"))


def voice_for(language: str, voices: list[str], preferred: str | None = None) -> str | None:
    """Bir dil icin hangi sesin kullanilacagi.

    Sira: acikca istenen ses -> tam dil eslesmesi (tr_TR) -> ana dil
    eslesmesi (tr). Hicbiri yoksa None, ve cagiran taraf bunu SESSIZCE
    baska bir dile dusmek yerine hata olarak bildiriyor: Ingilizce
    metni Turkce sesle okutmak, hicbir yerde gorunmeyen bir kusur.
    """
    if preferred and preferred in voices:
        return preferred

    # Piper "tr_TR-dfki-medium" bicimini kullaniyor; dil etiketi
    # "tr-TR" geliyor.
    tag = language.replace("-", "_")
    primary = tag.split("_")[0]

    exact = [v for v in voices if v.lower().startswith(tag.lower() + "-")]

    if exact:
        return exact[0]

    loose = [v for v in voices if v.lower().startswith(primary.lower() + "_")]

    return loose[0] if loose else None


def load(directory: Path, voice: str) -> Any:
    """Modeli yukler; ayni ses ikinci kez yuklenmiyor."""
    if voice in _LOADED:
        return _LOADED[voice]

    from piper import PiperVoice

    model = PiperVoice.load(str(directory / f"{voice}.onnx"))
    _LOADED[voice] = model

    return model


def synthesize(directory: Path, voice: str, text: str, speed: float = 1.0) -> tuple[bytes, int, int]:
    """Metni seslendirir.

    Donen uclu: (wav baytlari, ornekleme hizi, sure ms).

    Sure bilgi amacli: hatta giren sure HER ZAMAN ffprobe ile olculen
    (ADR-006). Burada dondurulmesinin tek sebebi teshis - "servis ne
    uretti" sorusunun cevabi.
    """
    model = load(directory, voice)

    buffer = io.BytesIO()

    with wave.open(buffer, "wb") as output:
        # Piper'in kendi hiz ayari `length_scale`: BUYUK deger YAVAS
        # konusma demek, yani istenen hizin TERSI.
        try:
            from piper import SynthesisConfig

            model.synthesize_wav(
                text,
                output,
                syn_config=SynthesisConfig(length_scale=1.0 / max(speed, 0.1)),
            )
        except ImportError:
            model.synthesize_wav(text, output)

    data = buffer.getvalue()

    with wave.open(io.BytesIO(data)) as reader:
        rate = reader.getframerate()
        duration_ms = int(round(reader.getnframes() / rate * 1000))

    return data, rate, duration_ms
