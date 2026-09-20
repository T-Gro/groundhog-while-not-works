#load "..\\Utils.fsx"
#load "..\\TypeDefinitions.fsx"
#load "..\\Prompting.fsx"

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading
open System.Xml.Linq
open TypeDefinitions
open Prompting

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let testGroup (name: string) =
    if name.Contains("after arbiter", StringComparison.Ordinal) then
        "arbiter"
    elif name.StartsWith("loop:", StringComparison.Ordinal) then
        "loop"
    elif name.StartsWith("boundary ", StringComparison.Ordinal) then
        "boundary"
    else
        "state"

if fsi.CommandLineArgs.Length = 1 then
    let children =
        ResizeArray<Process * Threading.Tasks.Task<string> * Threading.Tasks.Task<string>>()

    let deadline = DateTime.UtcNow.AddMinutes 14.
    let mutable exitCode = 0

    try
        for group in [ "state"; "arbiter"; "loop"; "boundary" ] do
            let psi =
                ProcessStartInfo(
                    "dotnet",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                )

            for arg in [ "fsi"; fsi.CommandLineArgs[0]; "--group=" + group ] do
                psi.ArgumentList.Add arg

            let child = Process.Start psi
            children.Add(child, child.StandardOutput.ReadToEndAsync(), child.StandardError.ReadToEndAsync())

        for child, stdout, stderr in children do
            let remaining = max 0 (int (deadline - DateTime.UtcNow).TotalMilliseconds)

            if not (child.WaitForExit remaining) then
                child.Kill true
                child.WaitForExit()

                eprintfn
                    "Recovery test group PID %d exceeded the shared fourteen-minute deadline (exit %d)."
                    child.Id
                    child.ExitCode

                exitCode <- 1

            printf "%s" stdout.Result
            eprintf "%s" stderr.Result

            if child.ExitCode <> 0 then
                exitCode <- 1
    finally
        for child, _, _ in children do
            if not child.HasExited then
                child.Kill true
                child.WaitForExit()

            child.Dispose()

    exit exitCode

let scratch =
    Path.Combine(root, ".tools", "recovery-tests", Guid.NewGuid().ToString("N"))

Directory.CreateDirectory scratch |> ignore
let mutable passed = 0
let mutable failed = 0
let fixtureTimeoutMs = 180_000

let check condition message =
    if not condition then
        failwith message

let test (name: string) action =
    if
        (if fsi.CommandLineArgs[1].StartsWith("--group=", StringComparison.Ordinal) then
             fsi.CommandLineArgs[1] = "--group=" + testGroup name
         else
             name.Contains(fsi.CommandLineArgs[1], StringComparison.Ordinal))
    then
        try
            action ()
            passed <- passed + 1
            printfn "PASS %s" name
        with ex ->
            failed <- failed + 1
            printfn "FAIL %s: %s" name ex.Message

let runProcessExpect expectedExit (psi: ProcessStartInfo) =
    use child = Process.Start psi
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit(fixtureTimeoutMs)) then
        child.Kill(true)
        child.WaitForExit()
        failwith $"Fixture exceeded {fixtureTimeoutMs / 1000} second test budget: {stdout.Result}\n{stderr.Result}"

    check
        (child.ExitCode = expectedExit)
        $"Fixture exit {child.ExitCode}, expected {expectedExit}: {stdout.Result}\n{stderr.Result}"

let runProcess = runProcessExpect 0

