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
open System.Xml.Linq
open Utils
open TypeDefinitions
open Ralph

let equal expected actual =
    if actual <> expected then failwith $"Expected {expected}, actual {actual}"

let mutable regressionFailures = []
let regression name test =
    try
        test ()
        printfn "PASS regression: %s" name
    with ex ->
        regressionFailures <- name :: regressionFailures
        eprintfn "FAIL regression: %s: %s" name ex.Message

let rejected action =
    let mutable failed = false
    try action () with _ -> failed <- true
    equal true failed

let realAgentRunner = agentRunner
let blockedOutput = """SUBTASK_BLOCKED {"reason":"Missing [owner] launch","retryCondition":"owner-launch-enrolled"}"""
let options = JsonSerializerOptions()
options.Converters.Add(JsonFSharpConverter())
dashboardDisabled <- true
pushChanges <- fun () -> failwith "Blocked dispatch attempted publication"

let fixtureArgs = fsi.CommandLineArgs |> Array.toList
match fixtureArgs |> List.tryFindIndex ((=) "--request-sink") with
| Some index ->
    let request = requestFromArgs (fixtureArgs |> List.skip (index + 1))
    File.WriteAllText(Environment.GetEnvironmentVariable "RALPH_FIXTURE_SINK", request)
    exit 0
| None -> ()

let xmlWithTerminalControl = XmlHelpers.xt "log" "before\u001b[31mafter"
let serializedXml = xmlWithTerminalControl.ToString()
equal false (serializedXml.Contains('\u001b'))
equal true (serializedXml.Contains("before"))
equal true (serializedXml.Contains("after"))
printfn "PASS terminal control characters are sanitized before XML serialization"

Directory.CreateDirectory Config.ralphDir |> ignore
let restartStatePath = Path.Combine(Config.ralphDir, "scheduler-state.json")
if File.Exists restartStatePath then File.Delete restartStatePath
equal false (shouldResumeBeforeRestart ())
File.WriteAllText(restartStatePath, "{}")
equal true (shouldResumeBeforeRestart ())
File.Delete restartStatePath
printfn "PASS explicit restart bypasses missing scheduler state"

let requestPath = Path.Combine(Config.ralphDir, "request.txt")
File.WriteAllText(requestPath, "large request from file")
equal "large request from file" (requestFromArgs ["--request-file"; requestPath; "--yes"])
equal "inline request" (requestFromArgs ["inline"; "request"; "--yes"])
File.Delete requestPath
printfn "PASS requests can be delivered without command-line length limits"

regression "exact long request reader and parse errors" (fun () ->
    let request = String.replicate 1500 "{ \"quoted\": \"žluťoučký 🦔 日本語\" }\r\n" + "\n"
    equal true (request.Length >= 50000)
    File.WriteAllText(requestPath, request)
    equal request (requestFromArgs ["--request-file"; requestPath; "--yes"])
    equal "inline {quoted} 日本語" (requestFromArgs ["inline"; "{quoted}"; "日本語"; "--yes"])
    for args in [
        ["--request-file"]
        ["--request-file"; "relative.txt"]
        ["--request-file"; requestPath + ".missing"]
        ["--request-file"; requestPath; "--request-file"; requestPath]
        ["--request-file"; requestPath; "silently discarded inline"]
    ] do rejected (fun () -> requestFromArgs args |> ignore)
    File.Delete requestPath)

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

match fixtureArgs |> List.tryFindIndex ((=) "--exit-case") with
| Some index ->
    let mode = fixtureArgs[index + 1]
    let item = setup true
    agentRunner <- fun _ title _ _ -> async {
        match mode with
        | "blocked" -> return blockedOutput, "fixture"
        | "failure" -> return failwith "ordinary fixture failure with retained journal"
        | "complete" -> return (if title.StartsWith "Implement-" then "SUBTASK_COMPLETE" else "VERIFY_PASSED"), "fixture"
        | _ -> return failwith "Protocol fault must not dispatch an agent"
    }
    let code =
        if mode = "fault" then
            File.WriteAllText(Path.Combine(Config.ralphDir, "scheduler-state.json"), "{")
            state <- emptyState
            run "corrupt journal" false true 0 None
        else runWithLive [item] false "exit fixture" |> dispatchExit
    equal (mode = "complete") (File.Exists(Path.Combine(Config.ralphDir, "completed-state.json")))
    equal (mode <> "complete") (File.Exists(Path.Combine(Config.ralphDir, "scheduler-state.json")))
    equal (mode = "blocked") (File.Exists(Path.Combine(Config.ralphDir, "blocked.json")))
    printfn "EXIT-FIXTURE %s retained journal=%b, blocked=%b; exiting %d" mode (mode <> "complete") (mode = "blocked") code
    exit code
