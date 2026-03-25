--- NO REGRESSION VERIFIER (EXECUTABLE) ---

TYPE: executable
GATE: hard (must pass to proceed)

WHAT IT CHECKS:
- The percentage of passing oracle tests is >= the previous sprint's percentage
- No test that was passing before is now failing (individual regression)
- The total number of tests is not decreasing (no test deletion to fake progress)

MECHANISM:
1. Read previous sprint's metrics from beads: `bd show {previous_sprint_id} --json`
2. Run current oracle comparison
3. Compare: new_passing >= old_passing AND new_total >= old_total

ON FAILURE:
- List specific tests that regressed (were passing, now failing)
- This is a HARD GATE — the sprint must be reverted or fixed
- Regressions indicate the implementor broke something that was working

WHY THIS IS CRITICAL:
- Without regression gating, agents can "fix" one thing by breaking another
- Progress must be monotonically increasing
- This is the single most important verifier for convergence
