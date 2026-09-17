# Design workspace — remaining authoring problem (2026-09-17)

**Main issue (addressed in UI):** stage navigation is corrected (Research Studio link + Design→Build→Validate→Run). Design center is the **rule editor**; Hyperion is a narrow prompt column. Hyperion replies must be **Accepted** before they overwrite Design fields.

**North star:** Every path edits the **same** working draft (instrument, timeframe, evaluation, entry/exit/sizing/risk/orders).

## Audit corrections (do not act on outdated claims)

| Claim | Assessment |
|-------|------------|
| Research still a numbered stage | **Fixed visually** — Studio link; numbered stages are Design→Build→Validate→Run |
| Six internal stages prove old workflow | Not necessarily — legacy enum/bindings may be compatibility; do **not** renumber persisted enums to force count=4 |
| PENDING vs OPTIONAL | Different: PENDING = progress; OPTIONAL = requirement policy |
| B/C/N buttons | Presence ≠ forced use; defect only if save/handoff requires labels |
| Partials/latency “available” | UI gates ≠ liquidity-aware engine proven |

Compare **same commit/build** before changing tests or declaring defects fixed.

## Code audit: Promote vs Accept

**Traced:** `PromoteDesignRulesToRequest` copies Design fields into `Composer` as a **Hyperion prompt string**. It does **not** update Design fields or invent TradeIR. **Send may reinterpret.**

| Path | Behavior |
|------|----------|
| Design fields | Working draft (authoritative for Review / Build intent) |
| **Review strategy** | Confirms fields as draft; no LLM |
| **Ask Hyperion from these rules** | Prompt copy only — Design unchanged |
| **Stage last Hyperion reply** | Loads assistant text into pending proposal panel |
| **Accept / Discard** | Accept writes keyed lines into Design fields; Discard keeps draft |

Unresolved checklist surfaces missing instrument / timeframe / evaluation / rules before Build.

## Layout target

| Area | Purpose |
|------|---------|
| Left | Strategies / versions (rail) |
| Center | Rules, unresolved checklist, optional linked research, Hyperion proposal review (**star column**) |
| Right | Hyperion (narrow Auto) |
| Top | Research Studio link; Design→Build→Validate→Run |
| Main actions | **Review strategy**, then Build when ready |

## Acceptance example

Hyperion: “On a completed 5-minute bar, enter when EMA 20 crosses above EMA 50.”

Editor exposes: instrument (unresolved until set), data 5m bars, evaluation completed bar, condition EMA20×EMA50, sizing/exit/risk unresolved until set. Changing EMA 20→30 updates the draft; prior Validate evidence stays on the prior revision. Hyperion text does not replace fields until Accept.