| None -> ()

let rawFailure =
    "useful <failure attr=\"'&\"> 日本語 🦔 \u001b[31m\u0000\u0001\u000b\uFFFE\uFFFF high:"
    + string (char 0xD800) + " low:" + string (char 0xDC00) + " end"
let cleanFailure = "useful <failure attr=\"'&\"> 日本語 🦔 �[31m����� high:� low:� end"

regression "durable raw UTF-16 history roundtrip" (fun () ->
    let item = setup false
    let record = { Iteration = 1; AgentOutput = rawFailure; VerifierResults = ["FUNCTIONAL", false, rawFailure] }
    state <- { state with CurrentPhase = "Complete"; Backlog = [item, Done 1, { emptyTiming with IterationHistory = [record] }] }
    SchedulerState.save state
    SchedulerState.archiveCompleted ()
    state <- emptyState
    equal None (resumeCheckpoint "load raw evidence" false)
    equal [record] (getItemTiming item.FilePath |> Option.get).IterationHistory)

for kind in ["corrective"; "arbiter"] do
    regression (kind + " actual prompt XML characters") (fun () ->
        let item = { setup false with Name = rawFailure; Description = rawFailure; FilePath = rawFailure; DoD = [rawFailure] }
        let history: Prompting.XmlPrompt.IterationHistory list =
            [{ Iteration = 1; AgentOutput = rawFailure; VerifierResults = ["FUNCTIONAL", false, rawFailure] }]
        let prompt =
            if kind = "corrective" then
                Prompting.Prompts.implement item 2 [rawFailure] [] [] [] history
            else
                let context: Prompting.XmlPrompt.FailedSprintContext = {
                    SprintOrder = 2; SprintName = rawFailure; SprintFilePath = rawFailure
                    IterationsSpent = 1; LastIterations = history; VerifierFailureCounts = Map.ofList ["FUNCTIONAL", 1]
                }
                Prompting.Prompts.arbiter rawFailure (Some context) [] [2, rawFailure, rawFailure]
        let parsed = XElement.Parse prompt
        equal true (parsed.Value.Contains cleanFailure)
        if kind = "arbiter" then
            equal true (parsed.Descendants() |> Seq.collect _.Attributes() |> Seq.exists (fun a -> a.Value = cleanFailure))
        equal rawFailure history.Head.AgentOutput
        equal ["FUNCTIONAL", false, rawFailure] history.Head.VerifierResults)

regression "verifier failure reaches corrective and arbiter prompts with raw history" (fun () ->
    let item = { setup false with TargetVerifiers = Some ["FUNCTIONAL"] }
    state <- { state with Backlog = [item, Todo, emptyTiming] }
    let mutable implementers, corrections, arbiters = 0, 0, 0
    agentRunner <- fun prompt title _ _ -> async {
        let output =
            if title.StartsWith "Implement-" then
                implementers <- implementers + 1
                if implementers > 1 then
                    equal true ((XElement.Parse prompt).Value.Contains cleanFailure)
                    corrections <- corrections + 1
                "SUBTASK_COMPLETE"
            elif title = "Arbiter" then
                equal true ((XElement.Parse prompt).Value.Contains cleanFailure)
                arbiters <- arbiters + 1
                "ARBITER_COMPLETE"
            elif title.StartsWith "Cutter-" then "<EliminatedVerifiers></EliminatedVerifiers>"
            else "VERIFY_FAILED\n<ManagementSummary>" + rawFailure + "</ManagementSummary>"
        return output, "fixture"
    }
    match runWithLive [item] false "fixture" with
    | Retry error -> equal true (error.Contains "ARBITER_NEEDED")
    | actual -> failwith $"Expected bounded verifier retry, got {actual}"
    equal Config.ArbiterThreshold implementers
    equal (Config.ArbiterThreshold - 1) corrections
    let history = (getItemTiming item.FilePath |> Option.get).IterationHistory
    equal true (history |> List.forall (fun h -> h.VerifierResults |> List.exists (fun (_, passed, text) -> not passed && text.Contains rawFailure)))
    invokeArbiter rawFailure false |> ignore
    equal 1 arbiters
    equal history (getItemTiming item.FilePath |> Option.get).IterationHistory)

