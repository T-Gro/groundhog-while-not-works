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

These options are mutually exclusive with `--restart` and `--push`. Fresh
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
`CaptureFailed`. A non-`Exited` receipt is never agent success, even with exit 0.
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

The dependency-free test harness uses the real script load chain and existing
runtime dependencies, an inert fixed-token backend for new-process Ralph tests
(including interruption after failed verification, durable unsuccessful completion,
and archive-before-mutation/fail-closed replanning),
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
