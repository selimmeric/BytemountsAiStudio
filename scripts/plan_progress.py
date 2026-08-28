#!/usr/bin/env python3
"""IS-PLANI.md'yi okur, ilerlemeyi hesaplar ve panoyu yeniden uretir.

Kullanim:
    python scripts/plan_progress.py            # panoyu guncelle + ozeti yazdir
    python scripts/plan_progress.py --check    # sadece yazdir, dosya yazma

Plan dosyasi tek gercek kaynaktir; bu betik ondan turetir. Gorev satiri bicimi:
    - [ ] **P0-01** `3p` — Baslik
"""
from __future__ import annotations

import argparse
import html
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PLAN = ROOT / "docs" / "IS-PLANI.md"
OUT = ROOT / "docs" / "plan-dashboard.html"

# Panonun yayimlanmis kopyasi. Depodaki dosya ile ayni icerik ama ayri
# bir yerde duruyor; birini guncellemek digerini guncellemiyor.
ARTIFACT_URL = "https://claude.ai/code/artifact/d8782ccd-ad1e-450d-8b16-bbbe726dc647"

MVP_PHASES = {0, 1, 2}

PHASE_RE = re.compile(r"^##\s+Faz\s+(\d+)\s+[—-]\s+(.+?)\s*$")
GROUP_RE = re.compile(r"^###\s+(\d+)\.([A-Z])\s+(.+?)\s*$")
# Gorev kodu: P<faz>-<sira> + istege bagli harf soneki (P0-01b gibi bolunmus gorevler).
# `~` = BASLADI ama bitmedi.
#
# Ayri bir isaret gerekiyordu cunku ilk hal boyle bir gorevi hic
# taniyamiyor ve gorevi plandan DUSURUYORDU: toplam puan azaliyor,
# yuzde yukseliyordu. Yarim bir is paydada kalmali.
TASK_RE = re.compile(r"^- \[([ xX~])\]\s+\*\*(P\d+-\d+[a-z]?)\*\*\s+`(\d+)p`\s+[—-]\s+(.+?)\s*$")


@dataclass
class Task:
    code: str
    title: str
    points: int
    done: bool
    group: str
    milestone: bool = False

    # Basladi ama bitmedi. Puan KAZANDIRMIYOR - yarim is, is degil -
    # ama panoda ayri gorunuyor: neyin durdugu ile neye hic
    # baslanmadigi farkli sorular.
    partial: bool = False


@dataclass
class Phase:
    number: int
    title: str
    tasks: list[Task] = field(default_factory=list)

    @property
    def total(self) -> int:
        return sum(t.points for t in self.tasks)

    @property
    def earned(self) -> int:
        return sum(t.points for t in self.tasks if t.done)

    @property
    def pct(self) -> float:
        return 100.0 * self.earned / self.total if self.total else 0.0


def parse(path: Path) -> list[Phase]:
    phases: list[Phase] = []
    group = ""
    for line in path.read_text(encoding="utf-8").splitlines():
        if m := PHASE_RE.match(line):
            phases.append(Phase(int(m.group(1)), m.group(2)))
            group = ""
            continue
        if m := GROUP_RE.match(line):
            group = m.group(3)
            continue
        if m := TASK_RE.match(line):
            if not phases:
                raise SystemExit(f"Faz basligi olmadan gorev: {line}")
            title = m.group(4)
            phases[-1].tasks.append(
                Task(
                    code=m.group(2),
                    title=title,
                    points=int(m.group(3)),
                    done=m.group(1).lower() == "x",
                    partial=m.group(1) == "~",
                    group=group,
                    milestone="🏁" in title,
                )
            )
    if not phases:
        raise SystemExit("Plan dosyasinda hic faz bulunamadi.")
    return phases


def pct(earned: int, total: int) -> float:
    return 100.0 * earned / total if total else 0.0


def inline(text: str) -> str:
    """Gorev basligini HTML'e cevirir: once kacis, sonra `kod` -> <code>."""
    escaped = html.escape(text)
    escaped = re.sub(r"`([^`]+)`", r"<code>\1</code>", escaped)
    return re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", escaped)


# --------------------------------------------------------------------------- HTML

