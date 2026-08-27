"""Istek/yanit sozlesmeleri.

.NET tarafi bu alanlara gore ayristiriyor; isim degisikligi kirici bir
degisikliktir. Alan adlari snake_case - JSON tarafinda da oyle.
"""

from __future__ import annotations

from pydantic import BaseModel, Field


class SearchRequest(BaseModel):
    query: str = Field(min_length=1)
    language: str = "tr-TR"
    max_results: int = Field(default=5, ge=1, le=25)


class SearchHit(BaseModel):
    url: str
    title: str
    snippet: str = ""
    engine: str = ""
    source_type: str = "web"


class SearchResponse(BaseModel):
    hits: list[SearchHit]
    query: str
    # Kac sonuc DONDUGU degil, kac sonuc GELDIGI: sinir yuzunden
    # kirpildiysa bu gorunsun. Kirpmayi gizlemek, "arama zayif" ile
    # "sinirimiz dusuk" arasindaki farki yok ederdi.
    total_available: int


class FetchRequest(BaseModel):
    url: str
    # Sayfayi tarayiciyla RENDER etmek pahali (saniyeler, yuzlerce MB
    # bellek). Yalnizca gerektiginde: .NET tarafindaki HTTP cekici
    # (P1-06) once deniyor, bos donerse buraya geliyor.
    render: bool = True
    wait_for_selector: str | None = None


class FetchResponse(BaseModel):
    url: str
    final_url: str
    title: str
    text: str
    html_length: int
    rendered: bool
    truncated: bool


class AlignRequest(BaseModel):
    # Ses YOLU, icerik degil: dosyalar ortak depoda ve 5 MB'lik bir WAV'i
    # base64 ile JSON'a gomerek tasimak hem bellegi hem log'lari sisirirdi.
    audio_path: str
    # Beklenen metin. Verilirse hizalama ona gore yapiliyor; verilmezse
    # serbest cozumleme. Metin varken vermemek daha kotu sonuc verir.
    text: str | None = None
    language: str = "tr"


class WordTiming(BaseModel):
    word: str
    start_ms: int
    end_ms: int
    # Modelin kendi guveni. Dusuk guvenli bir hizalama, dagitimla
    # uretilmis bir tahminden daha iyi olmayabilir ve cagiran tarafin
    # bunu bilmesi gerekiyor.
    confidence: float = 1.0


class AlignResponse(BaseModel):
    words: list[WordTiming]
    language: str
    duration_ms: int
    model: str
    device: str


class Capability(BaseModel):
    name: str
    available: bool
    # Neden yok. "align: false" tek basina teshis edilemez bir bilgi.
    detail: str = ""


class HealthResponse(BaseModel):
    status: str
    version: str
    capabilities: list[Capability]
