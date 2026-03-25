--- EXPERT REVIEWER (AGENT INVOCATION) ---

TYPE: agent-invocation
GATE: soft (advisory)

Invoke the project's expert-reviewer agent as a subtask.
The expert reviewer is a specialized agent trained on this specific codebase.

INVOCATION:
Run a copilot agent with the expert reviewer's context.
The reviewer receives the diff from this sprint and produces
a domain-expert assessment — things only someone deeply familiar
with the codebase architecture would catch.

WHAT IT COVERS:
- Architectural fit with the existing codebase
- Subtle behavioral mismatches between source and target
- Performance implications specific to this project's hot paths
- Integration concerns with already-ported modules
- Conformance gap impact assessment

CONTRACT:
- The expert reviewer does NOT make code changes
- It produces a verdict (VERIFY_PASSED / VERIFY_FAILED)
- On failure: specific, actionable feedback for the implementor
