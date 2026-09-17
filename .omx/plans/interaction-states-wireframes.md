# Interaction states & wireframe specs

**Status:** Design target (2026-09-17) — real states, not only the populated happy path  
**Pairs with:** [`task-to-interface-map.md`](task-to-interface-map.md) · [`acceptance-journey-research-to-validate.md`](acceptance-journey-research-to-validate.md)

Wireframes below are **structural** (layout regions + copy). Visual polish follows after task completion works.

---

## Shared rules

- Hyperion is a **collapsible companion** (default collapsed or narrow on small windows).
- Disabled controls show **why** (tooltip + status line), not silent grey.
- Empty states name the **next action**, not a dead end.
- Restored session restores **instrument, period, indicators, finding**, and which Studio was active.

---

## R1 — Research: Markets / screener

```text
┌─ Research Studio ─────────────────────────────────────────┐
│ Markets │ Studies │ Findings │            [Hyperion ▾]   │
├──────────┬────────────────────────────────────────────────┤
│ Screener │ Results (table | chart grid*)                  │
│ universe │ coverage: requested / usable / excluded / out  │
│ metric   │ source: SIMULATED | live | …                   │
│ period   │ [Rank]  [Open chart]                           │
│ Top N    │                                                │
└──────────┴────────────────────────────────────────────────┘
* Chart grid only when real tiles exist; else hide/disable with reason.
```

| State | UI |
|-------|-----|
| **Empty** | “No screen yet. Choose universe, metric, period → Rank.” Open chart still available via Charts. |
| **Loading** | Rank disabled; “Ranking… shared window …” |
| **Success** | Ranked rows; coverage counts honest |
| **Unavailable data** | Row/exclusion reason; tools explain missing L2 etc. |
| **Invalid** | Bad period / empty universe → inline error, no fake Top N pad |
| **Restored** | Last query + selection restored |

---

## R2 — Research: study one chart

```text
┌─ Chart (dominant) ──────────────────┬─ Inspector (optional) ─┐
│ Instrument · TF · history · Views   │ Indicators / condition │
│ [candles + overlays]                │ Save finding 1 | 2     │
│ drawings · optional period select   │ Find similar           │
├─────────────────────────────────────┴────────────────────────┤
│ Status: instrument · source · tool hints                      │
│ Hyperion ▾ (collapsed by default on narrow layouts)           │
└──────────────────────────────────────────────────────────────┘
```

| State | UI |
|-------|-----|
| **Empty** | “Select an instrument” — Views disabled with reason |
| **Loading** | Chart skeleton; Views disabled until bars ready |
| **Success** | Series + overlays; Views enabled when instrument set |
| **Unavailable** | e.g. “Order book: no L2 for this source” |
| **Invalid condition** | Apply fails with field-level error; Find similar stays off |
| **Restored** | Symbol/TF/indicators/period from saved finding |

**Observation select:** highlight period only. No auto-outcome, no required B/C/N (optional labels only), no auto-open Builder.

---

## R3 — Research: compare (target)

```text
┌─ Chart A ──┐ ┌─ Chart B ──┐ ┌─ Chart C ──┐
│ shared TF  │ │            │ │            │
│ shared ind │ │            │ │            │
└────────────┴─┴────────────┴─┴────────────┘
┌─ Comparison (numeric) ───────────────────┐
│ volume / EMA diffs · failures listed     │
│ Hyperion cites these numbers             │
└──────────────────────────────────────────┘
```

| State | Local target |
|-------|----------------|
| **Not ready** | Mode disabled: “Chart compare not available yet — open charts one-by-one.” |
| **Success (future)** | N charts + table Hyperion can reference |

---

## R4 — Saved findings

```text
┌─ Findings ────────────────────────────────┐
│ Finding 1: summary | [Restore] [Notes]    │
│ Finding 2: summary | [Restore]            │
│ [Use in Strategy Builder]                 │
└───────────────────────────────────────────┘
```

| State | UI |
|-------|-----|
| **Empty** | “Finding 1: empty” / “Finding 2: empty” — save needs chart context + (for Builder) applied condition |
| **Success** | Summary with symbol, condition ver, binding count |
| **Restored** | Chart + inspector match saved package |

---

## B1 — Builder: Design (rule editor)

```text
┌─ Strategy Builder ── [Research Studio] (chrome link) ─────────────┐
│ 1 Design → 2 Build → 3 Validate → 4 Run                           │
├─ Design (default home) ───────────────────────────────────────────┤
│ Instruments · data inputs                                         │
│ Entry · Exit · Sizing · Risk · Orders                             │
│ Linked findings ▸ (optional strip)                                │
├─ Hyperion ▾ · optional templates (collapsed) ─────────────────────┤
└───────────────────────────────────────────────────────────────────┘
```

| State | UI |
|-------|-----|
| **Empty (Start from rules)** | Blank rule fields + placeholders; **not** “Open Research” as the hero |
| **Empty (Use saved research)** | Linked finding summary + rules still editable; missing fields highlighted |
| **Loading** | Build/Validate progress with cancellable status |
| **Success** | Rules saved on draft; Build shows artifact hash |
| **Invalid rule** | Error anchored to field (e.g. entry references unknown indicator) |
| **Stale finding** | Banner: “Linked finding’s EMA changed to 30 — strategy still uses EMA(20) until you update.” |
| **Restored session** | Same draft + linked finding versions |

**Remove / relocate:** research templates and “Open Research” as Design’s primary content. Research is a **navigation action**, not Design’s empty state.

---

## B2 — Validate

```text
┌─ Assumptions (L1 disclosed | richer unavailable) ─┐
│ Period · costs · fill model                        │
├─ Trades / equity ──────────────┬─ Trade chart ────┤
│ select trade → opens chart     │ same chart tools │
└────────────────────────────────┴──────────────────┘
```

| State | UI |
|-------|-----|
| **Unavailable fill option** | Queue/liquidity not selectable; partials + latency ms are applied when enabled |
| **Success** | Trades list; click → chart at trade |
| **Failure** | Compile/run error tied to version |

---

## Accessibility checklist (same journeys)

| Check | Pass when |
|-------|-----------|
| Keyboard | All primary actions reachable; no mouse-only Rank/Open/Save/Use in Builder |
| Focus visible | Focus ring on chart chrome, tables, rule fields |
| Chart selection | Period/brush has keyboard or explicit From–To fields |
| Disabled why | Screen reader / tooltip states reason |
| Cross-window | Focus restores when returning Studio ↔ Builder |
| Contrast | Status and disabled text meet readable contrast on dark theme |

Test the acceptance journey in [`acceptance-journey-research-to-validate.md`](acceptance-journey-research-to-validate.md) with these checks—not a separate abstract a11y pass.
