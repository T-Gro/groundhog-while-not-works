# Porting Plan: Pyright (TypeScript → Go)

## Overview

Port [pyright](https://github.com/microsoft/pyright), a Python typechecker written
in TypeScript (~122K LoC source, 40K LoC tests, 1,284 Python test sample files)
to idiomatic Go.

## Source Codebase Profile

- **Location**: `Q:\pyright`
- **Packages**: `pyright-internal` (engine), `pyright` (CLI), `vscode-pyright` (extension)
- **Core source**: `packages/pyright-internal/src/` — 194 TS files, 122K lines
- **Tests**: 56 `.test.ts` suites + 1,284 `.py` sample files (the oracle)
- **Test framework**: Jest
- **Biggest files**: `typeEvaluator.ts` (25,122 lines), `checker.ts` (6,624), `parser.ts` (4,562)

## Target

- **Location**: `Q:\pyrightgo`
- **Language**: Go
- **Module path**: `github.com/AnywhereRealEstate/pyrightgo` (or TBD)
- **Build**: `go build ./...`
- **Lint**: `go vet ./...`
- **Test**: `go test -v -count=1 ./...`

## Convergence Oracle

The 1,284 Python `.py` sample files in `tests/samples/` define expected behavior.
Each sample, when run through the original pyright, produces specific diagnostics
(errors, warnings) at specific line numbers. The golden reference captures these.

**Progress metric**: % of samples producing matching diagnostics (0% → 100%).

## Layers (dependency order)

| Layer | Name | Source Dir | ~Lines | Boundary | Test strategy |
|-------|------|-----------|--------|----------|---------------|
| L0 | common | `common/` | 15K | Pure utility functions | Unit tests |
| L1 | parser | `parser/` | 8.8K | text → AST (ParseNode tree) | Parse all 1,284 samples |
| L2 | binder | `analyzer/binder.ts` + related | 5K | AST → scopes, symbol tables | Scope resolution tests |
| L3 | types | `analyzer/types.ts`, `typeUtils.ts`, `typeEvaluator.ts` | 40K | Type inference + evaluation | Type evaluation samples |
| L4 | checker | `analyzer/checker.ts` + related | 10K | Produces diagnostics | Golden file comparison |
| L5 | langservice | `languageService/`, `commands/` | 8K | LSP protocol handlers | Fourslash test suite |

### L3 Decomposition (typeEvaluator.ts is 25K lines — must split by feature)

| Feature | Est. samples | Dependency |
|---------|-------------|------------|
| Basic types (literals, None, bool, int, str) | ~50 | none |
| Variables & assignments | ~40 | basic types |
| Functions & parameters | ~80 | variables |
| Classes & inheritance | ~120 | functions |
| Generics | ~140 | classes |
| Protocols | ~90 | generics |
| Type guards & narrowing | ~80 | generics |
| Overloads | ~50 | functions |
| Decorators | ~30 | functions, classes |
| TypedDict, NamedTuple, dataclass | ~60 | classes |
| Union types, intersections | ~70 | basic types |
| Callable types | ~40 | functions |
| Type aliases, NewType | ~30 | basic types |
| Pattern matching | ~40 | classes, unions |
| Comprehensions & generators | ~30 | functions |
| ParamSpec, TypeVarTuple | ~30 | generics |

## Key Translation Patterns

### Discriminated Unions → Sum Types
TypeScript `type T = A | B | C` → Go interface with marker method + type switch.

### Optional Fields → Pointer Types
TypeScript `field?: T` → Go `*T` (for value types) or `nil` (for interface types).

### Enums → Const Blocks
TypeScript `enum E { A, B }` → Go `const ( EA = iota; EB )` with `String()` method.

### Exceptions → Error Returns
TypeScript `throw new Error(...)` → Go `return ..., fmt.Errorf(...)`.

### Lazy Evaluation → sync.Once
Pyright evaluates types lazily → Go `sync.Once` or `Lazy[T]` wrapper.

### Class Hierarchies → Embedded Structs
TypeScript `class B extends A` → Go struct embedding + interface.

## Architecture Notes

### Core Pipeline
```
Python source → Tokenizer → Parser → Binder → TypeEvaluator → Checker → Diagnostics
                                                     ↓
                                              LanguageService → LSP
```

### Key Dependencies
- `common/` is standalone (no internal deps)
- `parser/` depends only on `common/`
- `analyzer/binder.ts` depends on `parser/` AST types
- `analyzer/types.ts` is a standalone type definition file
- `analyzer/typeEvaluator.ts` depends on EVERYTHING in analyzer — it IS the core
- `analyzer/checker.ts` depends on typeEvaluator
- `languageService/` depends on all of the above

### What Makes This Hard
1. `typeEvaluator.ts` is 25,122 lines in ONE file — deeply recursive, self-referential
2. Heavy use of TypeScript union types (Go has no native equivalent)
3. Deep recursion in type evaluation (generics + protocols + overloads interact)
4. Lazy evaluation patterns throughout
5. Circular type references (recursive generics)
6. Typeshed stubs (5,224 files of Python type definitions — need equivalent in Go)

## Phases

### Phase 0: Infrastructure
- Set up Go module, directory structure mirroring layers
- Copy all 1,284 `.py` samples into Go test fixtures
- Run original pyright on all samples → golden reference files
- Create Go test harness for golden comparison
- Baseline: 0/1,284 passing

### Phase 1: Common + Parser
- Port `common/` utilities (pure functions, easy wins)
- Port tokenizer + parser (clear boundary: text → AST)
- Gate: all 1,284 samples parse without crash

### Phase 2: Binder
- Port scope/symbol binding
- Gate: scope resolution correct for all parseable samples

### Phase 3: Type Evaluator (iterative, by feature)
- Port one feature category per sprint (see L3 decomposition)
- Each sprint measures Δ% improvement on golden comparison
- This is the longest phase — expect 15+ sprints

### Phase 4: Checker
- Port diagnostic generation
- Golden file comparison becomes the primary metric
- Expect rapid convergence as the type evaluator stabilizes

### Phase 5: Language Service
- Port LSP handlers
- Test against fourslash test suite
- Can be parallelized with late Phase 3/4 work

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| typeEvaluator too big for context | Decompose by feature, feed only relevant sections |
| Union types → Go mismatch | Establish sum-type pattern early, reuse everywhere |
| Typeshed stubs (5K files) | Defer — use minimal stubs for test samples initially |
| Circular type refs | Implement lazy evaluation wrappers early in L3 |
| Regression during iterative L3 | V03-NO-REGRESSION hard gate on every sprint |
