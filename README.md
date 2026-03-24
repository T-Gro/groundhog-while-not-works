# 🔨 groundhog-while-not-works : The Iteratorinator

```
while (!works) {
    try_again();
}
```

A coding agent trapped in its own Groundhog Day.
It wakes up. It writes code. The code is wrong. It wakes up. It writes code. The code is slightly less wrong. Repeat.

What it lacks in model quality and trust, it makes up for in **sheer, caffeinated, brute-force repetition**.

## FAQ

**Q: Is it smart?**
A: No. But it has lived this day four thousand times. It knows things.

**Q: When does it stop?**
A: When CI passes. Or February 3rd. Whichever comes first.

## Setup

Add a system alias that calls `dotnet fsi Ralph.fsx` and passes arguments.

## Usage

Call from root of your repo:

- Assumes `copilot` CLI is installed
- Assumes repo has copilot instructions and skills to build and test

```bash
ralph "Fix all repo bugs labelled xyz"
```

```bash
ralph "Resolve all PR comments and CI failures on current branch" --push
```

The `--push` flag pushes changes after completion and monitors CI. When CI fails, it extracts unique failures and creates fixup commits. Requires a skill/tool that can fetch CI build errors (e.g., Azure DevOps or GitHub Actions integration).

## Large-Scale Porting (TypeScript → Go)

For porting a huge codebase (e.g. a Python typechecker written in TypeScript) to Go,
use the **PortingLoop** — an outer orchestrator that sits above Ralph's sprint loop.

### Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                        OUTER LOOP (PortingLoop.fsx)                 │
│                                                                      │
│  1. Scan TS source → discover modules → build dependency graph       │
│  2. Topological sort → leaf modules first                            │
│  3. Chunk modules into context-budget sprints (≤60k tokens each)     │
│  4. For each chunk (in order):                                       │
│     ┌──────────────────────────────────────────────────────────────┐ │
│     │  INNER LOOP (Ralph.fsx)                                     │ │
│     │  Architect → Implement → Verify → Fixup (max 15 iterations) │ │
│     └──────────────────────────────────────────────────────────────┘ │
│  5. Backpressure: go build + go test must pass before next chunk     │
│  6. Shared context: type_mappings.md grows across sprints            │
│  7. Progress: dashboard + trend chart + markdown report              │
│  8. If 3 consecutive failures → pause for human review               │
└──────────────────────────────────────────────────────────────────────┘
```

### Key Design Decisions

| Concern | Solution |
|---------|----------|
| **Context limits** | Source is chunked into ≤60k-token sprints; each sprint is self-contained |
| **Validation** | Every sprint runs `go build`, `go vet`, `go test`, plus 4 porting verifiers |
| **Backpressure** | Tests must pass before moving to next module; 3 failures → human review |
| **Feasible splits** | Topological sort on dependency graph; large modules split into parts |
| **Visual overview** | Spectre.Console dashboard + ASCII trend chart + markdown report |
| **Shared context** | Type-mapping registry (`type_mappings.md`) grows across sprints |

### Quick Start

```bash
# 1. Analyze source and create porting plan (project name is optional)
dotnet fsi PortingLoop.fsx init ./path/to/ts-source github.com/org/go-project
dotnet fsi PortingLoop.fsx init ./path/to/ts-source github.com/org/go-project "My Project"

# 2. Review the generated sprints
ls .tools/ralph/sprints/

# 3. Execute the porting loop
dotnet fsi PortingLoop.fsx run ./path/to/go-output --yes

# 4. Check progress at any time
dotnet fsi PortingLoop.fsx status
```

### Porting Verifiers

The porting loop uses four specialized verifiers (in addition to Ralph's standard ones):

| Verifier | Purpose |
|----------|---------|
| `P01-GO-BUILDS` | Go code compiles, `go vet` clean, no import cycles |
| `P02-TEST-PARITY` | Every TS test has a Go counterpart, table-driven style |
| `P03-TYPE-FIDELITY` | Types faithfully translated, no lossy `interface{}` |
| `P04-CONTEXT-BUDGET` | Sprint scoped correctly, shared mappings updated |

### Files

| File | Role |
|------|------|
| `PortingLoop.fsx` | Outer loop orchestrator — CLI entry point |
| `PortingSplit.fsx` | Source analysis, dependency graph, chunking |
| `PortingProgress.fsx` | Progress tracking, dashboard, reports |
| `templates/PORTING_SPRINT_TEMPLATE.md` | Sprint template for porting tasks |
| `verifiers/P0[1-4]-*.md` | Porting-specific quality verifiers |

## License

Any derivatives of this work must keep using F#.

