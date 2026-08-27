# documents/ — Kaynak belgeler ve değerlendirmeler

Bu klasör **insandan gelen girdiyi** ve **onun üzerine yazılan değerlendirmeleri** tutar. Türetilmiş teknik belgeler burada değil, `docs/` altındadır.

| Dosya | İçerik |
|---|---|
| [01-is-emri-orijinal.md](01-is-emri-orijinal.md) | Projenin ilk ve kapsamlı iş emri (27 Ağustos 2026) — birebir, değiştirilmeden |
| [02-kararlar-ve-ek-talimatlar.md](02-kararlar-ve-ek-talimatlar.md) | Sonraki talimatlar, sorulan sorular ve verilen cevaplar — tarih sırasıyla |
| [03-sistem-degerlendirmem.md](03-sistem-degerlendirmem.md) | Sistem hakkındaki teknik değerlendirmem: güçlü yanlar, riskler, itirazlar, tavsiyeler |

## Klasör ayrımı

```
documents/   ← insan girdisi + değerlendirme      (bu klasör)
docs/        ← türetilmiş teknik belgeler
             ├── Icerik-Fabrikasi-Mimarisi.md     mimari, ADR'ler, veri modeli
             ├── IS-PLANI.md                      görev kırılımı, ilerleme kaynağı
             └── plan-dashboard.html              plandan üretilen pano (elle düzenlenmez)
```

## Kural

Yeni bir iş emri, açıklama veya karar geldiğinde: **önce buraya yazılır**, sonra `docs/` altındaki teknik belgeler ona göre güncellenir. Ters yönde çalışılmaz — teknik belgeler kaynağını buradan alır.
