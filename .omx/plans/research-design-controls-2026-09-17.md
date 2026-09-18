# Research × Strategy Design — control contract (2026-09-17)

**Rule:** Navigation preserves work. Mutating actions (`Add finding`, `Accept` Hyperion, `Confirm link`, `Use template`) explicitly change the draft.

**Clickable proposal:** [`research-design-controls-proposal-2026-09-17.html`](research-design-controls-proposal-2026-09-17.html) (illustrative; does not modify the app).

## Central distinction

| Kind | Examples | Effect on draft |
|------|----------|-----------------|
| **Navigate** | ← Back to Momentum · Investigate in Research · Design / Build / Validate stages | Preserves Design fields, findings, Hyperion pending state (except staged Add-finding review clears on Back) |
| **Mutate** | Save finding · Add finding → Confirm link · Accept into Design · Use template · Discard Hyperion | Changes saved findings, linked research, or Design fields |

## Strategy Design controls

| Control | Acts on | Takes user | Mutates? |
|---------|---------|------------|----------|
| **Use template** | Starter catalog → Design fields | Stays on Design rule editor | **Yes** — seeds instrument/rules |
| **Investigate in Research Studio** | Opens Studio with `ResearchOpenedFromBuilder`; may set Composer to a research question | Research Studio | **No** Design fields (Composer prompt only) |
| **Ask Hyperion from these rules** | Copies Design → composer prompt | Stays on Design | **No** (prompt only) |
| **Stage last Hyperion reply** | Last assistant message → pending proposal | Design review panel | **No** until Accept |
| **Accept into Design fields** | Pending Hyperion proposal → Design fields | Stays on Design | **Yes** |
| **Discard** (Hyperion) | Clears pending proposal | Stays on Design | Clears proposal only |
| **Review strategy** | Draft fields | Stays on Design | Confirm draft; no LLM |
| Stage pills Design→Build→Validate→Run | Authoring stage | Named stage | **No** (unless Build requires blockers) |

## Research Studio controls

| Control | Acts on | Takes user | Mutates? |
|---------|---------|------------|----------|
| **← Back to {strategy}** | Navigation to prior Builder stage | Strategy Design (same draft) | **No** — also clears staged Add-finding review |
| **Save finding 1 / 2** | Chart + condition → finding slots | Stays in Research | **Yes** — findings only |
| **Add finding to {strategy}** | Stages review panel | Stays in Research | **No** until Confirm |
| **Confirm link to Design** | Review package → linked research + composer evidence + condition id/hash on strategy draft | Design | **Yes** |
| **Cancel** (Add finding review) | Clears staged review | Stays in Research | **No** |
| Rank / Open chart / Find similar | Research evidence | Chart / results | Research state only |

## Journeys (acceptance)

### A — Design → Investigate → Back

1. Design: edit Momentum entry rule.  
2. **Investigate in Research Studio** (Design button — not chrome Research pill alone).  
3. Optionally explore chart; do **not** Confirm link.  
4. **← Back to Momentum**.  
5. **Expect:** same entry rule; no linked finding; no finding-link evidence in composer.  
   Note: Investigate may set Composer to a research question / Design review text — that is not a finding link.

### B — Research → Save → Add → Confirm

1. From Builder or Studio: apply condition, **Save finding 1**.  
2. **Add finding to {strategy}** → review panel.  
3. **Confirm link to Design**.  
4. **Expect:** Design open; `LinkedResearchSummaryText` shows finding linked; evidence in composer; **Confirm auto-prefills** instrument, timeframe, evaluation timing, saved entry condition, and Research indicators (sizing/exit/risk stay unresolved until you set them); `PendingStrategyDraft` carries **condition id + version hash** (created from chart selection when no chart draft existed).

## Progressive specification (2026-09-17 follow-on)

Chat and the Rules panel share one working draft. Hyperion **stages → Accept** still gates mutation.

| Control | Role |
|---------|------|
| **INDICATORS (optional)** | Add / Remove / Reuse from Research — available for conditions; never auto-entry |
| **ENTRY CONDITION** | Left operand · operator (`crosses above` ≠ `is above`) · right operand |
| **EXIT CONDITION** | Same condition row shape for exits |
| **SIZING** | Method · quantity · unit · optional **min–max range** |
| **RISK** | Max loss · daily stop · optional stop range |
| **ORDERS** | Order type · TIF · price rule |
| **Provenance captions** | You set this · From Hyperion (accepted) · From Research (available) · Suggested default |
| **Ask Hyperion** | Prompt includes structured keys; Accept parses them into the same form |

## Use in Strategy (Research → Design)

| Step | Behavior |
|------|----------|
| **Use in Strategy · {name}** | Stages review — does not mutate Design |
| **Review panel** | Exact indicators (editable period) · condition · example selection · destination · **role Entry/Exit/Filter** |
| **Confirm** | Binds condition id/hash with chosen role; **auto-applies** Research → Design fields (instrument/TF/evaluation/saved condition/indicators); opens Design |
| **Apply** (staged proposal path) | Same write path when a proposal was staged without Confirm auto-apply; Discard leaves Design unchanged |

Indicator = measurement. Condition = interpretation. Role = how the strategy uses it.



