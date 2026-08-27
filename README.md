# BytemountsAiStudio

AI destekli, sürekli çalışan, provider-bağımsız dijital içerik üretim platformu.
Bir ana konudan başlayıp araştırma → senaryo → seslendirme → görsel → render → yayın
zincirini paralel ve otomatik yürütür.

**Durum:** Faz 0 — iskelet kuruluyor. Henüz çalışan bir üretim hattı yok.

## Belgeler

| Belge | İçerik |
|---|---|
| [documents/](documents/) | İş emirleri, kararlar, sistem değerlendirmesi — insan girdisi |
| [docs/Icerik-Fabrikasi-Mimarisi.md](docs/Icerik-Fabrikasi-Mimarisi.md) | Mimari, ADR'ler, veri modeli, timeline şeması |
| [docs/IS-PLANI.md](docs/IS-PLANI.md) | Görev kırılımı — ilerlemenin tek gerçek kaynağı |
| [docs/plan-dashboard.html](docs/plan-dashboard.html) | Plandan üretilen ilerleme panosu (elle düzenlenmez) |

## Gereksinimler

| Araç | Sürüm | Not |
|---|---|---|
| .NET SDK | 10.0.400+ | `global.json` ile sabitlendi |
| FFmpeg / ffprobe | 7+ | PATH üzerinde erişilebilir olmalı |
| PostgreSQL | 16 + pgvector | Faz 0'da gerekli olacak (P0-02) |
| Python | 3.11–3.12 | Yalnızca `tools-sidecar` ve betikler için |

## Kurulum

```bash
dotnet build BytemountsAiStudio.slnx
dotnet test BytemountsAiStudio.slnx
```

## Proje yapısı

```
src/
  BytemountsAiStudio.Core             domain modelleri — hiçbir şeye bağımlı değil
  BytemountsAiStudio.Contracts        provider arayüzleri, node sözleşmeleri
  BytemountsAiStudio.Persistence      EF Core, migration, varlık deposu
  BytemountsAiStudio.Queue            iş kuyruğu: lease, retry, DLQ, adalet
  BytemountsAiStudio.Workflow         DAG engine, run durum makinesi
  BytemountsAiStudio.Media            render motorunun SAF katmanı (Planner/IR/Emitter)
  BytemountsAiStudio.Media.Rendering  yan etkili katman (FFmpeg, Skia, ffprobe)
  BytemountsAiStudio.Providers.Fake   deterministik sahte sağlayıcılar
  BytemountsAiStudio.Api              REST + SSE
  BytemountsAiStudio.Worker           kuyruk tüketen host
  BytemountsAiStudio.Cli              geliştirme ve işletme komutları
tests/
scripts/plan_progress.py             plandan pano üretir
```

**Bağımlılık yönü tek yönlüdür ve testle korunur.** `Media` saf katmanı dosya sistemine,
sürece veya ağa dokunamaz — `MediaPurityTests` bunu IL metadata'sında doğrular.

## İlerleme

```bash
python scripts/plan_progress.py
```

`docs/IS-PLANI.md` içindeki kutucuklar işaretlendikçe yüzdeler ve pano güncellenir.

## Solution formatı

`.slnx` kullanılıyor (.NET 10 SDK varsayılanı, XML tabanlı, temiz diff).
Visual Studio 17.13+ açar. Eski bir sürüm gerekirse klasik `.sln` üretilebilir.
