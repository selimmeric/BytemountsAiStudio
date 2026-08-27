"""SearXNG uzerinden arama.

Cok az is yapiyor ve bu bilincli: .NET tarafinda zaten bir SearXNG
adaptoru var (P1-05b). Buradaki uc nokta, yan-servisi kullanan bir
dagitimda TEK bir dis kapi olsun diye var - zayif makineler SearXNG'e
dogrudan degil, bu servise baglaniyor.
"""

from __future__ import annotations

from typing import Any

from .models import SearchHit, SearchResponse

# Alan adina gore kaynak turu. .NET tarafiyla AYNI siniflandirma
# (WikipediaProvider, SearxngProvider); iki tarafta farkli olsaydi
# ayni kaynak, hangi yoldan geldigine gore farkli guven puani alirdi.
_SOURCE_TYPES: dict[str, str] = {
    "wikipedia.org": "encyclopedia",
    "wikidata.org": "encyclopedia",
    "britannica.com": "encyclopedia",
    "nature.com": "academic",
    "sciencedirect.com": "academic",
    "arxiv.org": "academic",
    "jstor.org": "academic",
    "pubmed.ncbi.nlm.nih.gov": "academic",
    "reuters.com": "news",
    "apnews.com": "news",
    "bbc.com": "news",
    "bbc.co.uk": "news",
    "aa.com.tr": "news",
    "nasa.gov": "official",
    "who.int": "official",
    "europa.eu": "official",
}


def classify(url: str) -> str:
    """Bir URL'nin kaynak turu.

    Alt alan adlari da sayiliyor: `tr.wikipedia.org` ansiklopedi.
    `.gov` ve `.edu` uzantilari listeye tek tek yazilamaz, kural olarak
    isleniyor.
    """
    host = _host(url)

    if not host:
        return "web"

    for domain, kind in _SOURCE_TYPES.items():
        if host == domain or host.endswith("." + domain):
            return kind

    if host.endswith(".gov") or host.endswith(".gov.tr"):
        return "official"

    if host.endswith(".edu") or host.endswith(".edu.tr") or host.endswith(".ac.uk"):
        return "academic"

    return "web"


def _host(url: str) -> str:
    from urllib.parse import urlparse

    try:
        return (urlparse(url).hostname or "").lower()
    except ValueError:
        return ""


def parse_results(payload: dict[str, Any], max_results: int, query: str) -> SearchResponse:
    """SearXNG yanitini sozlesmeye cevirir.

    Ayri ve SAF: aga cikmadan sinanabilsin. Ayristirmadaki bir hata,
    calisan bir SearXNG kurulumu gerektirmeden yakalanmali.
    """
    raw = payload.get("results")
    results: list[dict[str, Any]] = raw if isinstance(raw, list) else []

    hits: list[SearchHit] = []

    for item in results:
        if not isinstance(item, dict):
            continue

        url = item.get("url")

        # URL'siz bir sonuc cekilemez; basligi ne olursa olsun
        # kullanilamaz ve listede yer kaplamamali.
        if not isinstance(url, str) or not url.strip():
            continue

        hits.append(
            SearchHit(
                url=url.strip(),
                title=str(item.get("title") or url).strip(),
                snippet=str(item.get("content") or "").strip(),
                engine=str(item.get("engine") or "").strip(),
                source_type=classify(url),
            )
        )

        if len(hits) >= max_results:
            break

    # Kirpma GORUNUR: "arama zayif" ile "sinirimiz dusuk" ayri sorunlar.
    return SearchResponse(hits=hits, query=query, total_available=len(results))
