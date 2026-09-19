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

let scratch =
    Path.Combine(root, ".tools", "recovery-tests", Guid.NewGuid().ToString("N"))

Directory.CreateDirectory scratch |> ignore
let mutable passed = 0
let mutable failed = 0

let check condition message =
    if not condition then
        failwith message

let test name action =
    try
        action ()
        passed <- passed + 1
        printfn "PASS %s" name
    with ex ->
        failed <- failed + 1
        printfn "FAIL %s: %s" name ex.Message

let runProcess (psi: ProcessStartInfo) =
    use child = Process.Start psi
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit(60000)) then
        child.Kill(true)
        child.WaitForExit()
        failwith "Fixture exceeded 60 second test budget"

    check (child.ExitCode = 0) $"Fixture exit {child.ExitCode}: {stdout.Result}\n{stderr.Result}"

let runWorker mode =
    let psi =
        ProcessStartInfo("dotnet", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    for arg in
        [ "fsi"
          Path.Combine(__SOURCE_DIRECTORY__, "Recovery.Worker.fsx")
          mode
          scratch ] do
        psi.ArgumentList.Add arg

    psi.Environment["RALPH_WORK_DIR"] <- root
    psi.Environment["RALPH_STATE_DIR"] <- scratch
    runProcess psi

let runRalph verifying interrupt =
    let fakeDir = Path.Combine(scratch, ".tools", ".npm-global")
    Directory.CreateDirectory fakeDir |> ignore
    let calls = Path.Combine(scratch, "fake-agent-calls.log")

    if File.Exists calls then
        File.Delete calls

    File.WriteAllText(
        Path.Combine(fakeDir, "copilot.cmd"),
        $"@echo off\r\necho %%*>>\"{calls}\"\r\nif defined RECOVERY_INTERRUPT echo %%* | findstr /c:\"Verify-FUNCTIONAL-\" >nul && (echo waiting>\"{calls}.waiting\" & pwsh -NoProfile -Command Start-Sleep -Seconds 120)\r\necho SUBTASK_COMPLETE\r\necho VERIFY_PASSED\r\n"
    )
    // This backend only emits fixed test tokens. No agent or network client is invoked.
    let psi =
        ProcessStartInfo("dotnet", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    for arg in
        [ "fsi"
          Path.Combine(root, "Ralph.fsx")
          "Recovery test request"
          "--headless"
          "--yes"
          "--resume" ] do
        psi.ArgumentList.Add arg

    psi.Environment["RALPH_WORK_DIR"] <- root
    psi.Environment["RALPH_STATE_DIR"] <- scratch
    psi.Environment["DM_BASE_DIR"] <- scratch

    if interrupt then
        psi.Environment["RECOVERY_INTERRUPT"] <- "1"
        use child = Process.Start psi
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        try
            let waiting =
                SpinWait.SpinUntil((fun () -> File.Exists(calls + ".waiting") || child.HasExited), 60000)

            check (waiting && not child.HasExited) "Verifier interruption point not reached"
        finally
            if not child.HasExited then
                child.Kill(true)

            child.WaitForExit()
            stdout.Wait()
            stderr.Wait()

        psi.Environment.Remove "RECOVERY_INTERRUPT" |> ignore

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
      "node --test eng\\regression-bisect\\model.test.cjs\nnode --check eng\\regression-bisect\\model.cjs\ngit diff --check"
      "FSharp-helper",
      "dotnet fsi tests\\Recovery.Tests.fsx\ndotnet test tests\\Shared.Tests\\DailyMonitor.Shared.Tests.fsproj -c Debug --filter FullyQualifiedName~ExecutorStateTests"
      "Real-smoke",
      "Run the controlled-history trial against pinned stable SDK and clean main.\nRecord exact SDK identity, failing endpoint, older passing endpoint, and confirmed culprit.\nAn unavailable SDK is not acceptance." ]

for name, contract in cases do
    let source =
        { FilePath = Path.Combine(scratch, name + ".md")
          Order = 1
          Name = name
          Description =
            $"# Sprint: {name}\n\n## Validation scope and completion contract\n{contract}\n\n## Notes\nUnrelated notes.\n\n## Definition of Done\n- All required checks pass; report actual evidence."
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
                        "## Validation scope and completion contract",
                        "## Validation scope and completion contract - exact scoped commands, evidence, and completion requirements"
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

test "explicit resume validates identity and preserves plans" (fun () -> runWorker "validate")

for mode in [ "reject-multiple-running"; "reject-premature-final" ] do
    test mode (fun () -> runWorker mode)

test "archive retains prior state before replanning" (fun () -> runWorker "archive")
test "capture retains stdout stderr phase and exit" (fun () -> runWorker "capture-exit")
test "noisy child is bounded by hard phase budget" (fun () -> runWorker "capture-noisy")
test "idle child process tree is killed with stderr retained" (fun () -> runWorker "capture-idle")

Directory.Delete(scratch, true)
printfn "Recovery tests: %d passed, %d failed" passed failed

if failed > 0 then
    exit 1
