"""Yan-servisin yapilandirmasi.

Tamami ortam degiskeninden. Sebep `docs/DONANIM-VE-MODEL.md` ile ayni:
filodaki her makine ayni imaji kosuyor, farkli olan yalnizca ortam.
Bir makinede hizalama GPU'dan, digerinde islemciden, ucuncusunde hic
yok - ve ucu de kod degisikligi gerektirmiyor.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path


def _home() -> Path:
    return Path.home()


def _env(name: str, default: str) -> str:
    value = os.environ.get(name)
    # Bos deger TANIMSIZ sayiliyor: bir kabuk betiginde `VAR=` yazmak,
    # bos bir model adiyla cagri yapip anlasilmaz bir hata almak demekti.
    return value if value else default


def _int(name: str, default: int) -> int:
    try:
        return int(_env(name, str(default)))
    except ValueError:
        # Bozuk bir sayi yuzunden surec HIC baslamamaktansa varsayilana
        # dusmesi yeg; deger zaten /health ciktisinda gorunuyor.
        return default


def _csv(name: str, default: str) -> list[str]:
    return [part.strip() for part in _env(name, default).split(",") if part.strip()]


@dataclass(frozen=True)
class Settings:
    """Calisma zamani ayarlari."""

    # --- /search ---
    searxng_url: str = field(default_factory=lambda: _env("BMAI_SEARXNG_URL", "http://localhost:8888"))

    # --- /fetch ---
    # Playwright bir TARAYICI kosuyor: JavaScript ile kurulan sayfalari
    # .NET tarafindaki HTTP cekici goremiyor. Ama tarayici olmasi
    # kurallardan muaf oldugu anlamina GELMIYOR - robots kontrolu
    # burada da var (bkz. fetch.py).
    fetch_timeout_seconds: int = field(default_factory=lambda: _int("BMAI_FETCH_TIMEOUT", 30))
    fetch_max_bytes: int = field(default_factory=lambda: _int("BMAI_FETCH_MAX_BYTES", 5_000_000))
    fetch_user_agent: str = field(
        default_factory=lambda: _env(
            "BMAI_USER_AGENT",
            "BytemountsAiStudio/0.1 (+https://github.com/bytemounts; icerik arastirma)",
        )
    )
    fetch_blocked_schemes: list[str] = field(default_factory=lambda: _csv("BMAI_BLOCKED_SCHEMES", "file,ftp,data"))

    # --- /align ---
    # Model boyutu VRAM'e gore: 8 GB kartta `small` rahat, `large-v3`
    # sigmiyor. Islemcide de kosuyor, yalnizca yavas.
    align_model: str = field(default_factory=lambda: _env("BMAI_ALIGN_MODEL", "small"))
    align_device: str = field(default_factory=lambda: _env("BMAI_ALIGN_DEVICE", "auto"))
    align_compute_type: str = field(default_factory=lambda: _env("BMAI_ALIGN_COMPUTE", "int8"))
    align_max_seconds: int = field(default_factory=lambda: _int("BMAI_ALIGN_MAX_SECONDS", 900))

    # --- /tts ---
    # Piper CEVRIMDISI ve ANAHTARSIZ. Windows'un yerel sentezi yalnizca
    # KURULU dil paketleri icin ses veriyor; ikinci dil (P1-26) ancak
    # bu sayede uretilebiliyor.
    piper_voices_dir: str = field(
        default_factory=lambda: _env("BMAI_PIPER_VOICES", str(_home() / ".cache" / "piper"))
    )
    piper_voice_default: str = field(default_factory=lambda: _env("BMAI_PIPER_VOICE", ""))


SETTINGS = Settings()
