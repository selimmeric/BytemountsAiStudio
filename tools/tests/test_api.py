"""Uc noktalarin davranisi.

Aga cikilmiyor: sinanan sey, bir yetenek KAPALIYKEN uc noktanin ne
yaptigi. Bu depoda gercek videolarin altyazisiz cikmasinin sebebi tam
olarak buydu - eksik bir yetenek sessizce daha kotu bir yola dustu ve
hicbir yerde gorunmedi.
"""

import pytest
from fastapi.testclient import TestClient

from bmai_tools.main import app

client = TestClient(app)


def test_saglik_yetenekleri_de_soyluyor():
    """"Ayaktayim" tek basina yaniltici: /fetch calisma aninda patlayabilir."""
    payload = client.get("/health").json()

    assert payload["status"] == "ok"

    names = {c["name"] for c in payload["capabilities"]}

    assert names == {"search", "fetch", "align", "ffmpeg"}


def test_kapali_yetenek_nedenini_soyluyor():
    """"align: false" tek basina teshis edilemez bir bilgi."""
    capabilities = {c["name"]: c for c in client.get("/health").json()["capabilities"]}

    for capability in capabilities.values():
        if not capability["available"]:
            assert capability["detail"], f"{capability['name']} neden kapali soylenmiyor"


def test_playwright_yoksa_fetch_503_doner():
    from bmai_tools import capabilities

    if capabilities.fetch_capability().available:
        pytest.skip("playwright kurulu; bu test yalnizca eksik kurulumu sinar")

    response = client.post("/fetch", json={"url": "https://ornek.com"})

    # 503: gecici bir eksiklik, istegin kusuru degil. .NET tarafi
    # bunu KAYNAK hatasi olarak okuyup erteleyebilsin (ADR-011).
    assert response.status_code == 503
    assert "playwright" in response.json()["detail"]


def test_yasak_sema_yetenekten_once_reddedilmez():
    """Sira onemli: yetenek kontrolu once, cunku 503 ile 400 farkli seyler.

    Playwright kuruluyken yasak sema 400 donuyor; kurulu degilken
    yetenek eksikligi 503 donuyor. Ikisi de dogru cevap - ama ayni
    anda ikisi birden dogru olamaz.
    """
    from bmai_tools import capabilities

    response = client.post("/fetch", json={"url": "file:///C:/gizli.txt"})

    if capabilities.fetch_capability().available:
        assert response.status_code == 400
    else:
        assert response.status_code == 503


def test_olmayan_ses_dosyasi():
    from bmai_tools import capabilities

    response = client.post("/align", json={"audio_path": "yok-boyle-bir-dosya.wav"})

    if capabilities.align_capability("small", "cpu").available:
        assert response.status_code == 400
    else:
        assert response.status_code == 503


def test_bos_sorgu_reddedilir():
    assert client.post("/search", json={"query": ""}).status_code == 422


def test_sonuc_siniri_ustten_sinirli():
    """Sinirsiz bir sonuc sayisi, tek cagriyla SearXNG'i zorlamak demekti."""
    assert client.post("/search", json={"query": "x", "max_results": 500}).status_code == 422
