# Convergence Porting Tool

Test-driven convergence loop for porting large codebases between languages.
Language-agnostic — knows nothing until `init` configures `project.json`.

## How It Works

```
┌─ ORCHESTRATOR (ConvergenceLoop.fsx) ─── re-entrant, one step per invocation ─┐
│                                                                               │
│  1. Read test DB → pick failing bucket (with alternatives for fuzziness)      │
│  2. Build dynamic brief → run IMPLEMENTOR (fresh copilot session)             │
│  3. Build + test → HARD GATE on regression                                    │
│  4. Run verifiers from verifiers/ folder (dynamic, read-only reviewers)       │
│     └─ On fail: resume implementor → implementor fixes → resume verifier      │
│  5. All pass → archive DB, update beads, capture learnings                    │
└───────────────────────────────────────────────────────────────────────────────┘
```

## Information Flow

| What | Where | Access |
|------|-------|--------|
| Code changes | git commits | Agents see diffs via `git diff HEAD~1` |
| Project conventions | `.github/copilot-instructions.md` | Always loaded by copilot |
| Scoped conventions | `.github/instructions/*.md` | Loaded when matching file globs |
| Reusable techniques | `.github/skills/*/SKILL.md` | Loaded on demand by trigger |
| Architecture decisions | `adr/INDEX.md` → `adr/NNNN-*.md` | Read by agents on start |
| Test pass/fail | SQLite `testdata/current_results.db` | Queried by orchestrator |
| Subtask progress | beads (`bd`) | Query from another terminal |

## Agent Invocation

All agents run via GitHub Copilot CLI with Opus 4.6:
```
copilot -p "prompt" --model claude-opus-4.6 --resume <sessionId>
        --allow-all --no-ask-user --autopilot --no-color --stream off
```

Session IDs enable the resume chain: implementor ↔ verifier ↔ implementor.

## Visualization

The orchestrator prints progress to stdout. For beads task status, run in another terminal:
```bash
bd ready && bd list --status=open && bd status
```

## Quick Start

```bash
dotnet fsi ConvergenceLoop.fsx init <source-dir> <target-dir> [--plan file.md]
dotnet fsi ConvergenceLoop.fsx step [--retries=N]
dotnet fsi ConvergenceLoop.fsx status
```
