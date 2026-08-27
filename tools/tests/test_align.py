"""Hizalama testleri.

Model CAGRILMIYOR: sinanan sey, modelin verdigi ham araliklarin
sozlesmeye nasil cevrildigi. Bozuk bir aralik burada duzeltilmezse
altyazi olusturucusunda negatif sureli bir ipucuna donusuyor.
"""

import pytest

from bmai_tools.align import (
    RawWord,
    choose_device,
    cuda_usable,
    duration_ms,
    match_expected,
    normalize,
    to_timings,
)


def test_auto_karta_gore_secer():
    assert choose_device("auto", cuda_available=True) == "cuda"
    assert choose_device("auto", cuda_available=False) == "cpu"


def test_acikca_cuda_istenip_yoksa_hata():
    """Sessiz dusus, 20 kat yavas kosan bir servisi gizlerdi."""
    with pytest.raises(RuntimeError):
        choose_device("cuda", cuda_available=False)


def test_acikca_cpu_istenirse_kart_varken_de_cpu():
    assert choose_device("cpu", cuda_available=True) == "cpu"


def test_bos_deger_auto_sayilir():
    assert choose_device("", cuda_available=False) == "cpu"


def test_saniye_milisaniyeye_cevrilir():
    timings = to_timings([RawWord("bir", 0.0, 0.42)])

    assert timings[0].start_ms == 0
    assert timings[0].end_ms == 420


def test_bos_kelime_atlanir():
    assert to_timings([RawWord("   ", 0.0, 1.0), RawWord("bir", 1.0, 2.0)]) == to_timings(
        [RawWord("bir", 1.0, 2.0)]
    )


def test_cakisan_kelimeler_ayrilir():
    """Cakisma OLDUGU GIBI gecirilseydi altyazi iki kelimeyi ust uste bindirirdi."""
    timings = to_timings([RawWord("bir", 0.0, 1.0), RawWord("iki", 0.5, 1.5)])

    assert timings[0].end_ms <= timings[1].start_ms


def test_ters_aralik_duzeltilir():
    timings = to_timings([RawWord("bir", 1.0, 0.5)])

    assert timings[0].end_ms > timings[0].start_ms


def test_negatif_baslangic_sifirlanir():
    assert to_timings([RawWord("bir", -0.5, 1.0)])[0].start_ms == 0


def test_guven_araliga_sikistirilir():
    timings = to_timings([RawWord("a", 0, 1, 1.7), RawWord("b", 1, 2, -0.3)])

    assert timings[0].confidence == 1.0
    assert timings[1].confidence == 0.0


def test_beklenen_metin_yazimi_belirler():
    """Metinde "1453" yaziyor, model "bin dort yuz elli uc" duyuyor."""
    measured = to_timings([RawWord("bin", 0, 1), RawWord("dort", 1, 2)])

    matched = match_expected(measured, "1453 yilinda")

    assert [w.word for w in matched] == ["1453", "yilinda"]
    # Zamanlama MODELDEN geliyor; degismiyor.
    assert [w.start_ms for w in matched] == [w.start_ms for w in measured]


def test_kelime_sayisi_tutmazsa_olcum_korunur():
    """Hizali olmayan metni zorla eslestirmek, hepsini kaydirmaktan kotu."""
    measured = to_timings([RawWord("bir", 0, 1), RawWord("iki", 1, 2)])

    assert [w.word for w in match_expected(measured, "cok daha uzun bir metin")] == ["bir", "iki"]


def test_bos_olcum_sifir_sure():
    assert duration_ms([]) == 0


def test_sure_son_kelimenin_bitisi():
    assert duration_ms(to_timings([RawWord("a", 0, 1), RawWord("b", 1, 2.5)])) == 2500


def test_turkce_i_harfi_dogru_normalize_edilir():
    """`str.lower()` "I"yi "i" yapiyor ama Turkce'de "I"nin kucugu "i" degil."""
    assert normalize("Istanbul") == normalize("istanbul")
    assert normalize("IRMAK") == normalize("ırmak")


def test_noktalama_atilir():
    assert normalize("evet,") == normalize("evet")


# --- CUDA kullanilabilirligi ---
#
# Bu testlerin hepsi GERCEK bir hatadan geliyor. Ilk hal CUDA'yi
# torch'a soruyordu; torch kurulu degildi, "CUDA yok" dedi ve 8 GB'lik
# bir RTX 4060 dururken hizalama islemcide kostu. Duzeltince ikinci
# hata cikti: kart GORUNUYORDU ama cuBLAS kurulu degildi ve cagri
# modelin ortasinda "cublas64_12.dll bulunamadi" ile dustu.


def test_kart_yoksa_kullanilamaz():
    usable, reason = cuda_usable(0)

    assert not usable
    assert "cihazi yok" in reason


def test_kart_var_ama_kutuphane_yoksa_kullanilamaz():
    """Kartin gorunmesi yetmiyor: cuBLAS yoksa cagri modelin ortasinda duser."""
    usable, reason = cuda_usable(1, loader=lambda _: False)

    assert not usable
    # Sebep dogrudan cozumu soylemeli; "cuda: hayir" teshis edilemez.
    assert "kutuphaneleri eksik" in reason


def test_kart_ve_kutuphane_varsa_kullanilabilir():
    usable, reason = cuda_usable(2, loader=lambda _: True)

    assert usable
    assert "2" in reason


def test_eksik_kutuphaneler_isimleriyle_bildirilir():
    _, reason = cuda_usable(1, loader=lambda name: "cudnn" in name)

    assert "cublas" in reason
    assert "cudnn" not in reason
