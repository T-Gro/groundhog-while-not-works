#load "..\\Utils.fsx"
#load "..\\TypeDefinitions.fsx"
#load "..\\Prompting.fsx"
#load "..\\Recovery.fsx"
#load "Capture.ParentFirst.fsx"

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open TypeDefinitions
open Prompting
open Recovery

let mode = fsi.CommandLineArgs[1]
let root = fsi.CommandLineArgs[2]

let check condition message =
    if not condition then
        failwith message

let request = "Recovery test request"
let checkpoint = Path.Combine(root, "resume.json")
let sprintDir = Path.Combine(root, "sprints")

let makeSprint order =
    Directory.CreateDirectory sprintDir |> ignore
    let path = Path.Combine(sprintDir, $"{order:D2}_Saved.md")
    File.WriteAllText(path, $"# Sprint: {order}\n\n## Definition of Done\n- Tested")
    SprintFiles.readSprint path |> Option.get

let assertReject action =
    let mutable rejected = false

    try
        action ()
    with _ ->
        rejected <- true

    check rejected "Invalid resume accepted"

let sample verifying =
    let first, second = makeSprint 1, makeSprint 4
    File.WriteAllText(Path.Combine(root, "BACKLOG.md"), "Historical sprint files 2 and 3 are missing, not completed.")

    let timing =
        { emptyTiming with
            IterationHistory =
                [ { Iteration = 3
                    AgentOutput = "SUBTASK_COMPLETE"
                    VerifierResults = [ "FUNCTIONAL", true, "checked" ] } ] }

    let state =
        { emptyState with
            Backlog =
                [ first, Done 2, emptyTiming
                  second, (if verifying then Running(Implement, 3) else Todo), timing ]
            CurrentPhase = "Executing"
            CompletedIterations = if verifying then 5 else 2 }

    let cursor =
        if verifying then
            Some
                { SprintPath = second.FilePath
                  Iteration = 3
                  Stage = "Verify" }
        else
            None

    Resume.save checkpoint request state cursor

match mode with
| "capture-parent-first-stdout"
| "capture-parent-first-stderr"
| "capture-parent-first-both"
| "capture-parent-first-neither" -> Capture.ParentFirst.run root (mode.Substring("capture-parent-first-".Length))
| "save-completed" -> sample false
| "save-verifying" -> sample true
| "save-final"
| "save-final-pending" ->
    sample false
    let state, _ = Resume.load checkpoint request

    let finalState =
        { state with
            Backlog = state.Backlog |> List.map (fun (item, _, timing) -> item, Done 3, timing)
            CurrentPhase = "Final Verification"
            FinalVerifierResults =
                if mode = "save-final" then
                    Map.ofList [ "FUNCTIONAL", Passed 1 ]
                else
                    Map.empty
            FinalVerifierSummaries =
                if mode = "save-final" then
                    Map.ofList [ "FUNCTIONAL", "checked final" ]
                else
                    Map.empty }

    Resume.save checkpoint request finalState None
| "resume-final" ->
    let state, cursor = Resume.load checkpoint request
    check (cursor.IsNone && state.CurrentPhase = "Final Verification") "Final verification phase forgotten"
    check (state.FinalVerifierResults["FUNCTIONAL"] = Passed 1) "Completed final verifier forgotten"
    check (state.FinalVerifierSummaries["FUNCTIONAL"] = "checked final") "Final verifier evidence forgotten"
| "resume-completed"
| "resume-verifying" ->
    let before =
        Directory.GetFiles(sprintDir) |> Array.map (fun p -> p, File.ReadAllText p)

    let state, cursor = Resume.load checkpoint request
    check (state.Backlog.Length = 2) "Invented missing historical sprint completion"

    check
        (match state.Backlog[0] with
         | _, Done 2, _ -> true
         | _ -> false)
        "Completed sprint forgotten"

    if mode = "resume-verifying" then
        check
            (cursor |> Option.exists (fun c -> c.Stage = "Verify" && c.Iteration = 3))
            "Unfinished verifier stage forgotten"

        let _, status, timing = state.Backlog[1]
        check (status = Running(Implement, 3)) "Iteration reset"

        check
            (timing.IterationHistory.Head.VerifierResults = [ "FUNCTIONAL", true, "checked" ])
            "Prior verifier result forgotten"
    else
        check cursor.IsNone "Completed boundary inferred a running stage"

        check
            (match state.Backlog[1] with
             | _, Todo, _ -> true
             | _ -> false)
            "Unfinished sprint incorrectly completed"

    check (before |> Array.forall (fun (p, text) -> File.ReadAllText p = text)) "Resume changed prior plans"