let runWorkerAt directory mode =
    let psi =
        ProcessStartInfo("dotnet", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    for arg in
        [ "fsi"
          Path.Combine(__SOURCE_DIRECTORY__, "Recovery.Worker.fsx")
          mode
          directory ] do
        psi.ArgumentList.Add arg

    psi.Environment["RALPH_WORK_DIR"] <- root
    psi.Environment["RALPH_STATE_DIR"] <- directory
    runProcess psi

let runWorker = runWorkerAt scratch

let ralphProcess directory flags =
    let fakeDir = Path.Combine(directory, ".tools", ".npm-global")
    Directory.CreateDirectory fakeDir |> ignore
    let calls = Path.Combine(directory, "fake-agent-calls.log")

    if File.Exists calls then
        File.Delete calls

    File.WriteAllText(
        Path.Combine(fakeDir, "copilot.cmd"),
        $"""@echo off
echo %%*>>"{calls}"
pwsh -NoProfile -Command "[Console]::In.ReadToEnd() | Out-Null"
if errorlevel 1 exit /b 42
if defined RECOVERY_FAIL_ONCE echo %%* | findstr /c:"%%RECOVERY_FAIL_ONCE%%" >nul && (if not exist "{calls}.failed" (echo failed>"{calls}.failed" & echo ^<ManagementSummary^>retained failure evidence^</ManagementSummary^> & echo VERIFY_FAILED & exit /b 0))
if defined RECOVERY_FAIL_FINAL echo %%* | findstr /c:"FinalVerify-FUNCTIONAL-" >nul && (echo ^<ManagementSummary^>retained failure evidence^</ManagementSummary^> & echo VERIFY_FAILED & exit /b 0)
if defined RECOVERY_INCOMPLETE_ONCE echo %%* | findstr /c:"Implement-" >nul && (if not exist "{calls}.incomplete" (echo incomplete>"{calls}.incomplete" & echo SUBTASK_INCOMPLETE & exit /b 0))
if defined RECOVERY_ARBITER if not exist "{directory}\sprints\09_Saved.md" goto skipinterrupt
if defined RECOVERY_INTERRUPT echo %%* | findstr /c:"%%RECOVERY_INTERRUPT%%" >nul && (echo waiting>"{calls}.waiting" & pwsh -NoProfile -Command Start-Sleep -Seconds 120)
:skipinterrupt
if defined RECOVERY_REPLAN echo %%* | findstr /c:"Architect-" >nul && (dotnet fsi "{Path.Combine(__SOURCE_DIRECTORY__, "Recovery.Worker.fsx")}" replan "{directory}" & exit /b)
if defined RECOVERY_ARBITER echo %%* | findstr /c:"Architect-" >nul && (dotnet fsi "{Path.Combine(__SOURCE_DIRECTORY__, "Recovery.Worker.fsx")}" plan-arbiter "{directory}" & exit /b)
if defined RECOVERY_ARBITER echo %%* | findstr /c:"Implement-4-" >nul && (echo SUBTASK_INCOMPLETE & exit /b 0)
if defined RECOVERY_ARBITER echo %%* | findstr /c:"Arbiter-" >nul && (dotnet fsi "{Path.Combine(__SOURCE_DIRECTORY__, "Recovery.Worker.fsx")}" replan-arbiter "{directory}" & exit /b)
echo SUBTASK_COMPLETE
echo VERIFY_PASSED
"""
    )
    // This backend only emits fixed test tokens. No agent or network client is invoked.
    let psi =
        ProcessStartInfo("dotnet", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    for arg in
        [ "fsi"
          Path.Combine(root, "Ralph.fsx")
          "Recovery test request"
          "--headless"
          "--yes" ]
        @ flags do
        psi.ArgumentList.Add arg

    psi.Environment["RALPH_WORK_DIR"] <- root
    psi.Environment["RALPH_STATE_DIR"] <- directory
    psi.Environment["DM_BASE_DIR"] <- directory

    for name in
        [ "RECOVERY_FAIL_ONCE"
          "RECOVERY_FAIL_FINAL"
          "RECOVERY_INCOMPLETE_ONCE"
          "RECOVERY_INTERRUPT"
          "RECOVERY_REPLAN"
          "RECOVERY_ARBITER" ] do
        psi.Environment.Remove name |> ignore

    psi, calls

let interruptRalph (psi: ProcessStartInfo) calls stage =
    psi.Environment["RECOVERY_INTERRUPT"] <- stage
    use child = Process.Start psi
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    try
        let waiting =
            SpinWait.SpinUntil((fun () -> File.Exists(calls + ".waiting") || child.HasExited), fixtureTimeoutMs)

        check (waiting && not child.HasExited) "Verifier interruption point not reached"
    finally
        if not child.HasExited then
            child.Kill(true)

        child.WaitForExit()
        stdout.Wait()
        stderr.Wait()
        psi.Environment.Remove "RECOVERY_INTERRUPT" |> ignore

        if File.Exists(calls + ".waiting") then
            File.Delete(calls + ".waiting")

let runRalph verifying interrupt =
    let psi, calls = ralphProcess scratch [ "--resume" ]

    if interrupt then
        interruptRalph psi calls "Verify-FUNCTIONAL-"

        let checkpoint =
            JsonNode.Parse(File.ReadAllText(Path.Combine(scratch, "resume.json")))

        check (checkpoint.["Cursor"].["Stage"].GetValue<string>() = "Verify") "Verifier boundary was not durable"

        check
            (checkpoint.["Sprints"].[0].["Status"].GetValue<string>() = "Done")
            "Completed sprint lost before interruption"

        File.Delete calls

    let before = JsonNode.Parse(File.ReadAllText(Path.Combine(scratch, "resume.json")))
    runProcess psi
    let invocations = File.ReadAllText calls
    check (not (invocations.Contains("Architect"))) "Explicit resume replanned"
    check (not (invocations.Contains("Implement-1"))) "Completed sprint reran"
    check (invocations.Contains("Implement-4") <> verifying) "Wrong unfinished stage resumed"
    check (invocations.Contains("\"Verify-FUNCTIONAL-") <> (verifying && not interrupt)) "Wrong verifier stage resumed"
    runProcess psi
    check (File.ReadAllText calls = invocations) "Repeated resume reran completed workflow"
    let after = JsonNode.Parse(File.ReadAllText(Path.Combine(scratch, "resume.json")))

    let expected =
        before.["CompletedIterations"].GetValue<int>() + (if verifying then 0 else 1)

    check (after.["CompletedIterations"].GetValue<int>() = expected) "Resume counted implementation twice"

let cases =
    [ "Node",
      "##",
      "node --test eng\\regression-bisect\\model.test.cjs\nnode --check eng\\regression-bisect\\model.cjs\ngit diff --check"
      "FSharp-helper",
      "##",
      "dotnet fsi tests\\Recovery.Tests.fsx\ndotnet test tests\\Shared.Tests\\DailyMonitor.Shared.Tests.fsproj -c Debug --filter FullyQualifiedName~ExecutorStateTests"
      "Real-smoke",
      "##",
      "Run the controlled-history trial against pinned stable SDK and clean main.\nRecord exact SDK identity, failing endpoint, older passing endpoint, and confirmed culprit.\nAn unavailable SDK is not acceptance."
      "Recovery-sprint",
      "###",
      """Run in the restored dotnet/fsharp worktree, using PowerShell expansion rather than a literal wildcard:

```powershell
$tests = @(Get-ChildItem 'eng\regression-bisect' -Filter '*.test.cjs' | ForEach-Object FullName)
node --test @tests
Get-ChildItem 'eng\regression-bisect' -Filter '*.cjs' -Recurse | ForEach-Object {
    node --check $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Syntax check failed: $($_.FullName)" }
}
git diff --check
```

Run from the task-local Daily Monitor checkout:

```powershell
dotnet test tests\Shared.Tests\DailyMonitor.Shared.Tests.fsproj -c Debug --filter 'FullyQualifiedName~ExecutorStateTests'
```

Run the task-local Groundhog tests without a live agent loop:

```powershell
dotnet fsi tests\Recovery.Tests.fsx
```

#### Completion evidence
Record exact commands, exits, tested SHAs, and locally committed launcher/supervisor SHAs.
No root dotnet/fsharp build/test is required. Unavailable SDK smoke remains unaccepted.""" ]

for name, heading, contract in cases do
    let contract = contract.Replace("\r\n", "\n")

    let source =
        { FilePath = Path.Combine(scratch, name + ".md")
          Order = 1
          Name = name
          Description =
            $"# Sprint: {name}\n\n{heading} Validation scope and completion contract\n{contract}\n\n{heading} Notes\nUnrelated notes.\n\n## Definition of Done\n- All required checks pass; report actual evidence."
          DoD = [ "All required checks pass; report actual evidence." ]
          TargetVerifiers = None
          EliminatedVerifiers = Set.empty }

    File.WriteAllText(source.FilePath, source.Description)
    let sprint = SprintFiles.readSprint source.FilePath |> Option.get

    test $"{name} implementer validation snapshot" (fun () ->
        let prompt = Prompts.implement sprint 1 [] [] [] [] []
        let xml = XElement.Parse prompt
        let validation = xml.Descendants(XName.Get "validation_contract") |> Seq.tryHead

        check
            (validation |> Option.exists (fun el -> el.Value = contract))
            "Exact current-sprint validation contract missing"

        check
            (not (xml.Value.Contains("dotnet build -c Release && dotnet test -c Release")))
            "Unconditional root dotnet pair injected")

    test $"{name} verifier validation snapshot" (fun () ->
        let prompt =
            verifierPreamble
            + Verifiers.getPrompt "FUNCTIONAL"
            + buildSprintVerificationContext sprint Map.empty None "FUNCTIONAL"
            + verifierSuffix

        let marker = "=== VALIDATION SCOPE AND COMPLETION CONTRACT ===\n"
        check (prompt.Contains(marker + contract + "\n")) "Exact current-sprint validation contract missing"
        check (not (prompt.Contains("Use the check errors skill"))) "Unconditional compiler prerequisite injected"

        check
            (not (prompt.Contains("dotnet build -c Release && dotnet test -c Release")))
            "Unconditional root dotnet pair injected")

    test $"{name} architect contract heading snapshot" (fun () ->
        let decorated =
            { sprint with
                Description =
                    sprint.Description.Replace(
                        $"{heading} Validation scope and completion contract",
                        $"{heading} Validation scope and completion contract - exact scoped commands, evidence, and completion requirements"
                    ) }

        check (validationContract decorated = contract) "Architect-required heading lost validation contract")

test "new process resumes after completed sprint" (fun () ->
    runWorker "save-completed"
    runWorker "resume-completed"
    runRalph false false)

test "new process resumes before verifier result" (fun () ->
    runWorker "save-verifying"
    runWorker "resume-verifying"
    runRalph true false)

test "killed process resumes pending verifier without repeating implementation" (fun () ->
    runWorker "save-completed"
    runRalph true true)

test "new process resumes partial final verification" (fun () ->
    runWorker "save-final"
    runWorker "resume-final"
    runRalph true false

    check
        (not (File.ReadAllText(Path.Combine(scratch, "fake-agent-calls.log")).Contains("FinalVerify-FUNCTIONAL-")))
        "Completed final verifier reran")

for stage in [ "Implement"; "Verify" ] do
    test $"loop: fresh-process resume after arbiter reload at {stage} preserves saved descendant" (fun () ->
        let directory = Path.Combine(scratch, "arbiter-" + stage)
        let workDir = Path.Combine(directory, "product")
        Directory.CreateDirectory workDir |> ignore

        let git args =
            let psi =
                ProcessStartInfo(
                    "git",
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                )

            for arg in [ "-c"; "core.hooksPath=NUL" ] @ args do
                psi.ArgumentList.Add arg

            use child = Process.Start psi
            let stdout = child.StandardOutput.ReadToEndAsync()
            let stderr = child.StandardError.ReadToEndAsync()

            if not (child.WaitForExit(fixtureTimeoutMs)) then
                child.Kill(true)
                child.WaitForExit()
                failwith "Fixture Git command timed out"

            check (child.ExitCode = 0) $"Fixture Git failed: {stderr.Result}"
            stdout.Result.Trim()

        git [ "init"; "--quiet" ] |> ignore
        git [ "config"; "user.name"; "Recovery fixture" ] |> ignore
        git [ "config"; "user.email"; "recovery@example.invalid" ] |> ignore
        let source = Path.Combine(workDir, "saved.bin")
        File.WriteAllBytes(source, [| 0uy; 128uy; 255uy |])
        git [ "add"; "saved.bin" ] |> ignore

        git [ "-c"; "commit.gpgsign=false"; "commit"; "--quiet"; "-m"; "saved" ]
        |> ignore

        let saved = git [ "rev-parse"; "HEAD" ]
        File.AppendAllText(Path.Combine(workDir, "descendant.txt"), "real saved-descendant bytes")
        git [ "add"; "descendant.txt" ] |> ignore

        git [ "-c"; "commit.gpgsign=false"; "commit"; "--quiet"; "-m"; "descendant" ]
        |> ignore

        let head = git [ "rev-parse"; "HEAD" ]
        check (head <> saved) "Fixture did not create a saved descendant"
        git [ "merge-base"; "--is-ancestor"; saved; head ] |> ignore

        let bytes =
            Directory.GetFiles(workDir) |> Array.map (fun p -> p, File.ReadAllBytes p)

        let psi, calls = ralphProcess directory []
        psi.WorkingDirectory <- workDir
        psi.Environment["RALPH_WORK_DIR"] <- workDir
        psi.Environment["RECOVERY_ARBITER"] <- "1"

        interruptRalph
            psi
            calls
            (if stage = "Implement" then
                 "Implement-9-"
             else
                 "Verify-FUNCTIONAL-")

        let checkpoint = Path.Combine(directory, "resume.json")
        let before = JsonNode.Parse(File.ReadAllText checkpoint)

        let archives =
            Directory.GetDirectories(Path.Combine(directory, "archives"))
            |> Array.filter (fun p -> File.Exists(Path.Combine(p, "resume.json")))

        check (archives.Length = 1) "Expected exactly one checkpoint archive before arbiter mutation"

        let archived =
            JsonNode.Parse(File.ReadAllText(Path.Combine(archives[0], "resume.json")))

        let archiveBytes =
            Directory.GetFiles(archives[0], "*", SearchOption.AllDirectories)
            |> Array.map (fun p -> p, File.ReadAllBytes p)

        check (before.["Request"].GetValue<string>() = "Recovery test request") "Request identity changed"
        check (before.["WorkDir"].GetValue<string>() = workDir) "Checkpoint escaped the fixture worktree"
        check (before.["Cursor"].["Stage"].GetValue<string>() = stage) "Replacement cursor not durable"
        check (before.["Sprints"].AsArray().Count = 2) "Arbiter retained the obsolete pending sprint"
        check (before.["Sprints"].[1].["Path"].GetValue<string>().EndsWith("09_Saved.md")) "Arbiter plan not reloaded"

        check
            (before.["BacklogHash"].ToJsonString() <> archived.["BacklogHash"].ToJsonString())
            "Backlog hash not reloaded"

        check (git [ "rev-parse"; "HEAD" ] = head) "Arbiter changed saved-descendant HEAD"
        check (File.ReadAllText(calls).Contains("\"Arbiter-")) "Real arbiter dispatch did not run"
        let resume, resumedCalls = ralphProcess directory [ "--resume" ]
        resume.WorkingDirectory <- workDir
        resume.Environment["RALPH_WORK_DIR"] <- workDir
        runProcess resume
        let afterText = File.ReadAllText checkpoint
        let after = JsonNode.Parse afterText

        for field in [ "Request"; "WorkDir"; "StartTime" ] do
            check (after.[field].ToJsonString() = archived.[field].ToJsonString()) $"Resume reset {field}"

        check (after.["Phase"].GetValue<string>() = "Complete") "Resumed replacement plan did not complete"
        check (after.["Sprints"].AsArray().Count = 2) "Resume invented archived sprint completion"

        for state in [ before; after ] do
            check
                (state.["Sprints"].[0].ToJsonString() = archived.["Sprints"].[0].ToJsonString())
                "Accepted sprint reset"

        check
            (after.["CompletedIterations"].GetValue<int>() = archived.["CompletedIterations"].GetValue<int>() + 1)
            "Resume reset or recounted archived iterations"

        let invocations = File.ReadAllText resumedCalls

        for forbidden in [ "Architect-"; "Arbiter-"; "Implement-1-"; "Implement-4-" ] do
            check (not (invocations.Contains forbidden)) $"Resume repeated {forbidden}"

        check (invocations.Contains("Implement-9-") = (stage = "Implement")) "Wrong replacement stage resumed"
        runProcess resume
        check (File.ReadAllText checkpoint = afterText) "Repeated resume changed checkpoint"
        check (File.ReadAllText resumedCalls = invocations) "Repeated resume invoked agents"
        check (git [ "rev-parse"; "HEAD" ] = head) "Resume reset saved-descendant HEAD"
        check (git [ "status"; "--porcelain=v1"; "--untracked-files=all" ] = "") "Resume changed product status"

        for path, expected in Array.append bytes archiveBytes do
            check (File.ReadAllBytes path = expected) $"Resume changed preserved bytes: {path}")

for final in [ false; true ] do
    let phase = if final then "final" else "sprint"

    test $"loop: fresh-process resume retains failed {phase} verification" (fun () ->
        let directory = Path.Combine(scratch, $"failed-{final}")
        Directory.CreateDirectory directory |> ignore
        runWorkerAt directory (if final then "save-final-pending" else "save-completed")
        let psi, calls = ralphProcess directory [ "--resume" ]
        let prefix = if final then "FinalVerify-" else "Verify-"
        psi.Environment["RECOVERY_FAIL_ONCE"] <- prefix + "FUNCTIONAL-"
        interruptRalph psi calls (prefix + "TESTS-")
        let checkpoint = Path.Combine(directory, "resume.json")
        let before = JsonNode.Parse(File.ReadAllText checkpoint)

        let results =
            if final then
                before.["FinalResults"]
            else
                before.["Sprints"].[1].["History"].[1].["Results"]

        check
            (not (results.[0].["Passed"].GetValue<bool>()))
            "Failed result was not durable before process termination"

        check
            (results.[0].["Summary"].GetValue<string>().Contains("retained failure evidence"))
            "Failure evidence missing"

        File.Delete calls
        runProcess psi
        let afterText = File.ReadAllText checkpoint
        let after = JsonNode.Parse afterText
        let expected = before.["CompletedIterations"].GetValue<int>() + 1

        check
            (after.["CompletedIterations"].GetValue<int>() = expected)
            "Failed verification counted an implementation twice"

        check (after.["Phase"].GetValue<string>() = "Complete") "Recovery did not complete"
        check (after.["Sprints"].[0].ToJsonString() = before.["Sprints"].[0].ToJsonString()) "Completed sprint changed"
        let invocations = File.ReadAllLines calls

        check
            (invocations |> Array.filter (fun s -> s.Contains("Implement-")) |> Array.length = 1)
            "Wrong implementation retry count"

        check
            (invocations
             |> Array.filter (fun s -> s.Contains($"\"{prefix}FUNCTIONAL-"))
             |> Array.length = 1)
            "Recorded failed verifier reran before retry"

        if final then
            let fixup = after.["Sprints"].[2]

            check
                (fixup.["Targets"].ToJsonString() = "[\"FUNCTIONAL\"]")
                "Failed final result did not create a targeted fixup"

            check
                (File.ReadAllText(fixup.["Path"].GetValue<string>()).Contains("retained failure evidence"))
                "Fixup lost failure evidence"
        else
            let sprint = after.["Sprints"].[1]
            check (sprint.["History"].AsArray().Count = 3) "Retry lost or duplicated iteration history"

            check
                (sprint.["History"].[1].["Results"].[0].ToJsonString() = results.[0].ToJsonString())
                "Retry overwrote failed verifier evidence"

            check (sprint.["History"].[2].["Iteration"].GetValue<int>() = 2) "Retry reset iteration number"
            check (sprint.["Reasons"].AsArray().Count = 1) "Retry reason lost or duplicated"

        runProcess psi
        check (File.ReadAllText checkpoint = afterText) "Repeated successful resume changed checkpoint"
        check (File.ReadAllLines calls = invocations) "Repeated successful resume invoked agents")

test "loop: unsuccessful completion remains unsuccessful on repeated resume" (fun () ->
    let directory = Path.Combine(scratch, "unsuccessful")
    Directory.CreateDirectory directory |> ignore
    runWorkerAt directory "save-final-pending"
    let psi, calls = ralphProcess directory [ "--resume" ]
    psi.Environment["RECOVERY_FAIL_FINAL"] <- "1"
    runProcessExpect 1 psi
    let checkpoint = Path.Combine(directory, "resume.json")
    let before = JsonNode.Parse(File.ReadAllText checkpoint)
    check (before.["Sprints"].AsArray().Count = 5) "Final failure did not exhaust exactly three fixups"
    check (before.["CompletedIterations"].GetValue<int>() = 5) "Fixups lost iteration accounting"

    check
        (before.["FinalResults"].AsArray()
         |> Seq.exists (fun r -> not (r.["Passed"].GetValue<bool>())))
        "Unsuccessful completion lost failed verdict"

    let invocations = File.ReadAllText calls

    for _ in 1..2 do
        runProcessExpect 1 psi
        let after = JsonNode.Parse(File.ReadAllText checkpoint)
        check (after.ToJsonString() = before.ToJsonString()) "Repeated unsuccessful resume changed durable outcome"
        check (after.["Phase"].GetValue<string>() <> "Complete") "Failure converted to successful completion"
        check (File.ReadAllText calls = invocations) "Exhausted failure invoked agents again")

test "loop: incomplete implementation retry counts each invocation once" (fun () ->
    let directory = Path.Combine(scratch, "incomplete")
    Directory.CreateDirectory directory |> ignore
    runWorkerAt directory "save-completed"
    let psi, calls = ralphProcess directory [ "--resume" ]
    psi.Environment["RECOVERY_INCOMPLETE_ONCE"] <- "1"
    runProcess psi
    let after = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "resume.json")))
    check (after.["CompletedIterations"].GetValue<int>() = 4) "Incomplete implementation was not counted exactly once"
    check (after.["Sprints"].[1].["History"].AsArray().Count = 3) "Incomplete retry lost history"

    check
        (after.["Sprints"].[1].["History"].[1].["Output"].GetValue<string>().Contains("SUBTASK_INCOMPLETE"))
        "Incomplete outcome overwritten"

    check (after.["Sprints"].[1].["Reasons"].AsArray().Count = 1) "Incomplete retry lost reason"

    check
        (File.ReadAllLines calls
         |> Array.filter (fun s -> s.Contains("Implement-4"))
         |> Array.length = 2)
        "Incomplete retry invoked wrong implementations")

