#r "nuget: YamlDotNet"
#r "nuget: Fli"
#r "nuget: Spectre.Console"
#r "nuget: FSharp.SystemTextJson, 1.4.36"
#load "Ralph.fsx"

open System
open System.IO
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Serialization
open Utils
open TypeDefinitions
open Ralph

let equal expected actual =
    if actual <> expected then failwith $"Expected {expected}, actual {actual}"

let blockedOutput = """SUBTASK_BLOCKED {"reason":"Missing owner launch","retryCondition":"owner-launch-enrolled"}"""
let options = JsonSerializerOptions()
options.Converters.Add(JsonFSharpConverter())
dashboardDisabled <- true

let setup partial =
    if Directory.Exists Config.ralphDir then Directory.Delete(Config.ralphDir, true)
    Directory.CreateDirectory Config.sprintsDir |> ignore
    let sprintPath = Path.Combine(Config.sprintsDir, "02_Blocked.md")
    File.WriteAllText(sprintPath, "# Sprint: Blocked\n\n## Definition of Done\n- Owner enrolled\n")
    let item = { (Prompting.SprintFiles.readSprint sprintPath |> Option.get) with TargetVerifiers = Some [] }
    let completed = { item with FilePath = Path.Combine(Config.sprintsDir, "01_Done.md"); Order = 1; Name = "Done" }
    File.WriteAllText(completed.FilePath, "# Completed sprint\n")
    let history = { Iteration = 1; AgentOutput = "original attempt"; VerifierResults = ["FUNCTIONAL", true, "passed"] }
    let timing = { emptyTiming with IterationHistory = [history]; Summary = Some "Original completed history" }
    state <- { emptyState with Backlog = (if partial then [completed, Done 1, timing] else []) @ [item, Todo, emptyTiming] }
    item

let dirty = Path.Combine(Config.workDir, "tracked.txt")
File.AppendAllText(dirty, "dirty\n")
File.WriteAllText(Path.Combine(Config.workDir, "untracked.txt"), "untracked\n")

for partial in [false; true] do
    for output in [blockedOutput; blockedOutput + "\nSUBTASK_COMPLETE"; "SUBTASK_BLOCKED broken-json"; blockedOutput + "\n" + blockedOutput] do
        let item = setup partial
        let mutable checkpoint = BlockedProtocol.checkpoint Config.workDir Config.ralphDir
        let mutable calls = []
        agentRunner <- fun _ title _ _ -> async {
            calls <- calls @ [title]
            if partial then
                File.AppendAllText(dirty, "implementer partial work\n")
                File.WriteAllText(Path.Combine(Config.workDir, "partial-untracked.txt"), "retained partial work\n")
                checkpoint <- BlockedProtocol.checkpoint Config.workDir Config.ralphDir
            return output, "fixture"
        }
        let block =
            match runWithLive [item] false "fixture" with
            | Block block -> block
            | actual -> failwith $"Expected typed blocked result, got {actual}"
        equal ["Implement-2"] calls
        equal checkpoint (BlockedProtocol.checkpoint Config.workDir Config.ralphDir)
        let snapshotPath = Path.Combine(Config.ralphDir, "scheduler-state.json")
        let snapshot = File.ReadAllText snapshotPath
        let saved = JsonSerializer.Deserialize<State>(snapshot, options)
        equal state saved
        equal 1 (saved.Backlog |> List.sumBy (fun (_, status, _) -> match status with Blocked _ -> 1 | _ -> 0))
        equal partial (saved.Backlog |> List.exists (fun (_, status, timing) -> status = Done 1 && timing.Summary = Some "Original completed history"))
        GUI.GUI.buildDashboard saved [] |> ignore
        equal 42 (runAllBacklogItems [item] false |> Async.RunSynchronously |> dispatchExit)
        state <- emptyState // caller/scheduler restart, with no in-memory history
        for dispatch in [
            (fun () -> run "fixture" false true 0 None)
            (fun () -> runRestart "fixture" false true)
            (fun () -> runWithPush "fixture" false true 0)
            (fun () -> runWithCIContext "fixture" false true 0 "CI failure")
        ] do equal 42 (dispatch ())
        match invokeArbiter "fixture" false with
        | Block again -> equal block again
        | result -> failwith $"Arbiter bypassed blocked result: {result}"
        equal ["Implement-2"] calls
        equal snapshot (File.ReadAllText snapshotPath)
        equal checkpoint (BlockedProtocol.checkpoint Config.workDir Config.ralphDir)
        printfn "PASS blocked partial=%b malformed=%b; one implementer, zero downstream effects" partial (output <> blockedOutput)

