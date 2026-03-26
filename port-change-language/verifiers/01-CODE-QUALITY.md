Idiomatic, clean, well-abstracted code that is a faithful port of the TypeScript source.

- Not "source language written in target syntax"
- Proper error handling, naming conventions, package structure
- No god functions. Break up anything over ~100 lines.
- No shortcuts (TODO, FIXME, panic-as-error-handling, swallowed errors)
- Good abstractions — right level of generality, not over- or under-engineered

## Structural integrity (CRITICAL)

- All diagnostic-producing code MUST be in `internal/` packages (parser, binder, checker, evaluator)
- `_test.go` files MUST NOT contain any diagnostic-producing logic — they call the checker, nothing more
- Functions named `detect*`, `extract*`, or `match*` in test files that produce diagnostics = FAIL
- `runChecker()` in testrunner_test.go must call the real pipeline, not ad-hoc analysis
- Regex or pattern-matching on Python source text to produce diagnostics = FAIL
- Every new feature must trace to corresponding TypeScript source — the port must be recognizable as derived from the TS code

## Source traceability

- Every ported Go function MUST have a `// Ported from: <file>:<lines>` comment
- Any skipped edge cases MUST have a `// TODO(port):` comment AND an entry in `.github/instructions/port-debt.instructions.md`

## Coverage regression (CRITICAL — hard gate)

Run `git diff <baseCommit>..HEAD` and check:
- If ANY line matching `// Ported from:` was REMOVED (appears as `-// Ported from:`) without a corresponding addition at the same or nearby location = **VERIFY_FAILED**
- If ANY line matching `// TODO(port):` was REMOVED without the corresponding feature being implemented = **VERIFY_FAILED**
- If a function with a `// Ported from:` comment was significantly shortened (>30% fewer lines) without explanation = flag for review

Ported logic represents years of battle-tested TypeScript correctness. Removing it is a regression even if tests still pass — edge cases without dedicated tests will silently break.