regression "clarification block persists once without implementer retry or downstream" (fun () ->
    let item = setup true
    let checkpoint = BlockedProtocol.checkpoint Config.workDir Config.ralphDir
    let mutable calls = []
    agentRunner <- fun _ title _ resume -> async {
        calls <- calls @ [title]
        if calls.Length = 1 then
            equal None resume
            return "Need to clarify owner availability", "fixture-session"
        else
            equal (Some "fixture-session") resume
            return blockedOutput, "fixture-session"
    }
    let block =
        match runWithLive [item] false "fixture" with
        | Block block -> block
        | actual -> failwith $"Clarification lost blocked result: {actual}"
    equal ["Implement-2"; "Disambiguate-Subtask-Blocked-followup"] calls
    equal checkpoint (BlockedProtocol.checkpoint Config.workDir Config.ralphDir)
    equal (Some block) (BlockedProtocol.readBlock Config.ralphDir)
    let saved = JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(Config.ralphDir, "scheduler-state.json")), options)
    equal state saved
    equal 1 (saved.Backlog |> List.sumBy (fun (_, status, _) -> match status with Blocked _ -> 1 | _ -> 0))
    let timing = getItemTiming item.FilePath |> Option.get
    equal true (timing.IterationHistory.Head.AgentOutput.Contains blockedOutput)
    equal true (saved.Backlog |> List.exists (fun (_, status, timing) -> status = Done 1 && timing.Summary = Some "Original completed history")))

for response in ["SUBTASK_INCOMPLETE"; blockedOutput; "timeout"] do
    regression $"owned process cleanup {response}" (fun () ->
        let item = setup false
        let ready = Path.Combine(Config.ralphDir, "agent-child.json")
        let psi = ProcessStartInfo("pwsh")
        for arg in ["-NoProfile"; "-NonInteractive"; "-Command"; "Start-Sleep -Seconds 120"] do psi.ArgumentList.Add arg
        use sentinel = Process.Start psi
        use launcher = Process.GetCurrentProcess()
        let sentinelStart, launcherStart = sentinel.StartTime, launcher.StartTime
        let originalStart, originalClock = startAgentProcess, agentUtcNow
        let mutable owned: Process option = None
        try
            startAgentProcess <- fun info ->
                info.FileName <- "pwsh"
                info.ArgumentList.Clear()
                let childScript = """
$null = [Console]::In.ReadToEnd()
$info = [Diagnostics.ProcessStartInfo]::new('pwsh')
$info.UseShellExecute = $false
foreach ($arg in @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 120')) { $info.ArgumentList.Add($arg) }
$child = [Diagnostics.Process]::Start($info)
[IO.File]::WriteAllText($env:FIXTURE_READY, (@{ Pid=$child.Id; StartedUtc=$child.StartTime.ToUniversalTime() } | ConvertTo-Json))
if ($env:FIXTURE_RESPONSE -ne 'timeout') {
    [Console]::WriteLine($env:FIXTURE_RESPONSE)
    [Console]::WriteLine('RALPH_AGENT_COMPLETE')
    [Console]::Out.Flush()
}
Start-Sleep -Seconds 120
"""
                for arg in ["-NoProfile"; "-NonInteractive"; "-Command"; childScript] do info.ArgumentList.Add arg
                info.Environment["FIXTURE_READY"] <- ready
                info.Environment["FIXTURE_RESPONSE"] <- response
                let proc = Process.Start info
                let observed = Process.GetProcessById proc.Id
                observed.Handle |> ignore
                owned <- Some observed
                proc
            if response = "timeout" then
                let epoch = DateTime.UtcNow
                let mutable first = true
                agentUtcNow <- fun () ->
                    if first then first <- false; epoch
                    elif File.Exists ready then epoch.AddMinutes(float Config.AgentTimeoutMinutes + 1.)
                    else epoch
            agentRunner <- realAgentRunner
            let result = runBacklogItem item Config.ArbiterThreshold 0 [] false |> Async.Catch |> Async.RunSynchronously
            match response, result with
            | "SUBTASK_INCOMPLETE", Choice1Of2 (Retry error) -> equal "ARBITER_NEEDED" error
            | "timeout", Choice2Of2 error -> equal true (error.Message.Contains "timeout")
            | _, Choice1Of2 (Block _) when response = blockedOutput -> ()
            | _ -> failwith $"Completion marker changed scheduler outcome: {result}"
            let proc = owned |> Option.get
            equal true (proc.WaitForExit 10000)
            use childRecord = JsonDocument.Parse(File.ReadAllText ready)
            let childPid = childRecord.RootElement.GetProperty("Pid").GetInt32()
            try
                use child = Process.GetProcessById childPid
                equal true (child.WaitForExit 10000)
            with :? ArgumentException -> ()
            equal false sentinel.HasExited
            equal sentinelStart sentinel.StartTime
            equal launcherStart launcher.StartTime
            printfn "CLEANUP agent=%d exit=%d descendant=%d exited; sentinel=%d alive; launcher=%d unchanged" proc.Id proc.ExitCode childPid sentinel.Id launcher.Id
        finally
            startAgentProcess <- originalStart
            agentUtcNow <- originalClock
            for proc in (sentinel :: Option.toList owned) do
                if not proc.HasExited then
                    proc.Kill true
                    proc.WaitForExit 10000 |> ignore
            owned |> Option.iter _.Dispose())

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

