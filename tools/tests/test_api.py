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

    assert names == {"search", "fetch", "align", "tts", "ffmpeg"}


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


# ---- tarayici kurulu degilken (30 Agu 2026'da olculdu) ----------------
#
# ***PAKETIN KURULU OLMASI TARAYICININ VAR OLMASI DEMEK DEGIL.***
#
# `pip install playwright` yalnizca python paketini getiriyor;
# tarayicinin kendisi ayri bir indirme ve EN SIK ATLANAN adim bu.
#
# Eski kod yalnizca ICE AKTARMAYA bakiyordu -- ustundeki yorum tam da
# bu senaryoyu tarif ettigi halde. Olculdu: tarayici dizini bos
# birakildiginda /health "fetch: acik" demeye devam etti ve cagri
# "Executable doesn't exist" ile dustu.
#
# Zarar iki katliydi: .NET tarafi 502'yi GECICI okuyor, yani kuyruk
# insan mudahalesi olmadan asla duzelmeyecek bir isi tekrar tekrar
# deniyordu; ve mesaj URL'yi isaret ettigi icin teshis eden kisi
# siteye, robots.txt'ye, aga bakardi -- kuruluma asla.


def test_tarayici_yoksa_saglik_acik_demiyor(monkeypatch):
    """***BU DOSYANIN EN ONEMLI TESTI.***

    Yuzeysel kontrol "acik" diyor; derin kontrol diske bakiyor.
    """
    from bmai_tools import capabilities

    monkeypatch.setattr(capabilities, "_BROWSER_PROBE", None)
    monkeypatch.setattr(
        capabilities, "browser_probe", lambda *a, **k: (r"C:\yok\chrome.exe", False)
    )

    capability = capabilities.fetch_capability(deep=True)

    assert not capability.available

    # NE YAPILACAGI YAZIYOR. "chromium yok" tek basina, okuyani
    # kurulum belgesini aramaya gonderirdi.
    assert "playwright install" in capability.detail


def test_tarayici_varsa_saglik_yolu_yaziyor(monkeypatch):
    from bmai_tools import capabilities

    monkeypatch.setattr(capabilities, "_BROWSER_PROBE", None)
    monkeypatch.setattr(
        capabilities, "browser_probe", lambda *a, **k: (r"C:\var\chrome.exe", True)
    )

    capability = capabilities.fetch_capability(deep=True)

    assert capability.available
    assert r"C:\var\chrome.exe" in capability.detail


def test_yoklama_yapilamazsa_kapali_denmiyor(monkeypatch):
    """BILMEMEK, BOZUK OLMAK DEGIL.

    Surucu baslamadiysa tarayicinin durumu hakkinda bir sey
    bilmiyoruz. "Kapali" demek, calisan bir kurulumu kapatmak olurdu;
    belirsizlik DETAYDA yaziyor.
    """
    from bmai_tools import capabilities

    monkeypatch.setattr(capabilities, "_BROWSER_PROBE", None)
    monkeypatch.setattr(capabilities, "browser_probe", lambda *a, **k: None)

    capability = capabilities.fetch_capability(deep=True)

    assert capability.available
    assert "dogrulanamadi" in capability.detail


def test_yuzeysel_kontrol_diske_bakmiyor(monkeypatch):
    """/fetch her cagrida 0,29 sn'lik yoklama odememeli.

    Orada tarayici zaten aciliyor; acilmazsa hata SINIFLANDIRILIYOR.
    """
    from bmai_tools import capabilities

    def patla(*args, **kwargs):
        raise AssertionError("yuzeysel kontrol yoklama yapmamali")

    monkeypatch.setattr(capabilities, "browser_probe", patla)

    capabilities.fetch_capability()


@pytest.mark.parametrize(
    "message",
    [
        r"BrowserType.launch: Executable doesn't exist at C:\yok\chrome.exe",
        "Looks like Playwright was just installed. Please run playwright install",
        # SURUM UYUSMAZLIGI: imajdaki tarayicilar bir surume ait, pip
        # daha yenisini kurmus. Aranan dizin adi tutmuyor ve hata yine
        # ayni -- sinif da ayni: eksik kurulum.
        "Executable doesn't exist at /ms-playwright/chromium-1155/chrome",
    ],
)
def test_tarayici_eksikligi_sayfa_hatasindan_ayriliyor(message):
    from bmai_tools.main import _browser_missing

    assert _browser_missing(Exception(message))


@pytest.mark.parametrize(
    "message",
    [
        "Timeout 30000ms exceeded",
        "net::ERR_NAME_NOT_RESOLVED at https://ornek.com",
        "Target page, context or browser has been closed",
    ],
)
def test_gercek_sayfa_hatalari_kurulum_sanilmiyor(message):
    """Yanlis yone kaymamali.

    Her playwright hatasini "kurulum eksik" saymak, gercek bir ag
    hatasini 15 dakika erteler ve sebebini gizlerdi.
    """
    from bmai_tools.main import _browser_missing

    assert not _browser_missing(Exception(message))
