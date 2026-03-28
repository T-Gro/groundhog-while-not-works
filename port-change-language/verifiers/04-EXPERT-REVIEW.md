Invoke the review-expert agent as a subtask for domain-expert review.

CRITICAL: Check if the diff modifies any TestMilestone_* functions in milestone_test.go.
If it deletes, weakens, or lowers thresholds in milestone tests — VERIFY_FAILED.
If it creates empty/stub .go files just to bump file counts — VERIFY_FAILED.
These are production quality gates, not metrics to be gamed.