regression "fresh retries stop at the arbiter limit" (fun () ->
    setup true |> ignore
    let mutable architects, arbiters, implementers = 0, 0, 0
    agentRunner <- fun _ title _ _ -> async {
        if title = "Architect" then
            architects <- architects + 1
            File.WriteAllText(Path.Combine(Config.sprintsDir, "02_Retry.md"), "# Retry\n\n## Definition of Done\n- Pass\n")
        elif title = "Arbiter" then arbiters <- arbiters + 1
        elif title.StartsWith "Implement-" then implementers <- implementers + 1
        return (if title.StartsWith "Implement-" then "SUBTASK_INCOMPLETE" else "ARBITER_COMPLETE"), "fixture"
    }
    equal 1 (run "bounded retry" false true 0 None)
    equal Config.MaxArbiterAttempts architects
    equal Config.MaxArbiterAttempts arbiters
    equal (2 * Config.ArbiterThreshold * Config.MaxArbiterAttempts) implementers)

let arbiterFailureMessage = "injected ordinary arbiter transport failure"

type RecoveryFailure = NoFailure | ArbiterFailure | CheckpointFailure

let resumeFixture retry exhaust failureMode =
    let arbiterFailure = failureMode = ArbiterFailure
    let item = setup true
    let abandoned = { item with Order = 0; Name = "Abandoned"; FilePath = Path.Combine(Config.sprintsDir, "00_Abandoned.md") }
    File.WriteAllText(abandoned.FilePath, "Historical plan, not active")
    let attempts = [for iteration in 1 .. Config.ArbiterThreshold - 1 ->
                        { Iteration = iteration; AgentOutput = "earlier retry"; VerifierResults = [] }]
    let history =
        (abandoned, Running(Implement, 1), emptyTiming) ::
        (state.Backlog |> List.map (fun (sprint, status, timing) ->
            sprint, status, if sprint = item then { timing with IterationHistory = attempts } else timing))
    state <- { state with Backlog = history; ArbiterAttempt = if exhaust then Config.MaxArbiterAttempts - 1 else 0 }
    let captureDir = Path.Combine(Path.GetFullPath(Path.Combine(Config.ralphDir, "..", "..", "..")), "data", "executors", "910")
    Directory.CreateDirectory captureDir |> ignore
    let oldLaunch, newLaunch = Path.Combine(captureDir, "old.launch.json"), Path.Combine(captureDir, "new.launch.json")
    let startOwned () =
        let psi = ProcessStartInfo("pwsh")
        for arg in ["-NoProfile"; "-NonInteractive"; "-Command"; "Start-Sleep -Seconds 120"] do psi.ArgumentList.Add arg
        Process.Start psi
    let identity (proc: Process) = {| Pid = proc.Id; StartedUtc = proc.StartTime.ToUniversalTime() |}
    use fixtureLaunch = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable "RALPH_FIXTURE_LAUNCH"))
    use executor = Process.GetProcessById(fixtureLaunch.RootElement.GetProperty("Executor").GetProperty("Pid").GetInt32())
    use child = Process.GetProcessById(fixtureLaunch.RootElement.GetProperty("Child").GetProperty("Pid").GetInt32())
    use oldExecutor = startOwned ()
    use oldChild = startOwned ()
    let mutable lockedCheckpoint: (FileStream * string) option = None
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
        let mutable arbiters = 0
        agentRunner <- fun _ title _ _ -> async {
            if title.StartsWith "Implement-" then implementers <- implementers @ [title]
            if title.StartsWith "FinalVerify-" && failureMode = CheckpointFailure && lockedCheckpoint.IsNone then
                let path = Path.Combine(Config.ralphDir, "scheduler-state.json")
                let retained = File.ReadAllText path
                lockedCheckpoint <- Some (new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None), retained)
            let output =
                if title = "Arbiter" then
                    arbiters <- arbiters + 1
                    if arbiterFailure then failwith arbiterFailureMessage
                    let recovery = Path.Combine(Config.sprintsDir, "03_Recovery.md")
                    File.Delete item.FilePath
                    File.WriteAllText(recovery, "# Recovery\n\n## Definition of Done\n- Recovered\n")
                    "ARBITER_COMPLETE"
                elif title.StartsWith "Implement-" && (exhaust || (retry && title = "Implement-2")) then "SUBTASK_INCOMPLETE"
                elif title.StartsWith "Implement-" then "SUBTASK_COMPLETE"
                elif title.StartsWith "Cutter-" then "<EliminatedVerifiers></EliminatedVerifiers>"
                else "VERIFY_PASSED"
            return output, "fixture"
        }
        let code, failure =
            try run "resume" false true 0 None, None
            with
            | ex when arbiterFailure && ex.Message = arbiterFailureMessage -> 1, Some ex
            | SchedulerState.CheckpointFault _ as ex when failureMode = CheckpointFailure -> 1, Some ex
        if failureMode = CheckpointFailure then
            let _, retained = lockedCheckpoint |> Option.get
            equal "Complete" state.CurrentPhase
            equal retained (File.ReadAllText(Path.Combine(Config.ralphDir, "scheduler-state.json")))
            equal false (File.Exists(Path.Combine(Config.ralphDir, "completed-state.json")))
            printfn "PASS checkpoint finalization fault: prior journal retained after successful final verification"
        else
            if not arbiterFailure then equal (if exhaust then 1 else 0) code
        equal (if arbiterFailure then ["Implement-2"]
               elif exhaust then "Implement-2" :: List.replicate Config.ArbiterThreshold "Implement-3"
               elif retry then ["Implement-2"; "Implement-3"] else ["Implement-2"]) implementers
        equal (if retry then 1 else 0) arbiters
        if not retry || arbiterFailure then equal checkpoint (BlockedProtocol.checkpoint Config.workDir Config.ralphDir)
        equal snapshot (File.ReadAllText(Path.Combine(Config.ralphDir, $"scheduler-state-{block.BlockId}.json")))
        let _, _, resumedTiming = state.Backlog |> List.find (fun (sprint, _, _) -> sprint.FilePath = item.FilePath)
        equal (Config.ArbiterThreshold + 1) resumedTiming.IterationHistory.Length
        equal Config.ArbiterThreshold (List.last resumedTiming.IterationHistory).Iteration
        equal true (state.Backlog |> List.exists (fun (sprint, status, _) -> sprint = abandoned && status = Running(Implement, 1)))
        equal true (state.Backlog |> List.exists (fun (_, status, timing) -> status = Done 1 && timing.Summary = Some "Original completed history"))
        let usedClaim = Path.Combine(Config.ralphDir, $"resume-used-{block.BlockId}.json")
        equal false (File.Exists(Path.Combine(Config.ralphDir, "resume-launch.json")))
        equal false (File.Exists(Path.Combine(Config.ralphDir, "blocked.json")))
        let retainedPath =
            let active = Path.Combine(Config.ralphDir, "scheduler-state.json")
            if File.Exists active then active else Path.Combine(Config.ralphDir, "completed-state.json")
        let retainedSnapshot = File.ReadAllText retainedPath
        let retainedClaim = File.ReadAllText usedClaim
        File.Copy(usedClaim, Path.Combine(Config.ralphDir, "resume-launch.json"))
        state <- emptyState
        agentRunner <- fun _ _ _ _ -> failwith "Replayed resume must not dispatch"
        let replayCode = run "duplicate resume" false true 0 None
        equal 43 replayCode
        equal retainedClaim (File.ReadAllText usedClaim)
        equal retainedSnapshot (File.ReadAllText retainedPath)
        printfn "PASS authenticated scheduler resume retry=%b once at final retry; active plan/history/source preserved; replay refused" retry
        code, failure, replayCode
    finally
        lockedCheckpoint |> Option.iter (fun (stream, _) -> stream.Dispose())
        Environment.SetEnvironmentVariable("RALPH_ISSUE_NUMBER", previousIssue)
        Environment.SetEnvironmentVariable("RALPH_LAUNCH_FILE", previousLaunch)
        for proc in [oldExecutor; oldChild] do
            if not proc.HasExited then
                proc.Kill true
                proc.WaitForExit 10000 |> ignore

