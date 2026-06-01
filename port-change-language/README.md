# ralph-port — Autonomous Porting Loop

An autonomous while-loop that drives a porting project from 0% to 100% test parity.

```
while failing_tests > 0:
    harvest()           # measure current test state
    triage()            # classify: bootstrap / all-pass / false-all-pass / sprint
    implement(briefing) # run AI agent with failing-bucket context
    verify(diff)        # quality gates on the delta
    record(delta)       # persist results + push
```

## Quick start

```bash
cd /path/to/target-project

# 1. Create project.json
echo '{"ProjectName":"myport","SourceDir":"/path/to/original","SourceLang":"Python","TargetLang":"Rust"}' > project.json

# 2. Create harvest script (_tools/harvest_tests.py)
#    Must: run all tests, write results to SQLite DB passed as argv[1]

# 3. Optionally: .github/instructions/sprint-briefing.md (project-specific agent instructions)

# 4. Run
dotnet fsi /path/to/ralph-port/Loop.fsx run
```

## Files

| File | Lines | Role |
|------|-------|------|
| `Loop.fsx` | ~400 | Main loop: Sprint phases + ConvergenceLoop + CLI |
| `SprintDb.fsx` | ~180 | SQLite test tracking: sprint/bucket/test schema, trends |
| `ProjectConfig.fsx` | ~25 | `project.json` reader |
| `verifiers/*.md` | varies | Pluggable quality gate prompts |

## Architecture

```
ProjectConfig.fsx    project.json reader (what/where/languages)
        │
SprintDb.fsx         SQLite: sprint tracking, bucket ranking, trend charts
        │
Loop.fsx             The convergence loop
  ├── Agent          copilot CLI wrapper
  ├── Git            head / push / hasNewCommits
  ├── Beads          task tracking (dolt-backed, optional)
  ├── Verifiers      pluggable .md review gates
  └── Sprint         domain logic phases:
       ├── harvest   run _tools/harvest_tests.py → SQLite
       ├── triage    classify: bootstrap / all-pass / false-all-pass / sprint
       ├── briefing  generic framing + {{template}} from target repo
       ├── implement run agent, capture stdout, detect stalls
       ├── verify    run verifiers, fix failures, recheck
       └── traceability  check // Ported from: markers (opt-in)
```

## Project-specific configuration

All project-specific content lives in the **target repo**, not here:

| File in target repo | Purpose |
|---------------------|---------|
| `project.json` | Name, source dir, languages |
| `_tools/harvest_tests.py` | Test runner → SQLite |
| `.github/instructions/sprint-briefing.md` | Sprint instructions template |
| `nudge.md` | One-shot human override (consumed + deleted) |
| `pyright-source-index.db` | Port traceability DB (opt-in) |

## Backpressure mechanisms

| Mechanism | Trigger | Action |
|-----------|---------|--------|
| **No-commit streak** | ≥3 sprints without commits | Rotate to next bucket |
| **False all-pass guard** | Test count drops >20% from historical high | Launch BUILD REPAIR sprint |
| **Cooldown** | ≥3 consecutive failed sprints | 5 min pause |
| **Extended cooldown** | ≥10 consecutive failures | 30 min pause |
| **Periodic review** | Every 7 sprints | Full codebase review + refactor |
| **Nudge** | `nudge.md` exists in target | Inject into next briefing |

## Commands

```
ralph-port run   [--retries=N]   # Loop until all pass. Ctrl+C safe.
ralph-port step  [--retries=N]   # One sprint.
ralph-port status                # Current state + progress chart.
ralph-port watch [seconds]       # Live dashboard.
```
