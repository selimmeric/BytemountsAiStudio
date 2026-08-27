"""Cekme kapilarinin ve metin cikarmanin testleri.

Asil sinanan sey su: yan-servis ayri bir surecte kosuyor olsa da
P1-06'nin kurallari burada da gecerli. Bir cagriyi .NET cekicisinden
buraya tasimak, farkinda olmadan politikayi devre disi birakmamali.
"""

import pytest

from bmai_tools.extract import extract_text, extract_title
from bmai_tools.fetch import robots_gate, robots_url, scheme_gate

BLOCKED = ["file", "ftp", "data"]


@pytest.mark.parametrize(
    "url",
    [
        "file:///C:/Users/gizli.txt",
        "ftp://ornek.com/dosya",
        "data:text/html,<h1>x</h1>",
    ],
)
def test_yerel_ve_gomulu_semalar_yasak(url):
    """Bir TARAYICI kosuyoruz: `file:` semasi sunucunun diskini okutur."""
    gate = scheme_gate(url, BLOCKED)

    assert not gate.allowed
    assert "sema" in gate.reason


def test_bilinmeyen_sema_de_yasak():
    # Liste bir IZIN listesi degil, ek bir kapi. Listede olmayan ama
    # http/https de olmayan bir sema gecmemeli.
    assert not scheme_gate("javascript:alert(1)", BLOCKED).allowed


def test_gecerli_semalar_gecer():
    assert scheme_gate("https://ornek.com/a", BLOCKED).allowed
    assert scheme_gate("http://ornek.com/a", BLOCKED).allowed


def test_semasiz_ve_alan_adsiz_url_reddedilir():
    assert not scheme_gate("ornek.com/a", BLOCKED).allowed
    assert not scheme_gate("https:///yol", BLOCKED).allowed


def test_robots_okunamazsa_izin_yok():
    """"Okuyamadim, o halde serbesttir" ters yonde bir hata olurdu."""
    gate = robots_gate(None, "https://a.example/x", "BMAI")

    assert not gate.allowed


def test_bos_robots_serbest():
    # None ile "" FARKLI: biri okunamadi, digeri kural yok demek.
    assert robots_gate("", "https://a.example/x", "BMAI").allowed


def test_yasakli_yol_reddedilir():
    robots = "User-agent: *\nDisallow: /gizli"

    assert not robots_gate(robots, "https://a.example/gizli/sayfa", "BMAI").allowed
    assert robots_gate(robots, "https://a.example/acik/sayfa", "BMAI").allowed


def test_kendi_ajanimiza_ozel_kural_uygulanir():
    robots = "User-agent: BMAI\nDisallow: /\n\nUser-agent: *\nAllow: /"

    assert not robots_gate(robots, "https://a.example/x", "BMAI").allowed


def test_robots_adresi_alan_adi_kokunden():
    assert robots_url("https://a.example/derin/yol?x=1") == "https://a.example/robots.txt"


def test_script_ve_style_metne_girmez():
    html = "<body><script>var gizli=1;</script><style>p{color:red}</style><p>Metin</p></body>"

    assert extract_text(html)[0] == "Metin"


def test_baslik_govde_metnine_karismaz():
    """Karissaydi ozetin ilk cumlesi hep baslik cikardi."""
    html = "<html><head><title>Baslik</title></head><body><p>Govde</p></body></html>"

    assert extract_text(html)[0] == "Govde"
    assert extract_title(html) == "Baslik"


def test_blok_etiketleri_satir_sonu_uretir():
    html = "<body><p>Bir</p><p>Iki</p><li>Uc</li></body>"

    assert extract_text(html)[0].split("\n") == ["Bir", "Iki", "Uc"]


def test_html_varliklari_cozulur():
    assert extract_text("<body><p>Ba&#351;l&#305;k &amp; son</p></body>")[0] == "Başlık & son"


def test_kirpma_gorunur():
    """Yarim bir metni tam sanmak, eksik baglami gizlemek demek."""
    html = "<body><p>" + ("a" * 500) + "</p></body>"

    text, truncated = extract_text(html, max_chars=100)

    assert truncated
    assert len(text) <= 100


def test_kirpilmayan_metin_isaretlenmez():
    text, truncated = extract_text("<body><p>kisa</p></body>", max_chars=100)

    assert not truncated
    assert text == "kisa"


def test_bos_html_cokmez():
    assert extract_text("") == ("", False)
    assert extract_title("") == ""
