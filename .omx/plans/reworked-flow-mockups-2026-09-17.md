# Reworked flow — Research Studio × Strategy Builder (mockups)

**Status:** Align-before-code (2026-09-17) — choice **(b)** from product audit  
**Audience:** Decide target UX before more layout/rename/stage surgery  
**Inputs:** TradingView Strategy Report · QuantConnect applying-research · Composer create · TrendSpider Strategy Tester · Option Alpha · MT5 · Nautilus (fidelity later)  
**Local shipped already:** Compare MVP (numeric strip) · Validate partials/latency · Design rule fields · Finding 1/2 copy on Studio

---

## Decision this doc asks for

Approve this **target flow** (or mark deltas). After approval, implement in order:

1. Finish **finding** rename in code (IDs still `A`/`B` under the hood OK)  
2. **Unify stages** (Builder rail vs Studio; kill Brief-as-peer confusion)  
3. **Layout pass** (clipping + duplicate Open Research / Rank clusters) — old (c)

Do **not** start multi-chart tiles or Nautilus queue/L2 until this flow is accepted.

---

## 1. Product spine (locked)

```text
Research Studio                         Strategy Builder
─────────────────                       ────────────────
Find · Study · Compare · Save finding   Design rules · Build · Validate · Run
         │                                         ▲
         └── Use in Strategy Builder ──────────────┘
              (finding package links; does not invent the strategy)
```

| External lesson | Our application |
|-----------------|-----------------|
| QC: research → apply to backtest | Finding → Builder rules → Validate (same version hash) |
| Composer / TV: author then report | Design rules first; Validate is a report, not Design home |
| TrendSpider / OA: tester assumptions explicit | Validate checklist = applied fills only |
| Nautilus | Later fidelity lane — never claimed on L1 UI |

---

## 2. Stage model (unify)

### Today (confused)

| Surface | What users see | Problem |
|---------|----------------|---------|
| Builder rail | `1 Research Studio → 2 Design → 3 Build → 4 Validate → 5 Run` | Research is a **place**, not a Builder stage; looks sequential-mandatory |
| Enum | `Brief`, `Research`, `Design`, `Build`, `Validate`, `Paper` | Brief still exists; Research stage vs Studio shell overlap |
| Code slots | `ReferenceA` / finding copy mixed | Rename incomplete |

### Target

**Research Studio** — own window; tabs only (no Builder stage numbers):

```text
[ Markets ] [ Chart ] [ Findings ]     Hyperion ▾
```

**Strategy Builder** — stages only (Research is a link, not stage 1):

```text
[ Design ] → [ Build ] → [ Validate ] → [ Run ]
     ↑
  [ Open Research Studio ]   (nav chrome, not a numbered stage)
  Linked finding: Finding 1 · …   [Restore] [Clear link]
```

| Stage | Job | Entry |
|-------|-----|--------|
| **Design** | Editable entry / exit / sizing / risk / orders | Default home; or “Use saved research” |
| **Build** | Generate/edit code, compile, register | After rules confirmable |
| **Validate** | Historical replay + applied L1 fidelity | Bound revision |
| **Run** | Paper/Live book | After validation evidence (policy as today) |

**Brief** — demote to optional project metadata under Design (not a rail button).

**Working-flow map text** — rewrite from “1 Research → 2 Design…” to “Research (optional) · Design → Build → Validate → Run”.

---

## 3. Mockups (structural)

### M1 — Research Studio home (Markets)

```text
┌─ Research Studio ──────────────────────────────── Hyperion [Show] ─┐
│ [Markets]  Chart  Findings                                          │
├─ Filters ───────────────────────────────────────────────────────────┤
│ Universe · Metric · Bar size · Lookback · Top N                     │
│ [Rank]                                                              │
├─ Results ───────────────────────────────────────────────────────────┤
│ Table | Compare | Detail                                            │
│ coverage · exclusions · source                                      │
│ rows…  [Select] [Open chart]                                        │
│ Compare MVP: numeric strip (ret · vol÷avg · EMAΔ) + focus chart tip │
└─────────────────────────────────────────────────────────────────────┘
```