for restart in [ false; true ] do
    for archiveFails in [ false; true ] do
        let phase = if restart then "restart" else "fresh planning"

        test $"loop: {phase} archives before mutation (archive fails: {archiveFails})" (fun () ->
            let directory = Path.Combine(scratch, $"archive-{restart}-{archiveFails}")
            Directory.CreateDirectory directory |> ignore
            runWorkerAt directory "save-verifying"
            File.WriteAllText(Path.Combine(directory, "ralph.log"), "old evidence")
            let expected = Path.Combine(directory, "expected")
            Directory.CreateDirectory(Path.Combine(expected, "sprints")) |> ignore

            let files =
                [ "BACKLOG.md"
                  "resume.json"
                  "ralph.log"
                  "sprints\\01_Saved.md"
                  "sprints\\04_Saved.md" ]

            for file in files do
                File.Copy(Path.Combine(directory, file), Path.Combine(expected, file))

            if archiveFails then
                File.WriteAllText(Path.Combine(directory, "archives"), "archive blocked")

            let psi, calls = ralphProcess directory (if restart then [ "--restart" ] else [])
            psi.Environment["RECOVERY_REPLAN"] <- "1"
            runProcessExpect (if archiveFails then 1 else 0) psi

            if archiveFails then
                check (not (File.Exists calls)) "Archive failure invoked an agent"

                for file in files do
                    check
                        (File.ReadAllBytes(Path.Combine(directory, file)) = File.ReadAllBytes(
                            Path.Combine(expected, file)
                        ))
                        $"Archive failure mutated {file}"
            else
                let archive = File.ReadAllText(Path.Combine(directory, "archive-checked"))

                for file in files do
                    check
                        (File.ReadAllBytes(Path.Combine(archive, file)) = File.ReadAllBytes(
                            Path.Combine(expected, file)
                        ))
                        $"Archived {file} changed"

                check
                    (not (File.Exists(Path.Combine(directory, "sprints", "01_Saved.md"))))
                    "Replanning did not replace old plan"

                let after = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "resume.json")))
                check (after.["Phase"].GetValue<string>() = "Complete") "Replanned workflow did not execute"
                check (after.["Sprints"].AsArray().Count = 1) "Replanned workflow retained stale sprints")

