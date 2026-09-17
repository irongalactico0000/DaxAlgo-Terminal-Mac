# Acceptance journey — Research → Builder → Validate

**Status:** Runnable checklist (2026-09-17)  
**Measures:** task completion, not guessed success rates  
**Maps to:** [`task-to-interface-map.md`](task-to-interface-map.md) · [`interaction-states-wireframes.md`](interaction-states-wireframes.md)

Run on Mac against a build from current `main`. Record pass/fail + screenshot or short note per step.  
Include **keyboard** pass where marked [a11y].

---

## Journey A — Research chart & finding (core)

| # | Step | Expected | Pass? |
|---|------|----------|-------|
| A1 | Open Research Studio | Studio window; chart area usable; Hyperion not crushing chart | |
| A2 | Rank (e.g. S&P 100, estimated traded value, Top 10) | Table + honest coverage/source; no fake pad | |
| A3 | Open chart on ranked #1 **without** needing Rank for tools | Chart shows that symbol; Hyperion context matches | |
| A4 | Enable EMA 20 (and volume if available) on **chart** tools | Series visible; inspectable settings | |
| A5 | Open Order book / Footprint / Bookmap from chart Views | Opens for **chart** symbol; or clear data-unavailable reason | |
| A6 | Select an observation period only | Period highlighted; **no** invented outcome; **no** forced B/C/N; **no** auto Builder | |
| A7 | Apply a condition (e.g. volume ≥ k×avg) | Version shown; Find similar enabled | |
| A8 | Find similar | Hits for **that** condition — not silent next-day +5% | |
| A9 | Save finding 1 | Summary shows symbol/condition/bindings | |
| A10 | Change chart / save finding 2; Restore finding 1 | Finding 1 package restored distinctly | |
| A11 [a11y] | Keyboard: Rank → select row → Open chart → Save finding | Completes without mouse | |

---

## Journey B — Compare (MVP: numeric strip + focus chart)

| # | Step | Expected | Pass? |
|---|------|----------|-------|
| B1 | Select ≥2 ranked symbols (Select on tiles) | Selection set grows; Compare enabled | |
| B2 | Open Compare | Numeric strip (return %, vol÷avg, EMA20 slope) for each selected case; open a tile to focus the live chart | |
| B3 | Ask Hyperion what changed in volume/EMA before breakouts | Answer cites the visible comparison numbers/focus chart | |

Real multi-chart tiles: Compare hosts up to **3 live chart tiles** + numeric strip; open a ranked tile for the single focus chart. If tiles fail to load, strip + focus chart alone is still **PASS (MVP)**.

---

## Journey C — Builder rules from finding

| # | Step | Expected | Pass? |
|---|------|----------|-------|
| C1 | From finding 1: Use in Strategy Builder | Builder opens; **same session**; finding linked | |
| C2 | Design screen | **Editable rules** visible (entry/exit/size/risk/orders)—not research templates as home | |
| C3 | Confirm linked indicator def (e.g. EMA 20) | Exact params shown under research inputs | |
| C4 | Write entry using that def (e.g. close crosses above EMA 20) | Rule editable; indicator ≠ full strategy | |
| C5 | Add exit + sizing | Entry condition id/hash unchanged | |
| C6 | Return to Research Studio from Builder | Correct session Studio; finding intact | |
| C7 | Use in Builder **again** | Second handoff works (not one-shot) | |
| C8 [a11y] | Focus returns to a sensible control after window switch | Visible focus | |

---

## Journey D — Build & Validate (honest fidelity)

| # | Step | Expected | Pass? |
|---|------|----------|-------|
| D1 | Build | Compile result; errors tied to rules if any | |
| D2 | Validate assumptions | L1 applied; optional quantity-capped partials (max 4/touch) + latency ms; queue/liquidity unavailable **with explanation** | |
| D3 | Run validation | Trades/performance for the version | |
| D4 | Select one trade | Chart opens for that trade context | |
| D5 | Change research EMA to 30; reopen Builder | Strategy still on prior EMA 20 until user updates link (stale banner if implemented) | |

---

## Journey E — Direct Builder (no research)

| # | Step | Expected | Pass? |
|---|------|----------|-------|
| E1 | Open Strategy Builder → Start from rules (or equivalent) | Design rule editor without forcing Research | |
| E2 | Optional: Open Research from Builder | Studio for investigation; return with or without a finding | |

---

## Sign-off

| | |
|--|--|
| Build / commit | |
| Operator | |
| Date | |
| A (Research) | PASS / FAIL |
| B (Compare) | PASS / FAIL / BLOCKED |
| C (Builder) | PASS / FAIL |
| D (Validate) | PASS / FAIL |
| E (Direct rules) | PASS / FAIL |
| Notes | |

**Done means:** A + C + D pass on Mac with notes. B = strip + up to 3 compare chart tiles (focus chart still available). E must pass so known-rules users are not forced through Research.
