"""Tarayiciyla sayfa cekme.

VAR OLMA SEBEBI: .NET tarafindaki cekici (P1-06) duz HTTP yapiyor ve
JavaScript ile kurulan sayfalarda bos govde goruyor. Burada gercek bir
tarayici kosuyor.

TARAYICI OLMAK MUAFIYET DEGIL. En kolay hata su olurdu: cekme isi ayri
bir surece tasindigi icin P1-06'nin dort kapisinin (sema -> alan adi ->
robots.txt -> boyut) burada uygulanmamasi. O zaman bir cagriyi .NET
cekicisinden yan-servise tasimak, farkinda olmadan politikayi devre
disi birakmak olurdu. Kapilar burada da var ve AYNI SIRADA - robots
kontrolu boyut sinirindan once, cunku yasak bir sayfaya "sadece
bakmak" da cekmek sayiliyor.
"""

from __future__ import annotations

from dataclasses import dataclass
from urllib.parse import urlparse
from urllib.robotparser import RobotFileParser



@dataclass(frozen=True)
class Gate:
    """Bir kapinin sonucu."""

    allowed: bool
    reason: str = ""

    @staticmethod
    def ok() -> "Gate":
        return Gate(True)

    @staticmethod
    def deny(reason: str) -> "Gate":
        return Gate(False, reason)


def scheme_gate(url: str, blocked: list[str]) -> Gate:
    """1. kapi: sema.

    `file:` ve `data:` semalari bir tarayicida YEREL DOSYA okumaya
    aciliyor. Bu uc nokta disaridan gelen bir URL'yi aliyor; semayi
    kontrol etmemek, sunucunun diskini okutan bir cagriya izin vermek
    demekti.
    """
    try:
        parsed = urlparse(url)
    except ValueError as error:
        return Gate.deny(f"URL ayristirilamadi: {error}")

    scheme = (parsed.scheme or "").lower()

    if not scheme:
        return Gate.deny("sema yok: mutlak URL gerekiyor")

    if scheme in {s.lower() for s in blocked}:
        return Gate.deny(f"sema yasak: {scheme}")

    if scheme not in {"http", "https"}:
        return Gate.deny(f"desteklenmeyen sema: {scheme}")

    if not parsed.hostname:
        return Gate.deny("alan adi yok")

    return Gate.ok()


def robots_gate(robots_text: str | None, url: str, user_agent: str) -> Gate:
    """3. kapi: robots.txt.

    `robots_text` None ise cekilemedi demektir ve o zaman IZIN
    VERILMIYOR: "okuyamadim, o halde serbesttir" tam ters yonde bir
    hata olurdu, ustelik sunucu zorlanirken (P1-06 ile ayni karar).

    Bos bir robots.txt ise gecerli ve her seye izin veriyor - bu iki
    durumu ayirt etmek icin None ile "" farkli anlamlar tasiyor.
    """
    if robots_text is None:
        return Gate.deny("robots.txt okunamadi")

    parser = RobotFileParser()
    parser.parse(robots_text.splitlines())

    if parser.can_fetch(user_agent, url):
        return Gate.ok()

    return Gate.deny("robots.txt bu yolu yasakliyor")


def robots_url(url: str) -> str:
    parsed = urlparse(url)

    return f"{parsed.scheme}://{parsed.netloc}/robots.txt"
