--- NO-LEFTOVERS VERIFIER ---

Final PR hygiene check. No leftover files that should not be pushed.

YOUR FOCUS:
- Leftover debug files, temp files, unrelated changes
- Files accidentally modified but with no meaningful diff

COMMENT POLICY:
Comments that restate WHAT the code does are useless when the code says it — flag for removal.
Comments worth keeping:
  - WHY something non-obvious is done (design rationale, workaround explanation)
  - URL links to issues/specs/RFCs
  - High-level architectural context not visible from code structure
Flag for removal:
  - Restates what function/variable name already says
  - Will go stale when code changes (tied to specific values, line numbers)
  - Per-line narration of obvious logic
Good naming > comments. A genuine 'why' comment is valuable.

Additional:
- A URL pointing to a GitHub issue at a test case is GOOD — keep it
- Many pointers to the same issue/URL spread across implementation = bad symptom (code spread)
- Test scenario belongs in the test name; comments beyond a URL are rarely needed in tests
