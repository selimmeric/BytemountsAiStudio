"""Arama ayristirmasi ve kaynak siniflandirmasi testleri."""

from bmai_tools.search import classify, parse_results


def test_alt_alan_adi_da_sayilir():
    assert classify("https://tr.wikipedia.org/wiki/X") == "encyclopedia"
    assert classify("https://en.m.wikipedia.org/wiki/X") == "encyclopedia"


def test_uzanti_kuralla_isleniyor():
    # `.gov` ve `.edu` tek tek listeye yazilamaz.
    assert classify("https://nasa.gov/x") == "official"
    assert classify("https://saglik.gov.tr/x") == "official"
    assert classify("https://bogazici.edu.tr/x") == "academic"


def test_bilinmeyen_alan_web():
    assert classify("https://bir-blog.example/yazi") == "web"


def test_bozuk_url_cokmez():
    assert classify("bu bir url degil") == "web"
    assert classify("") == "web"


def test_urlsiz_sonuc_atlanir():
    payload = {"results": [{"title": "urlsiz"}, {"url": "https://a.example", "title": "A"}]}

    assert [hit.url for hit in parse_results(payload, 5, "q").hits] == ["https://a.example"]


def test_bos_url_atlanir():
    payload = {"results": [{"url": "   ", "title": "bos"}]}

    assert parse_results(payload, 5, "q").hits == []


def test_baslik_yoksa_url_kullanilir():
    payload = {"results": [{"url": "https://a.example"}]}

    assert parse_results(payload, 5, "q").hits[0].title == "https://a.example"


def test_kirpma_gorunur():
    """"arama zayif" ile "sinirimiz dusuk" ayri sorunlar."""
    payload = {"results": [{"url": f"https://a{i}.example"} for i in range(10)]}

    response = parse_results(payload, 3, "q")

    assert len(response.hits) == 3
    assert response.total_available == 10


def test_sonuc_alani_yoksa_bos_doner():
    assert parse_results({}, 5, "q").hits == []
    assert parse_results({"results": "liste degil"}, 5, "q").hits == []


def test_liste_icinde_sozluk_olmayan_ogeler_atlanir():
    payload = {"results": ["metin", 42, {"url": "https://a.example"}]}

    assert len(parse_results(payload, 5, "q").hits) == 1
