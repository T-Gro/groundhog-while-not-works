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

For large or multiline requests, use `--request-file <absolute-path>` instead of
inline text. The file is read without trimming, including trailing newlines.
Missing paths, repeated request-file options and mixed inline/file requests are
rejected before dispatch. Inline requests remain supported.

Agent output is escaped and XML-invalid characters are replaced only when
building prompts. Valid non-BMP Unicode is preserved. Checkpoints retain raw
history, including malformed UTF-16 (encoded losslessly in the private journal).
The `RALPH_AGENT_COMPLETE` line ends agent transport and cleans up a lingering
owned process tree; it is never an implementation or verification success signal.
Agents without that marker remain subject to the finite configured timeout.

## Owner-blocked work

An implementer can request a durable pause with one output line:

```text
SUBTASK_BLOCKED {"reason":"Missing owner launch","retryCondition":"owner-launch-enrolled"}
```

The reason is limited to 512 characters. Contradictory or malformed blocked requests
also pause for owner inspection; they never trigger clarification or retries.
Ralph persists the attempt and completed sprint history before returning exit **42**.
No verifier, arbiter, architect, final-success or push path runs after the pause.
This also applies when the blocked response arrives during clarification of the
same implementer session: it does not start another implementation attempt.
Exit **43** means invalid or inaccessible checkpoint state and also requires owner
inspection, not a fresh plan.

`RALPH_STATE_DIR` holds the version-1 `blocked.json` contract and the full
`scheduler-state.json` snapshot. The record binds the issue, worktree, source and
sprint-file fingerprint, snapshot hash, current sprint and original launch.
Repeated dispatch does not mutate that checkpoint. Deleting a record, touching a
file or changing agent text is not resume authorization.

Daily Monitor's explicit owner transition uses its existing retained-worktree and
launch enrollment. It supplies a one-shot claim bound to a new executor/launch;
Ralph validates and consumes it before restoring the saved sprint history.
The enrolled child must be a live descendant of the live executor, and Ralph must
be that child or its descendant (including `dotnet` → FSI wrappers). Authenticated
resumes use the same bounded arbiter recovery as fresh execution; unrelated
historical sprints are not reactivated by an arbiter.
Ordinary arbiter transport exceptions remain ordinary failures (exit **1**) after
resume, rather than being misreported as checkpoint faults (exit **43**).
Typed checkpoint faults during resumed execution, including a denied final
checkpoint write, still return **43** and retain the last persisted journal.
An interrupted consumed resume fails closed. Deploy both repositories together:
already-running scripts do not reload source.

Run the actual scheduler fixtures (task-owned Git repository under `.fake`,
injected agents, bounded subprocesses, no model calls or publication):

```powershell
.\Test-Scheduler.ps1
```

The runner checks real FSI exit statuses 0/1/42/43, exact long-request transport,
one-shot resume and replay denial, fresh/resumed arbiter exceptions, resumed
checkpoint-finalization faults, raw prompt/history
boundaries, and marker/virtual-clock timeout cleanup without disturbing an independent
sentinel or the launcher.

## License

Any derivatives of this work must keep using F#.
