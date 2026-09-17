# Design workspace — remaining authoring problem (2026-09-17)

**Main issue now:** stage navigation is corrected (Research Studio link + Design→Build→Validate→Run). The remaining problem is **Design still treating rules as a side panel** while chat/templates dominate — and whether inputs share one strategy definition.

**North star for this fix:** Make the **rule editor the Design center** and prove every path edits the **same** working draft.

## Audit corrections (do not act on outdated claims)

| Claim | Assessment |
|-------|------------|
| Research still a numbered stage | **Fixed visually** — Studio link; numbered stages are Design→Build→Validate→Run |
| Six internal stages prove old workflow | Not necessarily — legacy enum/bindings may be compatibility; do **not** renumber persisted enums to force count=4 |
| PENDING vs OPTIONAL | Different: PENDING = progress; OPTIONAL = requirement policy |
| B/C/N buttons | Presence ≠ forced use; defect only if save/handoff requires labels |
| Partials/latency “available” | UI gates ≠ liquidity-aware engine proven |

Compare **same commit/build** before changing tests or declaring defects fixed.

## Code audit: “Use rules as request” / Promote

**Traced:** `PromoteDesignRulesToRequest` copies the five Design text fields into `Composer` as a **Hyperion prompt string**. It does **not** update a separate structured TradeIR. **Send may reinterpret.**

| Path | Behavior |
|------|----------|
| Design fields | Working draft (authoritative for Review) |
| **Review strategy** | Confirms fields as draft; no LLM |
| **Ask Hyperion from these rules** | Prompt copy only — may reinterpret on Send |

## Layout target (in progress)

| Area | Purpose |
|------|---------|
| Left | Strategies / versions (rail) |
| Center | Rules, unresolved items, optional linked research (**star column**) |
| Right | Hyperion (narrow Auto) |
| Top | Research Studio link; Design→Build→Validate→Run |
| Main actions | **Review strategy**, then Build when ready |

## Acceptance example

Hyperion: “On a completed 5-minute bar, enter when EMA 20 crosses above EMA 50.”

Editor exposes: instrument (unresolved), data 5m bars, evaluation completed bar, condition EMA20×EMA50, sizing/exit/risk unresolved until set. Changing EMA 20→30 updates the draft; prior Validate evidence stays on the prior revision.
