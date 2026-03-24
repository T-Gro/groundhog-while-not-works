---
---
<!--
  PORTING SPRINT FILE FORMAT
  ==========================
  Each sprint ports one or more TypeScript modules to idiomatic Go.
  The sprint file is SELF-CONTAINED — the implementor agent sees ONLY this file
  plus the shared type-mapping context.

  REQUIRED SECTIONS:
  1. # Sprint: Port [module] to Go
  2. ## Context                — Why this module, what it does, dependencies
  3. ## Source Reference       — The TypeScript code to translate (inline or file paths)
  4. ## Type Mapping Context   — Shared type mappings from previous sprints
  5. ## Description            — Detailed implementation guidance
  6. ## Definition of Done     — Build + test + review criteria

  CONTEXT BUDGET:
  Each sprint targets ≤60k tokens of source code.  If a module is larger,
  it is split across multiple sprints (part1, part2, …).
-->

# Sprint: Port [module-name] to Go

## Context
[What this module does in the TypeScript codebase. Why it matters. What depends on it.]

## Source Reference
[INLINE the TypeScript source code here, or list file paths if too large.]
<!-- Keep within the context budget — ~60k tokens of source per sprint. -->

```typescript
// Paste the TypeScript code to port here
```

## Type Mapping Context
<!-- This section is auto-injected by PortingLoop with shared/type_mappings.md content. -->
[Shared type mappings from previously ported modules]

## Description
Translate the TypeScript source above to idiomatic Go.

### Target Go Package
- Package path: `[go-module-path]/[package-name]`
- File(s): `[package-name].go`, `[package-name]_test.go`

### Implementation Steps
1. Create the Go package directory and `package` declaration
2. Define Go structs/interfaces matching TypeScript types
3. Translate each exported function preserving the public API contract
4. Translate internal helpers
5. Add Go doc comments from existing TSDoc/JSDoc
6. Write table-driven `_test.go` tests covering all existing test cases

### Idiomatic Go Patterns
- Return `(result, error)` instead of throwing exceptions
- Use `context.Context` for cancellation (replaces `async/await` patterns)
- Use `io.Reader`/`io.Writer` interfaces for stream processing
- Prefer value receivers for small structs, pointer receivers for large/mutable ones
- Use `sync.Mutex` or channels for thread safety (not shared mutable state)

### What to Avoid
- Do NOT use `interface{}` / `any` unless TypeScript source is truly `unknown`
- Do NOT add external dependencies without justification
- Do NOT leave stubs — every function must be fully implemented
- Do NOT change the public API contract (function names, parameter semantics)

### Type Mapping Updates
After porting, append any NEW project-specific type mappings to the shared file:
`[path-to-shared-type-mappings]`

## Definition of Done
- `go build ./...` succeeds with no errors in the target package
- `go vet ./...` reports no issues
- `go test -v ./...` passes for this package
- Every exported TypeScript function has a corresponding Go function
- Every existing TypeScript test case has a corresponding Go table-driven test
- New type mappings appended to shared type_mappings.md
- No `TODO`, `FIXME`, or `STUB` placeholders remain
- Go doc comments present on all exported symbols