| "validate" ->
    sample true
    assertReject (fun () -> Resume.load checkpoint "different issue request" |> ignore)
    let text = File.ReadAllText checkpoint
    File.WriteAllText(checkpoint, text.Replace("\"Phase\":\"Executing\"", "\"Phase\":\"Complete\""))
    assertReject (fun () -> Resume.load checkpoint request |> ignore)
    File.WriteAllText(checkpoint, text.Replace("\"Stage\":\"Verify\"", "\"Stage\":\"Unknown\""))
    assertReject (fun () -> Resume.load checkpoint request |> ignore)
    File.WriteAllText(checkpoint, text)
    File.WriteAllText(checkpoint, text.Replace(Utils.Config.workDir.Replace("\\", "\\\\"), "Q:\\\\different-worktree"))
    assertReject (fun () -> Resume.load checkpoint request |> ignore)
    File.WriteAllText(checkpoint, text)
    File.AppendAllText(Path.Combine(sprintDir, "01_Saved.md"), "\nchanged")
    assertReject (fun () -> Resume.load checkpoint request |> ignore)
    sample true
    File.Delete(Path.Combine(sprintDir, "04_Saved.md"))
    assertReject (fun () -> Resume.load checkpoint request |> ignore)
    sample true
    assertReject (fun () -> Resume.adopt checkpoint request |> ignore)
    File.Delete checkpoint
    let state, cursor = Resume.adopt checkpoint request
    check cursor.IsNone "Adoption inferred an unfinished stage"
    check (state.Backlog |> List.forall (fun (_, s, _) -> s = Todo)) "Adoption inferred completed work"
| "archive" ->
    sample true
    File.WriteAllText(Path.Combine(root, "ralph.log"), "old evidence")
    let archive = Resume.archive root
    check (File.ReadAllText(Path.Combine(archive, "ralph.log")) = "old evidence") "Log not archived"
    check (File.Exists(Path.Combine(archive, "resume.json"))) "Checkpoint not archived"
    check (File.Exists(Path.Combine(archive, "sprints", "01_Saved.md"))) "Plan not archived"
    check (File.Exists checkpoint) "Archiving deleted current checkpoint"
| "replan" ->
    let archives = Directory.GetDirectories(Path.Combine(root, "archives"))
    check (archives.Length = 1) "Architect reached without one pre-mutation archive"
    let expected = Path.Combine(root, "expected")

    for file in Directory.GetFiles(expected, "*", SearchOption.AllDirectories) do
        let relative = Path.GetRelativePath(expected, file)

        check
            (File.ReadAllBytes(Path.Combine(archives[0], relative)) = File.ReadAllBytes file)
            $"Architect reached before archiving {relative}"

    check (Directory.GetFiles(sprintDir, "*.md").Length = 0) "Architect reached before stale plans cleared"
    File.WriteAllText(Path.Combine(root, "archive-checked"), archives[0])
    File.WriteAllText(Path.Combine(root, "BACKLOG.md"), "Replacement plan")
    makeSprint 9 |> ignore
| "reject-multiple-running"
| "reject-premature-final" ->
    sample true
    let text = File.ReadAllText checkpoint

    let corrupt =
        if mode = "reject-multiple-running" then
            text.Replace("\"Status\":\"Done\",\"Iteration\":2", "\"Status\":\"Running\",\"Iteration\":2")
        else
            text.Replace("\"Phase\":\"Executing\"", "\"Phase\":\"Final Verification\"")

    check (text <> corrupt) "Fixture did not corrupt checkpoint"
    File.WriteAllText(checkpoint, corrupt)
    assertReject (fun () -> Resume.load checkpoint request |> ignore)