for retry, exhaust in [false, false; true, false; true, true] do
    regression $"authenticated resume retry={retry} exhaust={exhaust}" (fun () -> resumeFixture retry exhaust NoFailure |> ignore)

if not regressionFailures.IsEmpty then failwith ("Regression failures: " + String.concat ", " regressionFailures)
printfn "Scheduler fixtures passed."

match fixtureArgs |> List.tryFindIndex ((=) "--recovery-exit-case") with
| Some index ->
    match fixtureArgs[index + 1] with
    | "fresh" ->
        setup false |> ignore
        let mutable implementers, arbiters = 0, 0
        agentRunner <- fun _ title _ _ -> async {
            if title = "Architect" then
                File.WriteAllText(Path.Combine(Config.sprintsDir, "02_Retry.md"), "# Retry\n\n## Definition of Done\n- Pass\n")
            elif title = "Arbiter" then
                arbiters <- arbiters + 1
                failwith arbiterFailureMessage
            elif title.StartsWith "Implement-" then implementers <- implementers + 1
            return (if title.StartsWith "Implement-" then "SUBTASK_INCOMPLETE" else "ARBITER_COMPLETE"), "fixture"
        }
        let code =
            try run "fresh arbiter failure" false true 0 None
            finally
                equal Config.ArbiterThreshold implementers
                equal 1 arbiters
                equal true (File.Exists(Path.Combine(Config.ralphDir, "scheduler-state.json")))
                equal false (File.Exists(Path.Combine(Config.ralphDir, "blocked.json")))
                printfn "PASS recovery exit fixture fresh: journal retained, one arbiter"
        exit code
    | "resumed" | "replay" | "checkpoint-fault" as mode ->
        let failureMode =
            match mode with
            | "resumed" -> ArbiterFailure
            | "checkpoint-fault" -> CheckpointFailure
            | _ -> NoFailure
        let code, failure, replayCode = resumeFixture (mode <> "checkpoint-fault") false failureMode
        printfn "PASS recovery exit fixture %s: checkpoint/history retained, one claim consumed, replay refused" mode
        match failure with
        | Some error -> raise error
        | None -> exit (if mode = "replay" then replayCode else code)
    | mode -> failwith $"Unknown recovery exit fixture: {mode}"
| None -> ()
