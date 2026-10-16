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
Fresh and resumed CLI calls use JSONL output. Only root final assistant content
reaches the scheduler, without terminal wrapping or whitespace trimming. User,
tool and subagent content cannot supply scheduler signals. Missing, malformed or
contradictory transport envelopes fail explicitly.
The stream must contain a root response and a terminal CLI result with exit code zero.
After that result, a lingering owned process tree is cleaned up. A natural
nonzero process exit still fails. Neither the result nor `RALPH_AGENT_COMPLETE`
proves implementation or verification success. Calls without a terminal result
remain subject to the finite configured timeout.

Fresh and resumed agents use `gpt-6.1-sol`, `xhigh`, and `long_context` by
default. Both paths disable built-in MCP servers only; custom MCP configuration
remains enabled. The quality and final-assessment verifier instructions require
the same explicit profile for their expert-reviewer subagents.

Native Git commands run in `RALPH_WORK_DIR`, not the launcher checkout.
Approval records use the target repository's full commit hash. Incremental
verification excludes short or foreign historical hashes without deleting them.
A moving HEAD fails verification, and skipped verifiers do not create approvals.
Uncommitted changes also disable incremental approval reuse and commit approval
recording; verifier results still describe the work that was actually checked.

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

Use `.\Test-Scheduler.ps1 -ProfileOnly` for the target-repository, dirty-tree,
fresh/resume argument, and verifier subagent-profile regressions.

