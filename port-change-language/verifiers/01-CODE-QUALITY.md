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
- Check that `port_status` table in `pyright-source-index.db` was updated for touched files