CSS = """
:root{
  --ground:#EDF2F7; --surface:#FFFFFF; --surface-2:#F6F9FB;
  --ink:#0C2337; --ink-soft:#4A647C; --ink-faint:#7E97AC;
  --rule:#C6D6E3; --signal:#1B6FA8; --signal-soft:#DCEAF4;
  --done:#2E7D66; --pending:#B7C9D7; --milestone:#B0741C;
  --grid:rgba(27,111,168,.06); --shadow:0 1px 2px rgba(12,35,55,.06);
}
@media (prefers-color-scheme:dark){
  :root:not([data-theme="light"]){
    --ground:#0A1A26; --surface:#0E2433; --surface-2:#122B3C;
    --ink:#DCE9F2; --ink-soft:#93AEC2; --ink-faint:#6A8AA1;
    --rule:#1E3E54; --signal:#57AEDD; --signal-soft:#153347;
    --done:#4CBF9A; --pending:#2C4C63; --milestone:#D9A24A;
    --grid:rgba(87,174,221,.05); --shadow:0 1px 2px rgba(0,0,0,.3);
  }
}
:root[data-theme="dark"]{
  --ground:#0A1A26; --surface:#0E2433; --surface-2:#122B3C;
  --ink:#DCE9F2; --ink-soft:#93AEC2; --ink-faint:#6A8AA1;
  --rule:#1E3E54; --signal:#57AEDD; --signal-soft:#153347;
  --done:#4CBF9A; --pending:#2C4C63; --milestone:#D9A24A;
  --grid:rgba(87,174,221,.05); --shadow:0 1px 2px rgba(0,0,0,.3);
}
*{box-sizing:border-box}
body{
  margin:0; background:var(--ground); color:var(--ink);
  font-family:"IBM Plex Sans",system-ui,-apple-system,Segoe UI,sans-serif;
  font-size:15px; line-height:1.55;
  background-image:linear-gradient(var(--grid) 1px,transparent 1px),
                   linear-gradient(90deg,var(--grid) 1px,transparent 1px);
  background-size:28px 28px;
}
.wrap{max-width:1080px;margin:0 auto;padding:40px 24px 72px;display:flex;flex-direction:column;gap:32px}
h1,h2,h3{font-family:"IBM Plex Sans Condensed","IBM Plex Sans",sans-serif;font-weight:600;
  text-wrap:balance;margin:0;letter-spacing:.01em}
h1{font-size:30px;line-height:1.15}
.eyebrow{font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:var(--ink-faint);
  font-family:"IBM Plex Mono",ui-monospace,monospace}
.num{font-family:"IBM Plex Mono",ui-monospace,monospace;font-variant-numeric:tabular-nums}
.stamp{margin-top:6px;font-size:11px;color:var(--ink-faint);
  font-family:"IBM Plex Mono",ui-monospace,monospace}

/* ---- ust panel ---- */
.head{background:var(--surface);border:1px solid var(--rule);border-radius:4px;
  box-shadow:var(--shadow);padding:28px 28px 24px;display:flex;flex-direction:column;gap:22px}
.head-top{display:flex;flex-wrap:wrap;gap:20px;align-items:flex-start;justify-content:space-between}
.scores{display:flex;flex-wrap:wrap;gap:36px}
.score{display:flex;flex-direction:column;gap:2px}
.score .v{font-family:"IBM Plex Mono",ui-monospace,monospace;font-variant-numeric:tabular-nums;
  font-size:44px;line-height:1;font-weight:600;color:var(--signal)}
.score .v.sub{font-size:26px;color:var(--ink)}
.score .l{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--ink-faint);
  font-family:"IBM Plex Mono",ui-monospace,monospace}
.bar{height:10px;background:var(--pending);border-radius:2px;overflow:hidden}
.bar>i{display:block;height:100%;background:var(--signal);border-radius:2px}
.legend{display:flex;flex-wrap:wrap;gap:18px;font-size:12px;color:var(--ink-soft)}
.legend span{display:inline-flex;align-items:center;gap:7px}
.dot{width:9px;height:9px;border-radius:2px;display:inline-block}

/* ---- faz kartlari ---- */
.phases{display:grid;grid-template-columns:repeat(auto-fill,minmax(310px,1fr));gap:14px}
.card{background:var(--surface);border:1px solid var(--rule);border-radius:4px;
  box-shadow:var(--shadow);padding:16px 18px;display:flex;flex-direction:column;gap:11px}
.card.mvp{border-left:3px solid var(--signal)}
.card h3{font-size:16px}
.card .meta{display:flex;justify-content:space-between;align-items:baseline;gap:10px}
.card .pctv{font-family:"IBM Plex Mono",ui-monospace,monospace;font-variant-numeric:tabular-nums;
  font-size:20px;font-weight:600}
.card .pts{font-size:12px;color:var(--ink-faint);font-family:"IBM Plex Mono",ui-monospace,monospace}
.ticks{display:flex;gap:2px;flex-wrap:wrap}
.ticks i{width:9px;height:16px;border-radius:1px;background:var(--pending);display:block}
.ticks i.d{background:var(--done)}
.ticks i.p{background:var(--milestone)}
.row.partial .box{color:var(--milestone)}
.row.partial .c{color:var(--milestone)}
.ticks i.m{background:var(--pending);outline:1px solid var(--milestone);outline-offset:-1px}
.ticks i.m.d{background:var(--milestone)}

/* ---- gorev listesi ---- */
.section{display:flex;flex-direction:column;gap:10px}
.tasks{background:var(--surface);border:1px solid var(--rule);border-radius:4px;
  box-shadow:var(--shadow);overflow:hidden}
.grouphdr{padding:9px 16px;background:var(--surface-2);border-bottom:1px solid var(--rule);
  font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--ink-faint);
  font-family:"IBM Plex Mono",ui-monospace,monospace}
.row{display:grid;grid-template-columns:22px 74px 1fr auto;gap:12px;align-items:baseline;
  padding:9px 16px;border-bottom:1px solid var(--rule);font-size:14px}
.row:last-child{border-bottom:0}
.row .box{font-family:"IBM Plex Mono",ui-monospace,monospace;color:var(--pending);font-size:13px}
.row.done .box{color:var(--done)}
.row.done .t{color:var(--ink-soft);text-decoration:line-through;text-decoration-color:var(--pending)}
.row .c{font-family:"IBM Plex Mono",ui-monospace,monospace;font-size:12.5px;color:var(--ink-faint)}
.row.done .c{color:var(--done)}
.row .p{font-family:"IBM Plex Mono",ui-monospace,monospace;font-size:12px;color:var(--ink-faint);
  font-variant-numeric:tabular-nums}
.row.ms .t{font-weight:600}
.row .t code{font-family:"IBM Plex Mono",ui-monospace,monospace;font-size:12.5px;
  background:var(--signal-soft);color:var(--ink-soft);padding:1px 5px;border-radius:3px}
.foot{font-size:12.5px;color:var(--ink-faint);text-align:center}
@media (max-width:620px){
  .row{grid-template-columns:20px 1fr auto;gap:9px}
  .row .c{grid-column:2;font-size:11.5px}
  .row .t{grid-column:2}
  .score .v{font-size:34px}
}
"""