The runner checks real FSI exit statuses 0/1/42/43, exact long-request transport,
one-shot resume and replay denial, fresh/resumed arbiter exceptions, resumed
checkpoint-finalization faults, raw prompt/history
boundaries, structured fresh/resume output (including the original #2020 block),
foreign-marker rejection, malformed/truncated transport, and terminal-result/
virtual-clock timeout cleanup without disturbing an independent sentinel or the launcher.

## Durable continuation

Set `RALPH_WORK_DIR` to the target repository and `RALPH_STATE_DIR` to its
external orchestration directory. Never put private plans or captures in the
product repository.

```powershell
# First adoption of an existing BACKLOG.md and sprints, without a checkpoint:
dotnet fsi Ralph.fsx "the original request" --adopt-plan --headless --yes

# Later invocations, including a new process after interruption:
dotnet fsi Ralph.fsx "the original request" --resume --headless --yes
```

`--adopt-plan` refuses an existing `resume.json` and treats every present sprint
as unfinished. Missing historical sprint files are not reconstructed or marked
complete. `--resume` requires a versioned, atomically replaced checkpoint bound
to the exact request, absolute work directory, and hashes of the backlog and
sprint files. Missing, changed, or inconsistent phase/cursor state fails closed
without invoking an architect. Completed sprints, iteration history, and pending verification
survive process restarts. A verifier interrupted before its result is recorded
runs again; it is never inferred to have passed. Agent sessions themselves are
not restored across processes. Recorded failures remain failures: unfinished
verification resumes before any retry, and exhausted final-verification fixups
continue to exit unsuccessfully on repeated resume without rerunning agents.
Each implementation invocation counts once, including incomplete work, not again
when a verifier requests a retry.
Durable checkpoints preserve raw UTF-16 history and full-HEAD verifier approvals.
Legacy passes without those approvals require verification again. Neither
`--resume` nor `--adopt-plan` bypasses an owner-blocked checkpoint.
New checkpoints include a lossless scheduler snapshot and an active-sprint list.
Legacy version-1 checkpoints remain readable. Retired history stays inactive,
including files removed after an archived replan. Completion applies to active
sprints; it does not declare unfinished historical work complete.

These options are mutually exclusive with `--restart` and `--push`. Fresh
dispatch refuses retained plans without an explicit recovery option. Authorized
planning, explicit restart, and arbiter replanning first copy existing top-level
state files and sprint files to a uniquely named `archives` directory. Existing
captures remain in place. Resume does not reset Git or restore a source revision;
the invoking supervisor must validate the target branch/recovery revision and
the clean, exact launcher pin. Deploying a pin does not replace an already-running
supervisor, and must not start a duplicate executor.

Both implementer and verifier prompts use the sprint's **Validation scope and
completion contract** (falling back to its DoD and repository guidance for older
plans). The heading may use any Markdown ATX depth; nested subsections stay in
the contract, which ends at the next heading of equal or shallower depth.
They do not inject an unconditional root build/test pair. Required real
smoke checks remain required; an unavailable SDK is not passing evidence.

Each agent invocation captures stdout, stderr, PID, phase, exit status and stop
reason under `RALPH_STATE_DIR\captures`. `RALPH_IDLE_SECONDS` defaults to 1200;
`RALPH_PHASE_SECONDS` defaults to 7200. Each accepts an integer from 1 to 28800.
Output renews only the idle lease, never the absolute phase budget. Capture
requires 64-bit Windows 10 or later: a kill-on-close Job object is assigned
atomically during native process creation, before target code executes. Only
the intended standard handles are inherited. The entire owned job is terminated
on budget expiry **or direct-parent exit**, including descendants with no captured
pipes; unrelated processes are not enumerated or terminated. Unsupported
containment fails before the target starts.

After the parent exits or a budget expires, cleanup allows five seconds for the
owned job to empty, then one shared five-second deadline for stdout, stderr and
stdin completion. Input is UTF-8 without BOM plus `Environment.NewLine`; completion
includes sending all bytes and closing stdin. Receipts are flushed and atomically
replaced before returning a failure to the caller. They retain the actual parent
`ExitCode` (null only if unavailable), partial output, `InputCompleted`,
`StreamsDrained`, `OwnedProcessesStopped`, and a `Failures` array.
Natural completion uses `Exited`; otherwise `CleanupFailed`, `DrainFailed` or
`InputFailed` identifies the first completion failure. Existing `IdleLease` and
`HardBudget` reasons take precedence when their budgets expired, with any
additional errors retained in `Failures`; supervision exceptions use
`CaptureFailed`. A valid terminal JSONL result can also end capture with
`TerminalResult`. Its forced root exit uses a distinct code, not a fabricated
natural exit. Other natural nonzero exits still fail. Every successful call
requires complete stdin, drained streams, an empty owned job, and valid JSONL.
Genuine output activity is reflected in `ralph.log` at most every 30 seconds,
not as an unconditional heartbeat.

### Local validation (no live agents or GitHub operations)

```powershell
dotnet fsi tests\Recovery.Tests.fsx
dotnet fsi Ralph.fsx --check
git diff --check
```

Fixture processes have a three-minute startup/execution budget to include FSI
dependency resolution and nested script loads. Timeout failures retain captured
stdout/stderr. This does not change the shorter idle/deadline budgets exercised
by the process-supervision tests or the launcher's production limits.
The full harness runs four disjoint groups in separate processes, each with its
own scratch directory, under one fourteen-minute deadline. A test-name argument
still runs matching cases serially for focused diagnosis.

The dependency-free test harness uses the real script load chain and existing
runtime dependencies, an inert fixed-token backend for new-process Ralph tests
(including interruption after failed verification, durable unsuccessful completion,
archive-before-mutation/fail-closed replanning, and fresh-process resume at
implementation/verification boundaries after arbiter plan replacement while
preserving checkpoint identity, accepted sprint history, archives, and a real
saved-descendant Git checkout),
and bounded local PowerShell children for capture/termination tests. Scratch
files stay beneath the ignored `.tools` directory and are removed afterward.

The `boundary stdin` cases load the real `runAgent` with an offline CLI that
reads raw stdin to EOF and compares the entire generated prompt before returning
a completion token. They cover fresh/resumed session arguments, multiline and
quoted Unicode data beyond a pipe buffer, early-close failure, and dropped or
truncated input mutations in isolated source copies. Set `RECOVERY_SPRINT_PATH`
to an existing sprint to test its on-disk H3 validation section and commands;
otherwise the tests create a small local sprint. Failed fixtures retain their
scratch path and byte/receipt evidence instead of deleting it.

## License

Any derivatives of this work must keep using F#.
