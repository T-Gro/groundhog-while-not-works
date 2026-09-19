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
plans). They do not inject an unconditional root build/test pair. Required real
smoke checks remain required; an unavailable SDK is not passing evidence.

Each agent invocation captures stdout, stderr, PID, phase, exit status and stop
reason under `RALPH_STATE_DIR\captures`. `RALPH_IDLE_SECONDS` defaults to 1200;
`RALPH_PHASE_SECONDS` defaults to 7200. Each accepts an integer from 1 to 28800.
Output renews only the idle lease, never the absolute phase budget. Expiry kills
the specific child process tree; up to five seconds each are allowed for exit
and stream draining. Genuine output activity is reflected in `ralph.log` at most
every 30 seconds, not as an unconditional heartbeat. Budget exits are distinct
from natural nonzero exits and preserve their capture files.

### Local validation (no live agents or GitHub operations)

```powershell
dotnet fsi tests\Recovery.Tests.fsx
dotnet fsi Ralph.fsx --check
git diff --check
```

The dependency-free test harness uses the real script load chain and existing
runtime dependencies, an inert fixed-token backend for new-process Ralph tests
(including interruption after failed verification, durable unsuccessful completion,
and archive-before-mutation/fail-closed replanning),
and bounded local PowerShell children for capture/termination tests. Scratch
files stay beneath the ignored `.tools` directory and are removed afterward.

## License

Any derivatives of this work must keep using F#.