test "explicit resume validates identity and preserves plans" (fun () -> runWorker "validate")

for mode in [ "reject-multiple-running"; "reject-premature-final" ] do
    test mode (fun () -> runWorker mode)

test "archive helper copies saved state" (fun () -> runWorker "archive")
test "capture retains stdout stderr phase and exit" (fun () -> runWorker "capture-exit")
test "noisy child is bounded by hard phase budget" (fun () -> runWorker "capture-noisy")
test "idle child process tree is killed with stderr retained" (fun () -> runWorker "capture-idle")

for inherited in [ "stdout"; "stderr"; "both"; "neither" ] do
    test $"boundary parent-first {inherited}" (fun () -> runWorker ("capture-parent-first-" + inherited))

test "boundary drain failure retains receipt" (fun () -> runWorker "capture-drain-failure")

for mode in [ "complete"; "closed"; "dropped"; "truncated" ] do
    test $"boundary stdin: actual runAgent {mode}" (fun () ->
        let directory = Path.Combine(scratch, "stdin-" + mode)
        let fakeDir = Path.Combine(directory, ".tools", ".npm-global")
        Directory.CreateDirectory fakeDir |> ignore

        let launcher =
            if mode = "dropped" || mode = "truncated" then
                let copy = Path.Combine(directory, "launcher")
                Directory.CreateDirectory(Path.Combine(copy, "tests")) |> ignore

                for file in Directory.GetFiles(root, "*.fsx") do
                    File.Copy(file, Path.Combine(copy, Path.GetFileName file))

                for folder in [ "templates"; "verifiers" ] do
                    Directory.CreateDirectory(Path.Combine(copy, folder)) |> ignore

                    for file in Directory.GetFiles(Path.Combine(root, folder)) do
                        File.Copy(file, Path.Combine(copy, folder, Path.GetFileName file))

                File.Copy(
                    Path.Combine(__SOURCE_DIRECTORY__, "PromptDelivery.Worker.fsx"),
                    Path.Combine(copy, "tests", "PromptDelivery.Worker.fsx")
                )

                let path = Path.Combine(copy, "Ralph.fsx")
                let source = File.ReadAllText path

                let replacement =
                    if mode = "dropped" then
                        "(Some \"\")"
                    else
                        "(Some (prompt.Substring(0, 1024)))"

                let mutated = source.Replace("(Some prompt)", replacement)
                check (source <> mutated) "Stdin mutation did not target the production handoff"
                File.WriteAllText(path, mutated)
                copy
            else
                root

        let fakeCli = Path.Combine(__SOURCE_DIRECTORY__, "PromptDelivery.Cli.ps1")

        File.WriteAllText(
            Path.Combine(fakeDir, "copilot.cmd"),
            $"@echo off\r\npwsh -NoProfile -File \"{fakeCli}\" %%*\r\nexit /b %%errorlevel%%\r\n"
        )

        let psi =
            ProcessStartInfo(
                "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        for arg in
            [ "fsi"
              Path.Combine(launcher, "tests", "PromptDelivery.Worker.fsx")
              "--check" ] do
            psi.ArgumentList.Add arg

        psi.Environment["DM_BASE_DIR"] <- directory
        psi.Environment["RALPH_WORK_DIR"] <- root
        psi.Environment["RALPH_STATE_DIR"] <- directory
        psi.Environment["RECOVERY_PROMPT_DIRECTORY"] <- directory
        psi.Environment["RECOVERY_PROMPT_MODE"] <- mode
        psi.Environment["RALPH_IDLE_SECONDS"] <- "15"
        psi.Environment["RALPH_PHASE_SECONDS"] <- "30"

        for name in
            [ "GH_TOKEN"
              "GITHUB_TOKEN"
              "COPILOT_GITHUB_TOKEN"
              "GH_ENTERPRISE_TOKEN"
              "GITHUB_ENTERPRISE_TOKEN" ] do
            psi.Environment.Remove name |> ignore

        runProcessExpect (if mode = "dropped" || mode = "truncated" then 1 else 0) psi

        if mode = "dropped" || mode = "truncated" then
            check
                (File.ReadAllText(Path.Combine(directory, "mismatch.txt")).StartsWith("PROMPT_BYTES_MISMATCH"))
                "Mutation was not rejected by the EOF-reading CLI"

            check
                (File.ReadAllBytes(Path.Combine(directory, "received.bin"))
                 <> File.ReadAllBytes(Path.Combine(directory, "expected.bin")))
                "Stdin mutation did not corrupt bytes"
        else
            check (File.Exists(Path.Combine(directory, "invocation.json"))) "Fake CLI did not execute")

if failed = 0 then
    for file in Directory.GetFiles(scratch, "*", SearchOption.AllDirectories) do
        File.SetAttributes(file, FileAttributes.Normal)

    Directory.Delete(scratch, true)
else
    printfn "Failure evidence retained: %s" scratch

printfn "Recovery tests: %d passed, %d failed" passed failed

if failed > 0 then
    exit 1
