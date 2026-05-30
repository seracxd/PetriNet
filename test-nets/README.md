# Test Nets

Sample Petri nets for exercising the analysis engine — especially the
unbounded-net / "truncated → inconclusive" verdicts. Load any `.json` via
**Load** in the editor (File → Load, or the toolbar), then **Run Analysis**.

The format is the editor's native JSON (camelCase; `arcType` is `0=Normal`,
`1=Inhibitor`, `2=Reset`). Coordinates are included so each net lays out
readably on load.

## Expected verdicts

`✓` = Pass / holds, `✗` = Fail / does not hold, `?` = Inconclusive (Undecidable).

| Net | Bounded | Safe | Live | Deadlock-free | Reversible | Notes |
|-----|:---:|:---:|:---:|:---:|:---:|-------|
| `bounded-live-cycle` | ✓ | ✓ | ✓ | ✓ | ✓ | Two-place token ring — the textbook "everything holds" net. |
| `shared-resource` | ✓ | ✗ | ✓ | ✓ | ✓ | 4 processes share 2 resources (skripta Obr. 1.10). Bounded & live; unsafe because places hold >1 token. |
| `dining-philosophers-4` | ✓ | ✓ | ✓ | ✓ | ✓ | 4 philosophers, **atomic** two-chopstick grab (skripta Obr. 1.19) — the atomic grab avoids the trivial deadlock. |
| `deadlock-sequential` | ✓ | ✓ | ✗ | ✗ | ✗ | P1→P2→P3 chain that ends in a deadlock and can't return to M₀. |
| `bounded-unsafe-merge` | ✓ | ✗ | ✗ | ✗ | ✗ | Two inputs merge into a weight-3 output → place reaches 3 tokens. Bounded but unsafe. **Conservative (weighted) = holds** (`3·M(P1)+M(P3)=3` is preserved); **Strictly conservative = does not hold** — Merge turns 2 tokens into 3, so the raw count is not constant. The analysis panel shows these as two separate rows. |
| `inhibitor-bounded` | ✓ | ✓ | ✗ | ✗ | ✗ | Bounded net with an inhibitor arc. Conservativeness/Repetitiveness are `?` (non-ordinary → invariant shortcuts skipped). |
| `reset-cancellation` | ✓ | ✓ | ✗ | ✗ | ✗ | Reset arc cancels a parallel branch (Aalst cancellation pattern). Structural props `?`. |

## Unbounded nets — the "truncated → inconclusive" regression cases

These are the nets the verdict fix targets. Each must report **Bounded = ✗
(does not hold)**, never Inconclusive, even though the state space is
truncated by the cap.

| Net | Bounded | Safe | Live | Reversible | Why it matters |
|-----|:---:|:---:|:---:|:---:|----------------|
| `unbounded-producer` | ✗ | ✗ | ✓ | ✗ | Ordinary net, P1 net +1 per firing. Karp-Miller introduces **ω** at P1. T1 is always enabled → live. |
| `unbounded-source-transition` | ✗ | ✗ | ✓ | ✗ | Source transition with no inputs feeds P1. ω at P1. Pre-fix this was wrongly classed as a free-choice net and reported "not live". |
| `heavyweight-state-machine` | ✗ | ✗ | ✓ | ✗ | Structural state machine (1 in / 1 out) but the output arc has weight 100, so it is **not token-conserving**. Pre-fix the "state machine ⟹ bounded" shortcut wrongly returned Pass. |
| `unbounded-saturating-inhibitor` | ✗ | ✗ | ✓ | ✗ | Inhibitor arc disables ω-acceleration, so the place climbs to the **saturation ceiling** instead of an ω sentinel. Pre-fix this saturated-but-finite marking read as "bounded" / inconclusive. |

### What these exercise

* **ω witness** (`unbounded-producer`, `unbounded-source-transition`):
  Karp-Miller ω marks unboundedness directly.
* **Saturation witness** (`unbounded-saturating-inhibitor`,
  `heavyweight-state-machine`): when ω-acceleration is off or weights are
  large, a place clamps at `int.MaxValue/2`. The engine now treats that
  ceiling as a definite unboundedness proof (`GrowsWithoutBound`), so the
  verdict is a hard **Fail** rather than a truncation-induced "Inconclusive".
* **Weight-aware structural theorems**: the State-machine and Marked-graph
  boundedness/safety/conservativeness shortcuts now require an *ordinary*
  (unit-weight) net, so a heavy-weight arc can no longer fake a Pass.