def render_html(phases: list[Phase]) -> str:
    # Uretim damgasi: pano guncel mi degil mi TARAYICIDAN anlasilabilsin.
    # Damga olmadan onbellekten gelen eski bir kopya ile taze bir kopya
    # birbirinin ayni gorunuyor - "pano ilerlemiyor" sikayetinin sebebi
    # buydu, sayilar dogruydu ama tazeligi soyleyen bir sey yoktu.
    stamp = datetime.now().strftime("%d.%m.%Y %H:%M")

    total = sum(p.total for p in phases)
    earned = sum(p.earned for p in phases)
    mvp = [p for p in phases if p.number in MVP_PHASES]
    mvp_total = sum(p.total for p in mvp)
    mvp_earned = sum(p.earned for p in mvp)
    all_tasks = [t for p in phases for t in p.tasks]
    done_tasks = [t for t in all_tasks if t.done]

    o = pct(earned, total)
    m = pct(mvp_earned, mvp_total)

    cards = []
    for p in phases:
        ticks = "".join(
            f'<i class="{"m " if t.milestone else ""}{"d" if t.done else ""}" title="{html.escape(t.code)}"></i>'
            for t in p.tasks
        )
        cards.append(
            f'<article class="card{" mvp" if p.number in MVP_PHASES else ""}">'
            f'<div class="eyebrow">Faz {p.number}</div>'
            f"<h3>{html.escape(p.title)}</h3>"
            f'<div class="meta"><span class="pctv">{p.pct:.0f}<span style="font-size:13px">%</span></span>'
            f'<span class="pts">{p.earned}/{p.total} p · {sum(1 for t in p.tasks if t.done)}/{len(p.tasks)} görev</span></div>'
            f'<div class="ticks">{ticks}</div>'
            "</article>"
        )

    sections = []
    for p in phases:
        rows = []
        current = None
        for t in p.tasks:
            if t.group != current:
                current = t.group
                if current:
                    rows.append(f'<div class="grouphdr">{html.escape(current)}</div>')
            cls = " ".join(x for x in (
                "row",
                "done" if t.done else ("partial" if t.partial else ""),
                "ms" if t.milestone else "") if x)
            box = "▣" if t.done else ("◧" if t.partial else "▢")
            rows.append(
                f'<div class="{cls}"><span class="box">{box}</span>'
                f'<span class="c">{html.escape(t.code)}</span>'
                f'<span class="t">{inline(t.title)}</span>'
                f'<span class="p">{t.points}p</span></div>'
            )
        sections.append(
            '<section class="section">'
            f'<h2>Faz {p.number} — {html.escape(p.title)} '
            f'<span class="num" style="color:var(--ink-faint);font-size:15px">· {p.pct:.0f}%</span></h2>'
            f'<div class="tasks">{"".join(rows)}</div>'
            "</section>"
        )

    return f"""<title>Fabrika İlerleme Panosu</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;600&family=IBM+Plex+Sans+Condensed:wght@600&family=IBM+Plex+Sans:wght@400;600&display=swap">
<style>{CSS}</style>
<div class="wrap">
  <header class="head">
    <div class="head-top">
      <div>
        <div class="eyebrow">İçerik Fabrikası · İş Planı</div>
        <h1>İlerleme Panosu</h1>
        <div class="stamp">{stamp} · {len(done_tasks)} görev tamam</div>
      </div>
      <div class="scores">
        <div class="score"><span class="v">{o:.0f}<span style="font-size:20px">%</span></span>
          <span class="l">Tüm plan</span></div>
        <div class="score"><span class="v sub">{m:.0f}<span style="font-size:15px">%</span></span>
          <span class="l">MVP (Faz 0–2)</span></div>
        <div class="score"><span class="v sub">{len(done_tasks)}<span style="font-size:15px">/{len(all_tasks)}</span></span>
          <span class="l">Görev</span></div>
        <div class="score"><span class="v sub">{earned}<span style="font-size:15px">/{total}</span></span>
          <span class="l">Puan</span></div>
      </div>
    </div>
    <div class="bar"><i style="width:{o:.2f}%"></i></div>
    <div class="legend">
      <span><i class="dot" style="background:var(--done)"></i>Tamamlandı</span>
      <span><i class="dot" style="background:var(--milestone)"></i>Başladı, bitmedi</span>
      <span><i class="dot" style="background:var(--pending)"></i>Bekliyor</span>
      <span><i class="dot" style="background:var(--milestone)"></i>Kilometre taşı</span>
      <span>Sol kenarı çizgili kartlar MVP kapsamındadır</span>
    </div>
  </header>

  <div class="phases">{"".join(cards)}</div>

  {"".join(sections)}

  <p class="foot">docs/IS-PLANI.md dosyasından üretildi · güncellemek için <span class="num">python scripts/plan_progress.py</span></p>
</div>
"""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="dosya yazma, sadece ozet")
    args = ap.parse_args()

    # Windows konsolu varsayilan olarak cp1252; Turkce faz adlari bozulmasin.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    phases = parse(PLAN)
    total = sum(p.total for p in phases)
    earned = sum(p.earned for p in phases)
    mvp_total = sum(p.total for p in phases if p.number in MVP_PHASES)
    mvp_earned = sum(p.earned for p in phases if p.number in MVP_PHASES)

    width = 34
    print(f"\n  {'FAZ':<34} {'%':>5}  {'PUAN':>9}  GOREV")
    print("  " + "-" * 62)
    for p in phases:
        filled = round(p.pct / 100 * 12)
        bar = "#" * filled + "." * (12 - filled)
        name = f"{p.number} {p.title}"[:width]
        done_n = sum(1 for t in p.tasks if t.done)
        print(f"  {name:<34} {p.pct:>4.0f}%  {p.earned:>4}/{p.total:<4}  {done_n}/{len(p.tasks)}  {bar}")
    print("  " + "-" * 62)
    print(f"  {'MVP (Faz 0-2)':<34} {pct(mvp_earned, mvp_total):>4.0f}%  {mvp_earned:>4}/{mvp_total}")
    print(f"  {'TUM PLAN':<34} {pct(earned, total):>4.0f}%  {earned:>4}/{total}\n")

    if not args.check:
        OUT.write_text(render_html(phases), encoding="utf-8")
        print(f"  Pano guncellendi: {OUT.relative_to(ROOT)}")
        # YAYIMLANMIS KOPYA AYRI BIR SEY: bu dosyayi yazmak, tarayicidan
        # bakilan panoyu guncellemiyor. Ikisi bir gun ayristi ve pano
        # gunlerce eski sayilari gosterdi -- kimse fark etmedi, cunku
        # yanlis oldugunu soyleyen hicbir sey yoktu.
        print(f"  Yayimlanmis panoyu da guncelle: {ARTIFACT_URL}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
