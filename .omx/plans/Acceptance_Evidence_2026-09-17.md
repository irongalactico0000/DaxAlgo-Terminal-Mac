# Acceptance evidence — Research → Builder → Validate (2026-09-17)

**Build / commit:** `0d66819` (+ this evidence/tests commit)  
**Operator:** automated agent (headless + UX contracts); click-path still for human  
**Date:** 2026-09-17  
**Launcher:** `~/Desktop/DaxAlgo Terminal.app` (Debug, `--bypass-login`) via `tools/macos/make-dev-launcher-app.sh`

Checklist: [`.omx/plans/acceptance-journey-research-to-validate.md`](acceptance-journey-research-to-validate.md)

## Automated gates (PASS)

| Journey | Evidence |
|---------|----------|
| **A** (finding continuity) | `ResearchReferenceContinuityTests` — save finding 1/2, session reload, restore swap |
| **A/C** (Studio chrome) | `CandidateAuthoringUxContractTests.Research_Studio_*` — Rank/Compare/Save finding 1; no Reference A/B; `CompareChartTilesHost` present |
| **C** (Design first) | `StrategyAuthoringFreshSessionTests` — Builder opens Design (not Research stage); rule editor on; Research Studio separate |
| **C** (Design → request) | Fresh session: `PromoteDesignRulesToRequest` copies ENTRY/EXIT into composer; text says not TradeIR yet |
| **D** (Validate fidelity) | Fresh session: only L1 fill model selectable; queue enable → reject; partials max4 + latency applied token; explanation contains **Nautilus-class** |
| **B/C/D** (AXAML contracts) | Promote design rules button; SMA/EMA period NumericUpDown; WrapPanel stage rail; queue checkbox disabled |

**Test run:** `dotnet test … --filter CandidateAuthoringUxContract|FreshSession|ResearchReferenceContinuity` → **27 passed**.

## Human click-path (still open)

Open `~/Desktop/DaxAlgo Terminal.app` and mark A1–A11 / B1–B3 / C1–C8 / D1–D5 / E1–E2 on the checklist. Automation cannot paint charts or prove Hyperion NL answers.

## Sign-off (automated slice)

| | |
|--|--|
| A (Research) | **PASS (automated continuity + Studio chrome)** — UI paint pending human |
| B (Compare) | **PASS (contract: tiles host + strip bindings)** — live tiles paint pending human |
| C (Builder) | **PASS (Design-first + promote-to-request)** — handoff click pending human |
| D (Validate) | **PASS (L1 applied; Nautilus not claimed)** — Run historical validation click pending human |
| E (Direct rules) | **PASS (fresh session Design without Research)** |

Notes: Do not treat automated PASS as full Mac click acceptance. Use the Desktop launcher for the remaining paint path.