for output in [
    "SUBTASK_INCOMPLETE"
    "SUBTASK_COMPLETE"
    "Implemented SUBTASK_BLOCKED support.\nSUBTASK_COMPLETE"
    "\"SUBTASK_BLOCKED\" is a documented token.\nSUBTASK_COMPLETE"
] do
    let success = output <> "SUBTASK_INCOMPLETE"
    let item = setup false
    let mutable calls = 0
    agentRunner <- fun _ _ _ _ -> async {
        calls <- calls + 1
        return output, "fixture"
    }
    match runAllBacklogItems [item] false |> Async.RunSynchronously with
    | Complete () when success -> equal 1 calls
    | Retry error when not success ->
        equal Config.ArbiterThreshold calls
        if not (error.Contains "ARBITER_NEEDED") then failwith error
    | actual -> failwith $"Ordinary result changed: {actual}"
    printfn "PASS ordinary success=%b" success

let failureItem = setup false
agentRunner <- fun _ _ _ _ -> async { return failwith "agent transport failure" }
match runWithLive [failureItem] false "fixture" with
| Retry error when error.Contains "agent transport failure" -> ()
| actual -> failwith $"Ordinary agent exception changed: {actual}"
printfn "PASS ordinary agent exception remains retryable"

let completedItem = setup true
agentRunner <- fun _ title _ _ -> async {
    return (if title.StartsWith "Implement-" then "SUBTASK_COMPLETE" else "VERIFY_PASSED"), "fixture"
}
equal (Complete ()) (runWithLive [completedItem] false "fixture")
let completedHistory = state.Backlog
state <- emptyState
agentRunner <- fun _ _ _ _ -> failwith "Loading completed history must not invoke an agent"
equal None (resumeCheckpoint "next CI iteration" false)
equal completedHistory state.Backlog
printfn "PASS completed dispatch permits ordinary continuation without losing history"

let item = setup true
let mutable afterBlock = false
agentRunner <- fun _ title _ _ -> async {
    if afterBlock then failwith $"Unexpected effect after block: {title}"
    let output =
        if title = "Implement-2" then "SUBTASK_COMPLETE"
        elif title.StartsWith "FinalVerify-" then "VERIFY_FAILED"
        elif title.StartsWith "Implement-" then
            afterBlock <- true
            blockedOutput
        else "VERIFY_PASSED"
    return output, "fixture"
}
match runWithLive [item] false "fixture" with
| Block _ -> equal "Blocked" state.CurrentPhase
| actual -> failwith $"Final fixup swallowed blocked result: {actual}"
printfn "PASS final verification fixup propagates blocked without synthetic success"

for corruption in ["missing-block"; "corrupt-block"; "stale-source"; "stale-sprint"; "stale-snapshot"; "unrelated-issue"] do
    let item = setup false
    agentRunner <- fun _ _ _ _ -> async { return blockedOutput, "fixture" }
    runAllBacklogItems [item] false |> Async.RunSynchronously |> ignore
    let path = Path.Combine(Config.ralphDir, "blocked.json")
    match corruption with
    | "missing-block" -> File.Delete path
    | "corrupt-block" -> File.WriteAllText(path, "{")
    | "stale-source" -> File.AppendAllText(dirty, "new work retained\n")
    | "stale-sprint" -> File.AppendAllText(item.FilePath, "new sprint requirements\n")
    | "stale-snapshot" -> File.AppendAllText(Path.Combine(Config.ralphDir, "scheduler-state.json"), " ")
    | "unrelated-issue" ->
        let block = BlockedProtocol.readBlock Config.ralphDir |> Option.get
        File.WriteAllText(path, JsonSerializer.Serialize { block with Issue = 911 })
    | _ -> failwith corruption
    state <- emptyState
    agentRunner <- fun _ _ _ _ -> failwith "Fault must not launch any agent"
    equal 43 (run "fixture" false true 0 None)
    printfn "PASS fail-closed %s" corruption

if OperatingSystem.IsWindows() then
    let item = setup false
    let path = Path.Combine(Config.ralphDir, "scheduler-state.json")
    File.WriteAllText(path, "retained checkpoint")
    use locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
    agentRunner <- fun _ _ _ _ -> failwith "Denied persistence must not launch an agent"
    match runWithLive [item] false "fixture" with
    | Fault _ -> ()
    | actual -> failwith $"Denied checkpoint was not a terminal fault: {actual}"
    printfn "PASS denied persistence launches no agent and retains checkpoint"

