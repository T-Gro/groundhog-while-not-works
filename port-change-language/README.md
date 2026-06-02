# ralph-port

Autonomous porting loop. Point it at a source repo, it ports until done.

```bash
cd /path/to/target-repo
dotnet fsi /path/to/ralph-port.fsx /path/to/source-repo
```

That's it. Sprint 0 bootstraps everything automatically — discovers languages, creates the harvest script, writes sprint instructions. No config files to create.

## How it works

```
while failing_tests > 0:
    harvest()           # run tests, measure pass/fail per bucket
    triage()            # bootstrap? all-pass? false-all-pass? sprint?
    implement(briefing) # AI agent ports code from source → target
    verify(diff)        # pluggable quality gates (verifiers/*.md)
    record(delta)       # persist, capture learnings, push
```

## Files

```
ralph-port.fsx     The loop (50 LOC top-level, calls into →)
Infra.fsx          Agent · Git · Harvest · Verify · Triage · Briefing · Bootstrap
Db.fsx             SQLite: sprint/bucket/test tracking
verifiers/*.md     Pluggable quality gates (drop a .md to add one)
```

## Backpressure

| What | When | Action |
|------|------|--------|
| No-commit streak | ≥3 sprints | Rotate to next bucket |
| False all-pass | Test count drops >20% | BUILD REPAIR sprint |
| Cooldown | ≥3 consecutive fails | 5 min pause |
| Nudge | `nudge.md` exists in target | Inject into next briefing, then delete |

## Battle-tested

274 sprints on pyrightgo (TypeScript → Go port of Microsoft pyright):
- Flask: 24% → 88%
- Django: 0% (crash) → 5%
- 3000+ tests passing
