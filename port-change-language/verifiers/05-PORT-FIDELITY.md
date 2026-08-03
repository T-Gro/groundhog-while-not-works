PORT FIDELITY — the faithfulness gate. This project is a PORT: the complete source
exists and every diagnostic-producing change must TRANSLATE it, never invent it.

Review ONLY this sprint's diff (baseCommit..HEAD). Open the cited source anchors yourself.

## HARD FAIL — output VERIFY_FAILED if ANY of these is true

1. **Unanchored logic.** A new/changed diagnostic-producing block has no
   `// Ported from: <file>:<lines>` anchor, OR the anchor is fabricated — open it in the
   source and confirm those lines actually contain the logic being reproduced. No match = FAIL.

2. **Invention instead of translation.** The change adds a gate, suppression,
   predicate-relaxation, regex / Python-text heuristic, magic threshold, or ad-hoc special
   case that has NO corresponding logic in the source. A port reproduces the source
   algorithm; it does not guess a shortcut that happens to move a number.

3. **Parity-neutral busywork.** The diff flips no failing test and removes no false
   positive — renames, comment/format churn, or porting a file that emits no diagnostics
   (e.g. a localizer / printer) presented as progress.

4. **Shrinking a faithful port.** A `// Ported from:` function was gutted (>30% fewer
   lines) or its edge-case branches dropped without a matching false-positive reduction.

5. **Recurring advisory debt.** A port-debt item previously flagged advisory is still
   unfixed AND this sprint edited its area — advisory must not silently become permanent.

## What to do

- For each added/changed diagnostic block: open its anchor in the source, read the source
  logic, and confirm the target faithfully reproduces it (same branches, same conditions,
  same edge cases). If you cannot confirm it, that is invention → FAIL.
- Reward big, faithful structural ports of the real source logic. Punish clever local
  patches that fake a parity gain — those regress at scale and are the reason the port stalls.

When the diff is a faithful, source-grounded port: VERIFY_PASSED.
