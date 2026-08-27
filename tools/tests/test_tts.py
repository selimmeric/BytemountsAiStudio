"""Ses secimi testleri.

Model YUKLENMIYOR: sinanan sey, bir dil icin hangi sesin secildigi ve
SECILEMEDIGINDE ne oldugu. Ikincisi asil mesele - baska bir dilin
sesine dusmek, Ingilizce metni Turkce sesle okutmak demekti ve bu
hicbir yerde gorunmezdi.
"""

from pathlib import Path

from bmai_tools.tts import installed_voices, voice_for

VOICES = ["en_US-amy-medium", "en_GB-alba-medium", "tr_TR-dfki-medium"]


def test_tam_dil_eslesmesi():
    assert voice_for("tr-TR", VOICES) == "tr_TR-dfki-medium"
    assert voice_for("en-US", VOICES) == "en_US-amy-medium"


def test_ana_dil_eslesmesi_yeterli():
    """"en-AU" icin tam karsilik yok ama Ingilizce bir ses kabul edilir.

    Hangi Ingilizce sesin secildigi onemli DEGIL, Ingilizce olmasi
    onemli: aksan farki bir kusur, yanlis dil bir felaket.
    """
    assert voice_for("en-AU", VOICES).startswith("en_")


def test_acikca_istenen_ses_oncelikli():
    assert voice_for("en-US", VOICES, preferred="en_GB-alba-medium") == "en_GB-alba-medium"


def test_istenen_ses_yoksa_dile_gore_secilir():
    # Kurulu olmayan bir ses adi, secimi engellemiyor - ama dilden
    # sapmaya da yol acmiyor.
    assert voice_for("tr-TR", VOICES, preferred="boyle-bir-ses-yok") == "tr_TR-dfki-medium"


def test_dil_icin_ses_yoksa_none():
    """Baska dilin sesine DUSULMUYOR."""
    assert voice_for("de-DE", VOICES) is None
    assert voice_for("ja-JP", VOICES) is None


def test_hic_ses_yoksa_none():
    assert voice_for("tr-TR", []) is None


def test_olmayan_klasor_cokmez():
    assert installed_voices(Path("boyle-bir-klasor-yok")) == []
