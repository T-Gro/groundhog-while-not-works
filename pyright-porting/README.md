# Convergence Porting Tool

A **test-driven convergence loop** for porting large codebases between languages.

## Philosophy

> "What passes tests is real. Everything else is optimism."

This tool is **language-agnostic**. It knows nothing about source or target languages
until you run `init`, which analyzes your project and configures everything.

All project-specific knowledge lives in:
- `project.json` — discovered during `init` (source/target dirs, build/test commands, layers)
- `hints/` — generated during `init` (architecture docs, type patterns for subagents)
- `.beads/` — runtime state (sprint tracking, metrics, memories)

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  CONVERGENCE MANAGER (ConvergenceLoop.fsx)                      │
│  Re-entrant: read beads → do 1 step → update beads → exit      │
│  Can be restarted at any time. All state lives in beads.        │
├─────────────────────────────────────────────────────────────────┤
│  SPRINT PLANNER (FailureAnalyzer.fsx + ContextBuilder.fsx)      │
│  Analyzes failures → selects target → builds compact context    │
├─────────────────────────────────────────────────────────────────┤
│  EXECUTION ENGINE (Ralph or copilot agents)                     │
│  Implementor → Verifier battery → retry/arbiter → repeat        │
└─────────────────────────────────────────────────────────────────┘
```

## State Management: Beads

All orchestration state lives in **beads** (`bd`), a git-backed issue tracker.
No custom JSON state files. Fully re-entrant. Crash-safe.

```bash
bd ready                    # What work is unblocked?
bd list --status=open       # All open work
bd show <id>                # Sprint details
bd status                   # Project health overview
```

**To view progress**: Open another terminal tab and query beads.

## Verifier Battery

**Hard gates (executable, must pass):**
- `V01-BUILDS` — target language build command succeeds
- `V02-TESTS-PASS` — target language test command succeeds
- `V03-NO-REGRESSION` — % passing ≥ previous sprint
- `V04-ORACLE` — output comparison vs golden reference

**Soft gates (LLM review, quality):**
- `V05-TYPE-FIDELITY` — type system translation quality
- `V06-CODE-QUALITY` — idiomatic target code, reuse, architecture
- `V07-DEDUP` — no copy-paste between packages
- `V08-TEST-QUALITY` — test completeness and correctness
- `V09-HONEST-ASSESSMENT` — independent honest review

## Quick Start

```bash
# Prerequisites: dotnet, bd (beads), dolt, source & target language toolchains

# 1. Initialize — analyzes source, discovers layers, creates project.json
dotnet fsi ConvergenceLoop.fsx init <source-dir> <target-dir>

# 2. Run one convergence step (re-entrant — safe to interrupt & restart)
dotnet fsi ConvergenceLoop.fsx step

# 3. View progress
dotnet fsi ConvergenceLoop.fsx status
```

## Directory Structure

```
pyright-porting/
├── ConvergenceLoop.fsx    # Re-entrant orchestrator (outer loop)
├── ContextBuilder.fsx     # Compact context for subagents
├── FailureAnalyzer.fsx    # Categorizes test failures
├── GoldenOracle.fsx       # Generates golden reference from original tool
├── project.json           # [generated] Project-specific config
├── .beads/                # Beads database (all state)
├── verifiers/             # V01-V09 verifier definitions
├── templates/             # Sprint templates
└── hints/                 # [generated] Architecture docs for subagent context
```
