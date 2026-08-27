"""HTML'den ANA metin cikarma.

.NET tarafindaki `HtmlTextExtractor` ile AYNI kurallar. Iki tarafta
farkli olsaydi ayni sayfa, hangi cekiciden geldigine gore farkli metin
verirdi - ve icerik ozeti (sha256) tutmayacagi icin bilgi tabani onu
iki ayri kaynak sanardi (P1-11).

Bu dosya BIR HATA yuzunden ayrildi: ilk hal yalnizca etiketleri
soyuyordu ve gercek Vikipedi sayfasinda cikan metnin basi bastan asagi
menuydu - "Icerige atla / Ana menu / Dolasim / Anasayfa...". O metinle
iddia cikarilsaydi model "Rastgele madde"yi bir olgu sanabilirdi.
"""

from __future__ import annotations

import html as html_module
import re

# Icerigi hic olmayan, TAMAMEN atilan ogeler.
# `head` de burada: icindeki `<title>` ve `<meta>` metni govdeye
# karisiyordu. `<article>`/`<main>` olan sayfalarda daraltma bunu
# gizliyor, olmayanlarda ozetin ilk cumlesi hep baslik cikiyordu.
_DROPPED = ["head", "script", "style", "noscript", "svg", "iframe", "template", "form", "button", "select"]

# Metni olan ama ANA ICERIK olmayan ogeler. Menu ve altbilgi metni
# iddia cikarimina girerse kaynak guvenilirligi coker: "Gizlilik
# Politikasi" bir olgu degil.
_CHROME = ["nav", "header", "footer", "aside"]

_BLOCK_END = re.compile(
    r"</(p|div|section|article|blockquote|pre|li|tr|td|th|h[1-6]|figcaption)>",
    re.IGNORECASE,
)
_BR = re.compile(r"<br\s*/?>", re.IGNORECASE)
_TAG = re.compile(r"<[^>]+>")
_SPACES = re.compile(r"[ \t\r\f\v]+")
_BLANK_LINES = re.compile(r"\n{3,}")
_TITLE = re.compile(r"<title\b[^>]*>(.*?)</title>", re.IGNORECASE | re.DOTALL)


def _drop(html: str, tag: str) -> str:
    pattern = re.compile(rf"<{tag}\b[^>]*>.*?</{tag}>", re.IGNORECASE | re.DOTALL)
    previous = None

    # IC ICE gecmis ogeler icin tekrar tekrar: bir `<div>` icindeki
    # `<nav>` atildiginda disaridaki `<nav>` hala duruyor olabilir.
    # Tek gecis, ic ice menusu olan sayfalarda menuyu birakiyordu.
    while previous != html:
        previous = html
        html = pattern.sub(" ", html)

    return html


def _narrow(html: str, tag: str) -> str | None:
    """Sayfanin yalnizca ana icerik bolgesini alir.

    `<article>` ya da `<main>` varsa yalnizca onu aliyoruz: sayfanin
    geri kalani (kenar cubugu, ilgili baglantilar, yorumlar) ana metin
    degil ve modele verildiginde konuyu dagitiyor.
    """
    match = re.search(rf"<{tag}\b[^>]*>(.*?)</{tag}>", html, re.IGNORECASE | re.DOTALL)

    return match.group(1) if match else None


def extract_title(html: str) -> str:
    match = _TITLE.search(html or "")

    if not match:
        return ""

    return _SPACES.sub(" ", html_module.unescape(_TAG.sub(" ", match.group(1)))).strip()


def extract_text(html: str, max_chars: int = 200_000) -> tuple[str, bool]:
    """Ana metni cikarir.

    Donen ikili: (metin, kirpildi mi). Kirpmanin gorunur olmasi sart -
    yarim bir metni tam sanmak, modele eksik baglam verip nedenini
    gizlemek demek.
    """
    if not html:
        return "", False

    working = html

    for tag in _DROPPED:
        working = _drop(working, tag)

    for tag in _CHROME:
        working = _drop(working, tag)

    # `<article>` once: bir sayfada ikisi de varsa article daha dar ve
    # daha dogru olani.
    narrowed = _narrow(working, "article") or _narrow(working, "main") or working

    # Daraltma sonrasi metin YOK denecek kadar azsa daraltma yanlis
    # bolgeyi secmis demektir; tum govdeye donuluyor. Bos bir metin
    # dondurmek, sayfayi hic cekmemekten daha kotu - "cektik ama
    # bostu" diye kayda giriyor.
    if len(_plain(narrowed)) < 200 <= len(_plain(working)):
        narrowed = working

    text = _plain(narrowed)

    if len(text) <= max_chars:
        return text, False

    return text[:max_chars].rstrip(), True


def _plain(html: str) -> str:
    body = _BR.sub("\n", html)
    body = _BLOCK_END.sub("\n", body)
    body = _TAG.sub(" ", body)
    body = html_module.unescape(body)
    body = _SPACES.sub(" ", body)
    body = "\n".join(line.strip() for line in body.split("\n"))

    return _BLANK_LINES.sub("\n\n", body).strip()


def looks_paywalled(html: str, text: str) -> bool:
    """Odeme duvari suphesi.

    Kesin bir tespit DEGIL ve oyle iddia edilmiyor: buyuk bir HTML'den
    cok kisa bir metin cikmasi, cogu zaman icerigin gizlendigi anlamina
    geliyor. Iddia cikarimi bu bayrakli kaynaklari kullanmiyor.
    """
    return len(html) > 40_000 and len(text) < 500