do
    let item = setup true
    let abandoned = { item with Order = 0; Name = "Abandoned"; FilePath = Path.Combine(Config.sprintsDir, "00_Abandoned.md") }
    File.WriteAllText(abandoned.FilePath, "Historical plan, not active")
    let attempts = [for iteration in 1 .. Config.ArbiterThreshold - 1 ->
                        { Iteration = iteration; AgentOutput = "earlier retry"; VerifierResults = [] }]
    let history =
        (abandoned, Running(Implement, 1), emptyTiming) ::
        (state.Backlog |> List.map (fun (sprint, status, timing) ->
            sprint, status, if sprint = item then { timing with IterationHistory = attempts } else timing))
    state <- { state with Backlog = history }
    let captureDir = Path.Combine(Path.GetFullPath(Path.Combine(Config.ralphDir, "..", "..", "..")), "data", "executors", "910")
    Directory.CreateDirectory captureDir |> ignore
    let oldLaunch, newLaunch = Path.Combine(captureDir, "old.launch.json"), Path.Combine(captureDir, "new.launch.json")
    let startOwned () =
        let psi = ProcessStartInfo("pwsh")
        for arg in ["-NoProfile"; "-NonInteractive"; "-Command"; "Start-Sleep -Seconds 120"] do psi.ArgumentList.Add arg
        Process.Start psi
    let identity (proc: Process) = {| Pid = proc.Id; StartedUtc = proc.StartTime.ToUniversalTime() |}
    use executor = Process.GetCurrentProcess()
    use child = startOwned ()
    use oldExecutor = startOwned ()
    use oldChild = startOwned ()
    let previousIssue, previousLaunch = Environment.GetEnvironmentVariable("RALPH_ISSUE_NUMBER"), Environment.GetEnvironmentVariable("RALPH_LAUNCH_FILE")
    try
        let oldRecord = {| Issue = 910; Worktree = Config.workDir; Executor = identity oldExecutor; Child = identity oldChild |}
        File.WriteAllText(oldLaunch, JsonSerializer.Serialize oldRecord)
        for proc in [oldExecutor; oldChild] do
            proc.Kill true
            if not (proc.WaitForExit 10000) then failwith "Owned previous fixture did not exit"
        Environment.SetEnvironmentVariable("RALPH_ISSUE_NUMBER", "910")
        Environment.SetEnvironmentVariable("RALPH_LAUNCH_FILE", oldLaunch)
        agentRunner <- fun _ _ _ _ -> async { return blockedOutput, "fixture" }
        let block =
            match runWithLive [item] false "fixture" with
            | Block block -> block
            | actual -> failwith $"Expected last-iteration block: {actual}"
        let snapshot = File.ReadAllText(Path.Combine(Config.ralphDir, "scheduler-state.json"))
        let checkpoint = BlockedProtocol.checkpoint Config.workDir Config.ralphDir
        let launch = {| Issue = 910; Worktree = Config.workDir; Executor = identity executor; Child = identity child |}
        File.WriteAllText(newLaunch, JsonSerializer.Serialize launch)
        let claim = {|
            Version = 1; BlockId = block.BlockId; Checkpoint = block.Checkpoint
            PreviousLaunchFile = oldLaunch; NewLaunchFile = newLaunch
            NewExecutorPid = executor.Id; NewExecutorStartedUtc = executor.StartTime.ToUniversalTime()
        |}
        File.WriteAllText(Path.Combine(Config.ralphDir, "resume-launch.json"), JsonSerializer.Serialize claim)
        Environment.SetEnvironmentVariable("RALPH_LAUNCH_FILE", newLaunch)
        state <- emptyState
        let mutable implementers = []
        agentRunner <- fun _ title _ _ -> async {
            if title.StartsWith "Implement-" then implementers <- implementers @ [title]
            return (if title.StartsWith "Implement-" then "SUBTASK_COMPLETE" else "VERIFY_PASSED"), "fixture"
        }
        equal 0 (run "resume" false true 0 None)
        equal ["Implement-2"] implementers
        equal checkpoint (BlockedProtocol.checkpoint Config.workDir Config.ralphDir)
        equal snapshot (File.ReadAllText(Path.Combine(Config.ralphDir, $"scheduler-state-{block.BlockId}.json")))
        let _, _, resumedTiming = state.Backlog |> List.find (fun (sprint, _, _) -> sprint.FilePath = item.FilePath)
        equal (Config.ArbiterThreshold + 1) resumedTiming.IterationHistory.Length
        equal Config.ArbiterThreshold (List.last resumedTiming.IterationHistory).Iteration
        equal true (state.Backlog |> List.exists (fun (sprint, status, _) -> sprint = abandoned && status = Running(Implement, 1)))
        equal true (state.Backlog |> List.exists (fun (_, status, timing) -> status = Done 1 && timing.Summary = Some "Original completed history"))
        File.Copy(Path.Combine(Config.ralphDir, $"resume-used-{block.BlockId}.json"), Path.Combine(Config.ralphDir, "resume-launch.json"))
        state <- emptyState
        agentRunner <- fun _ _ _ _ -> failwith "Replayed resume must not dispatch"
        equal 43 (run "duplicate resume" false true 0 None)
        printfn "PASS authenticated scheduler resume once at final retry; active plan/history/source preserved; replay refused"
    finally
        Environment.SetEnvironmentVariable("RALPH_ISSUE_NUMBER", previousIssue)
        Environment.SetEnvironmentVariable("RALPH_LAUNCH_FILE", previousLaunch)
        for proc in [oldExecutor; oldChild; child] do
            if not proc.HasExited then
                proc.Kill true
                proc.WaitForExit 10000 |> ignore

printfn "Scheduler fixtures passed."
