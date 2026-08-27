"""Kelime zamanlarini SESTEN olcer (P1-15).

Bu dosya bir tahmini bir OLCUMLE degistiriyor. Su an sistem, TTS
kelime zamani vermediginde sureyi kelimelere karakter sayisina gore
dagitiyor (P1-15a). O dagitim isliyor ama bir hizalama DEGIL: uzun bir
duraklama, vurgu ya da hizlanma oldugunda altyazi sesten kayiyor.

Oncelik sirasi (P1-15):
  1. TTS saglayicisinin kendi zamanlamasi  - en dogru, bedava
  2. ASR hizalamasi (bu dosya)             - dogru, pahali
  3. Karakter bazli dagitim (P1-15a)       - tahmin, bedava

Ucuncusune dusuldugunde cikti `timings_estimated` bayragini tasiyor.
Bayrak bu yuzden var: bir kayma arastirilirken ilk bakilacak sey o.
"""

from __future__ import annotations

import ctypes
import sys
import unicodedata
from collections.abc import Callable
from dataclasses import dataclass

from .models import WordTiming


@dataclass(frozen=True)
class RawWord:
    """Modelden gelen ham kelime."""

    word: str
    start: float
    end: float
    probability: float = 1.0


# CTranslate2'nin CUDA'da calismasi icin gereken kutuphaneler.
# Ekran KARTININ olmasi yetmiyor: surucu var, kart gorunuyor, ama
# cuBLAS ve cuDNN kurulu degilse cagri MODELIN ORTASINDA patliyor.
# Bu tam olarak yasandi: `get_cuda_device_count()` 1 dedi, hizalama
# baslamis bir istegi "cublas64_12.dll bulunamadi" ile dusurdu.
_CUDA_LIBRARIES = {
    "win32": ["cublas64_12.dll", "cudnn_ops64_9.dll"],
    "linux": ["libcublas.so.12", "libcudnn_ops.so.9"],
}


def cuda_usable(device_count: int, loader: Callable[[str], bool] | None = None) -> tuple[bool, str]:
    """CUDA GERCEKTEN kullanilabilir mi.

    Donen ikili: (kullanilabilir mi, aciklama). Aciklama /health'e
    giriyor - "cuda: hayir" tek basina teshis edilemez, "kart var ama
    cublas64_12.dll yok" dogrudan cozumu soyluyor.
    """
    if device_count <= 0:
        return False, "CUDA cihazi yok"

    load = loader or _can_load
    missing = [name for name in _CUDA_LIBRARIES.get(sys.platform, []) if not load(name)]

    if missing:
        return False, f"kart var ama CUDA kutuphaneleri eksik: {', '.join(missing)}"

    return True, f"{device_count} CUDA cihazi"


def _can_load(name: str) -> bool:
    try:
        ctypes.CDLL(name)

        return True
    except OSError:
        return False


def choose_device(requested: str, cuda_available: bool) -> str:
    """Hangi cihazda kosulacagi.

    "auto" varsayilan cunku ayni imaj hem 8 GB'lik kartta hem ekran
    karti olmayan bir makinede kosuyor (docs/DONANIM-VE-MODEL.md).
    Acikca "cuda" istenip kart yoksa CPU'ya DUSULMUYOR: sessiz bir
    dusus, "GPU'da kosuyor" sanilan bir servisin aslinda 20 kat yavas
    calismasi demekti ve bu ancak kuyruk buyudugunde fark edilirdi.
    """
    wanted = (requested or "auto").strip().lower()

    if wanted == "auto":
        return "cuda" if cuda_available else "cpu"

    if wanted == "cuda" and not cuda_available:
        raise RuntimeError("cuda istendi ama kullanilabilir bir CUDA kurulumu yok")

    return wanted


def normalize(word: str) -> str:
    """Karsilastirma icin kelimeyi sadelestirir.

    Turkce'ye ozel bir tuzak var: `str.lower()` "I" harfini "i" yapiyor
    ama Turkce'de "I"nin kucugu "i" degil. Karsilastirma buyuk harfe
    cevirerek yapiliyor ve once "i" acikca "I"ya esleniyor.
    """
    text = word.strip()
    text = "".join(ch for ch in text if not unicodedata.category(ch).startswith("P"))
    text = text.replace("i", "I").replace("\u0131", "I")

    return text.upper()