| "capture-exit"
| "capture-drain-failure"
| "capture-noisy"
| "capture-idle" ->
    let childFile = Path.Combine(root, "child.ps1")

    let body =
        match mode with
        | "capture-exit" ->
            "[Console]::Out.WriteLine('stdout-evidence'); [Console]::Error.WriteLine('stderr-evidence'); exit 7"
        | "capture-drain-failure" ->
            let output = Path.Combine(root, mode + ".stdout.log").Replace("'", "''")

            $"""
[Console]::Out.WriteLine('stdout-evidence')
[Console]::Error.WriteLine('stderr-evidence')
$watch = [Diagnostics.Stopwatch]::StartNew()
do {{
    $stream = [IO.File]::Open('{output}', 'Open', 'Read', 'ReadWrite')
    $reader = [IO.StreamReader]::new($stream)
    try {{ $ready = $reader.ReadToEnd().Contains('stdout-evidence') }} finally {{ $reader.Dispose() }}
    if ($watch.Elapsed.TotalSeconds -gt 3) {{ throw 'Capture readiness timed out' }}
}} until ($ready)
[Console]::OpenStandardOutput().WriteByte(255)
exit 7
"""
        | "capture-noisy" ->
            "[Console]::Error.WriteLine('stderr-evidence'); while ($true) { [Console]::Out.WriteLine('noise'); Start-Sleep -Milliseconds 20 }"
        | _ ->
            let pidFile = Path.Combine(root, "grandchild.pid").Replace("'", "''")
            $"$p = Start-Process pwsh -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 120' -PassThru; [IO.File]::WriteAllText('{pidFile}', [string]$p.Id); [Console]::Error.WriteLine('stderr-evidence'); Start-Sleep -Seconds 120"

    File.WriteAllText(childFile, body)
    let psi = ProcessStartInfo("pwsh", UseShellExecute = false)

    for arg in [ "-NoProfile"; "-File"; childFile ] do
        psi.ArgumentList.Add arg

    if mode = "capture-drain-failure" then
        psi.StandardOutputEncoding <- System.Text.UTF8Encoding(false, true)

    let budget: ProcessCapture.Budget =
        { Idle = TimeSpan.FromSeconds 2.0
          Hard = TimeSpan.FromSeconds 5.0 }

    let result =
        ProcessCapture.runCaptured psi "test-phase" budget (Path.Combine(root, mode)) None

    check (result.Phase = "test-phase") "Phase lost"
    check (result.Stderr.Contains "stderr-evidence") "Stderr lost"
    check (File.ReadAllText(result.StderrPath).Contains "stderr-evidence") "Durable stderr lost"

    let receipt =
        JsonSerializer.Deserialize<ProcessCapture.Result>(File.ReadAllText result.ResultPath)

    check (receipt = result) "Durable capture receipt differs from the returned child result"
    check result.ExitCode.HasValue "Child exit status lost"

    check (result.InputCompleted && result.OwnedProcessesStopped) "Capture lifecycle did not complete"

    if mode <> "capture-drain-failure" then
        check (result.StreamsDrained && result.Failures.Length = 0) "Capture streams did not complete cleanly"

    match mode with
    | "capture-exit" ->
        check (result.ExitCode.Value = 7 && result.Reason = "Exited") "Natural exit lost"
        check (result.Stdout.Contains "stdout-evidence") "Stdout lost"
    | "capture-drain-failure" ->
        check (result.ExitCode.Value = 7 && result.Reason = "DrainFailed") "Drain failure hid actual child exit"
        check (result.Stdout = "stdout-evidence" + Environment.NewLine) "Partial stdout lost"
        check (File.ReadAllText(result.StdoutPath) = result.Stdout) "Durable partial stdout differs"
        check (not result.StreamsDrained && result.Failures.Length = 1) "Failed stream reported clean completion"
        check (result.Failures[0].StartsWith("DrainFailed:")) "Drain failure evidence lost"
        check (result.Elapsed < TimeSpan.FromSeconds 12.) "Drain failure exceeded cleanup grace"
    | "capture-noisy" ->
        check (result.Reason = "HardBudget") "Noise renewed absolute budget"
        check (result.Elapsed < TimeSpan.FromSeconds 12.0) "Hard budget not bounded"
    | _ ->
        check (result.Reason = "IdleLease") "Idle lease not enforced"
        let pid = File.ReadAllText(Path.Combine(root, "grandchild.pid")) |> int

        let running =
            try
                use p = Process.GetProcessById pid
                not p.HasExited
            with :? ArgumentException ->
                false

        check (not running) "Grandchild survived process-tree termination"
| _ -> failwith $"Unknown mode {mode}"
