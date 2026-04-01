Invoke the review-expert agent as a subtask for domain-expert review.

CRITICAL ANTI-GAMING CHECKS:

1. If the diff modifies quality_gates_test.go:
   - Check if ANY numeric threshold was LOWERED — VERIFY_FAILED
   - Check if ANY TestQualityGate_ function was deleted — VERIFY_FAILED
   - Check if t.Skip() was added to non-PyrightComparison tests — VERIFY_FAILED

2. If the diff modifies testdata/parity_ratchet.json:
   - Check that ALL numbers INCREASED or stayed the same — never decreased
   - If any parity_pct or matching count went DOWN — VERIFY_FAILED
   - This is a ratchet — it only goes up

3. If the diff modifies reference baselines (testdata/baselines/reference/real-world-*.txt):
   - VERIFY_FAILED — reference baselines come from pyright only, never pyrightgo

4. If the diff creates empty .go files to bump file counts — VERIFY_FAILED

These are production quality gates. Gaming them delays shipping.