def to_timings(words: list[RawWord]) -> list[WordTiming]:
    """Ham kelimeleri sozlesmeye cevirir; bozuk araliklari duzeltir.

    Modelin verdigi araliklar her zaman duzgun degil: bazen bitis
    baslangictan kucuk cikiyor, bazen iki kelime cakisiyor. Bunlari
    OLDUGU GIBI gecirmek, altyazi olusturucusunda negatif sureli bir
    ipucuna ve orada bir cokmeye donusurdu.
    """
    timings: list[WordTiming] = []
    previous_end = 0

    for raw in words:
        if not raw.word.strip():
            continue

        start = max(int(round(raw.start * 1000)), 0)
        end = int(round(raw.end * 1000))

        # Cakisma: bir onceki kelimenin bitisinden once baslayamaz.
        start = max(start, previous_end)

        # Ters aralik: en az 1 ms surmeli ki sifir sureli bir ipucu
        # olusmasin.
        if end <= start:
            end = start + 1

        timings.append(
            WordTiming(
                word=raw.word.strip(),
                start_ms=start,
                end_ms=end,
                confidence=max(0.0, min(1.0, raw.probability)),
            )
        )

        previous_end = end

    return timings


def match_expected(measured: list[WordTiming], expected_text: str) -> list[WordTiming]:
    """Olculen kelimeleri BEKLENEN metne gore duzeltir.

    ASR kelimeyi yanlis duyabiliyor ve ozellikle ozel isimlerde
    duyuyor. Ama biz metni ZATEN BILIYORUZ - senaryoyu kendimiz
    urettik. O yuzden zamanlama modelden, YAZIM metinden aliniyor.

    Sayilar bu ayrimin en gorunur oldugu yer: metinde "1453" yaziyor,
    model "bin dort yuz elli uc" duyuyor. Altyaziya modelin duydugu
    yazilsaydi ekranda yazan sey senaryodan farkli olurdu.

    BIRLESTIRME GEREKIYOR cunku ASR bir kelimeyi bolebiliyor.
    Turkce'de bu istisna degil KURAL: "Turkiye'nin" iki parca olarak
    olculuyor ("Turkiye" + "'nin"). Ilk hal yalnizca kelime sayilari
    esitse esleme yapiyordu ve gercek bir cumlede -- 9 olculen, 8
    beklenen -- hic devreye girmedi. Turkce'de neredeyse her ozel isim
    kesme isareti aliyor, yani esleme pratikte hicbir zaman
    calismayacakti.

    Eslestirilemezse OLCUM OLDUGU GIBI donuyor: hizali olmayan bir
    metni zorla eslestirmek, hepsini kaydirmaktan daha kotu.
    """
    expected = [w for w in expected_text.split() if w.strip()]

    if not expected or not measured:
        return measured

    matched: list[WordTiming] = []
    index = 0

    for word in expected:
        target = normalize(word)

        if not target:
            # Yalnizca noktalamadan olusan bir "kelime": olculen
            # tarafta karsiligi yok, atlanmali.
            continue

        merged = ""
        first = index

        while index < len(measured) and normalize_merge(merged) != target:
            merged += measured[index].word
            index += 1

        if normalize_merge(merged) != target:
            # Bu kelime eslestirilemedi; tum eslemeden vazgeciliyor.
            # Kismi bir esleme, kalan kelimeleri kaydirirdi.
            return measured

        matched.append(
            WordTiming(
                word=word,
                start_ms=measured[first].start_ms,
                end_ms=measured[index - 1].end_ms,
                # Birlesen parcalarin EN DUSUK guveni: bir parca supheliyse
                # birlesik kelime de supheli.
                confidence=min(m.confidence for m in measured[first:index]),
            )
        )

    # Olculen kelimelerin hepsi tuketilmediyse metin ile ses
    # ortusmuyor demektir.
    return matched if index == len(measured) else measured


def normalize_merge(text: str) -> str:
    """Birlestirilmis parcalari karsilastirma icin sadelestirir."""
    return normalize(text).replace(" ", "")


def duration_ms(timings: list[WordTiming]) -> int:
    return timings[-1].end_ms if timings else 0