**One Rank cluster only** (no second Rank in details drawer).  
**Compare** = strip + focus chart until real tiles ship (label stays Compare, never “Chart grid”).

### M2 — Research Chart

```text
┌─ Chart (dominant) ──────────────┬─ Tools (on chart) ─┐
│ Symbol · TF · Views             │ Indicators · OB ·  │
│                                 │ Footprint · Bookmap│
│ period select = observation only│                    │
├─────────────────────────────────┴────────────────────┤
│ Condition · Find similar · Save finding 1 | 2        │
│ [Use in Strategy Builder]                            │
└──────────────────────────────────────────────────────┘
```

No B/C/N forced (optional ghost labels only). No auto Builder.

### M3 — Findings

```text
Finding 1  summary…  [Restore] [Notes]
Finding 2  summary…  [Restore]
[Use in Strategy Builder]
```

User-facing: **Finding 1 / Finding 2** only. Internal key may stay `A`/`B` until a rename PR.

### M4 — Builder Design (start)

```text
┌─ Strategy Builder ──── [Open Research Studio] ─────────────────────┐
│ Design · Build · Validate · Run                                     │
├─ Linked finding (optional strip) ───────────────────────────────────┤
│ Finding 1 · MSFT · EMA(50) · condition ver …   or “None — start blank”│
├─ Rules (always visible) ────────────────────────────────────────────┤
│ ENTRY …………………………………………………………………………………                             │
│ EXIT  …………………………………………………………………………………                             │
│ SIZING · RISK · ORDERS                                              │
├─ Hyperion (companion) ─ optional templates (collapsed) ─────────────┤
└─────────────────────────────────────────────────────────────────────┘
```

**Shipped MVP already matches the rules block.** Remaining: strip start chooser, collapse templates, remove duplicate “Open Research Studio” (hero + empty + rule panel → **one** chrome button).

### M5 — Validate

```text
Assumptions: L1 · partials max4 (opt) · latency ms (opt)
             queue / liquidity = unavailable (explained)
[Run historical validation] → trades → open trade on chart
```

---

## 4. Finding rename — finish list

| Layer | Today | Target |
|-------|-------|--------|
| Buttons / labels | Save finding 1/2 | Keep |
| Empty copy | Finding 1: empty | Keep |
| Commands / props | `SaveResearchReferenceA` | Rename to `…Finding1` when safe, or alias only |
| Tooltips | Still mention “Reference A/B” in one Studio tip | Delete that phrase |
| Docs / evidence filenames | Some V04 “Reference A/B” | Retitle in docs only |
| Handoff composer | “saved Research finding” | Keep |

**Acceptance:** `rg -i "reference a/b|Reference A"` on `*.axaml` and user-visible strings → zero.

---

## 5. Layout / duplicate clusters (deferred c)

| Issue | Target fix |
|-------|------------|
| Multiple “Open Research Studio” | Single chrome control on Builder |
| Rank + view mode repeated in Studio drawers | One Markets toolbar |
| Clipped Validate / Design headers on narrow Mac | Stage rail wraps; Hyperion collapses first |
| Design left empty + right rules both preach Research | Left = Hyperion/templates collapsed; right = rules |

---

## 6. What we will **not** claim yet

- Real multi-chart tiles (Compare MVP strip is enough until approved)  
- Queue / liquidity / L2 Validate  
- Design rules auto-compiling into TradeIR without Build  
- Fake success % or padded Rank  

---

## 7. Approval checklist

Reply with **yes** / deltas:

- [ ] Builder rail = Design→Build→Validate→Run; Research = separate Studio link  
- [ ] Brief off the rail  
- [ ] Finding 1/2 only in UI; finish rename sweep  
- [ ] Design home = rules (already); one Research CTA  
- [ ] Compare stays strip+focus until tiles  
- [ ] Next engineering sprint = (1) rename/stage unify (2) layout dedupe  

So: **approve the flow.** Stage-unify (item #2) is implemented: Builder rail is Design→Build→Validate→Run; Research is a de-numbered chrome link; FreshSession/UX contract lock the new model; B/C/N are optional (ghost).

**Shipped (2026-09-17):** stage-unify + Finding 1/2 API + single Research chrome CTA. Remaining: optional layout polish only.
