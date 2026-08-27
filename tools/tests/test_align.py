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
    """Zamanlama MODELDEN, yazim METINDEN.

    ASR ozel isimleri yanlis duyuyor; biz metni zaten biliyoruz.
    Ornekte model "gobeklitepe" duymus, senaryoda "Gobeklitepe"
    yaziyor - ekrana senaryodaki yazim cikiyor.
    """
    measured = to_timings([RawWord("gobeklitepe", 0, 1), RawWord("kazisi", 1, 2)])

    matched = match_expected(measured, "Gobeklitepe kazisi")

    assert [w.word for w in matched] == ["Gobeklitepe", "kazisi"]
    # Zamanlama degismiyor.
    assert [w.start_ms for w in matched] == [w.start_ms for w in measured]


def test_sayilar_esitse_bile_metin_ortusmuyorsa_dayatilmaz():
    """Konum bazli dayatma SAGLAM DEGIL.

    Ilk hal, kelime sayilari esitse beklenen metni oldugu gibi
    yaziyordu - "bin dort"un zamanlamasina "1453 yilinda" yazmak gibi.
    Sayilarin tesadufen tutmasi, kelimelerin ayni oldugu anlamina
    gelmiyor ve o dayatma altyaziyi sessizce kaydirirdi.
    """
    measured = to_timings([RawWord("bin", 0, 1), RawWord("dort", 1, 2)])

    assert [w.word for w in match_expected(measured, "1453 yilinda")] == ["bin", "dort"]


def test_eslestirilemezse_olcum_korunur():
    """Hizali olmayan metni zorla eslestirmek, hepsini kaydirmaktan kotu."""
    measured = to_timings([RawWord("bir", 0, 1), RawWord("iki", 1, 2)])

    assert [w.word for w in match_expected(measured, "cok daha uzun bir metin")] == ["bir", "iki"]


# --- Bolunmus kelimelerin birlestirilmesi ---
#
# Bu testler GERCEK bir kosudan geliyor: ASR "Turkiye'nin"i iki parca
# olarak olctu ("Turkiye" + "'nin"), sayilar tutmadi ve esleme hic
# devreye girmedi. Turkce'de neredeyse her ozel isim kesme isareti
# aliyor, yani esleme pratikte hicbir zaman calismayacakti.


def test_kesme_isaretli_kelime_birlestirilir():
    measured = to_timings([RawWord("Türkiye", 1.06, 1.4), RawWord("'nin", 1.4, 1.68)])

    matched = match_expected(measured, "Türkiye'nin")

    assert [w.word for w in matched] == ["Türkiye'nin"]
    # Zamanlama ilk parcanin basindan son parcanin sonuna kadar.
    assert matched[0].start_ms == 1060
    assert matched[0].end_ms == 1680


def test_sayi_birden_cok_kelimeye_karsilik_gelebilir():
    """Metinde "1453" yaziyor, model "bin dort yuz elli uc" duyuyor."""
    measured = to_timings([
        RawWord("bin", 0, 0.4),
        RawWord("dört", 0.4, 0.8),
        RawWord("yüz", 0.8, 1.1),
        RawWord("elli", 1.1, 1.5),
        RawWord("üç", 1.5, 1.9),
    ])

    # Duyulan kelimeler beklenen yazimla ORTUSMUYOR: esleme
    # yapilamiyor ve olcum oldugu gibi donuyor. Bu dogru davranis -
    # normalizasyonun hangi sozcugun hangilerine acildigini bildirmesi
    # gerekir, o bilgi olmadan zorla eslestirme altyaziyi kaydirir.
    assert len(match_expected(measured, "1453")) == 5


def test_birlesen_kelimenin_guveni_en_dusuk_parcanin():
    measured = to_timings([
        RawWord("Türkiye", 0, 1, 0.95),
        RawWord("'nin", 1, 2, 0.40),
    ])

    assert match_expected(measured, "Türkiye'nin")[0].confidence == pytest.approx(0.40)


def test_olculen_kelimeler_artarsa_esleme_reddedilir():
    """Metin bitmis ama seste hala kelime varsa metin ile ses ortusmuyor."""
    measured = to_timings([RawWord("bir", 0, 1), RawWord("iki", 1, 2), RawWord("uc", 2, 3)])

    assert len(match_expected(measured, "bir iki")) == 3


def test_bircok_kelime_birlestirme_ayni_cumlede():
    measured = to_timings([
        RawWord("Göbekli", 0, 0.54),
        RawWord("Tepe,", 0.54, 0.78),
        RawWord("Türkiye", 1.06, 1.4),
        RawWord("'nin", 1.4, 1.68),
        RawWord("Güneydoğu", 1.68, 2.26),
    ])

    matched = match_expected(measured, "Göbekli Tepe, Türkiye'nin Güneydoğu")

    assert [w.word for w in matched] == ["Göbekli", "Tepe,", "Türkiye'nin", "Güneydoğu"]


def test_bos_olcum_veya_bos_metin_cokmez():
    assert match_expected([], "bir metin") == []
    assert len(match_expected(to_timings([RawWord("bir", 0, 1)]), "")) == 1


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
