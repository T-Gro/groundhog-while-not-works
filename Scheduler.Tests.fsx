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
let assistantMessage content =
    JsonSerializer.Serialize {| ``type`` = "assistant.message"; data = {| content = content; phase = "final_answer"; toolRequests = Array.empty<obj> |} |}
let agentResult code = JsonSerializer.Serialize {| ``type`` = "result"; exitCode = code; usage = {| totalTokens = 0 |} |}
let runTransportFixture resume lines exitCode =
    let originalStart = startAgentProcess
    let path = Path.Combine(Config.ralphDir, $"transport-{Guid.NewGuid():N}.jsonl")
    File.WriteAllText(path, String.concat "\n" lines + "\n")
    try
        startAgentProcess <- fun info ->
            info.FileName <- "pwsh"
            info.ArgumentList.Clear()
            for argument in [
                "-NoProfile"; "-NonInteractive"; "-Command"
                "$null=[Console]::In.ReadToEnd(); [Console]::Write([IO.File]::ReadAllText($env:FIXTURE_STDOUT_FILE)); exit ([int]$env:FIXTURE_EXIT)"
            ] do info.ArgumentList.Add argument
            info.Environment["FIXTURE_STDOUT_FILE"] <- path
            info.Environment["FIXTURE_EXIT"] <- string exitCode
            Process.Start info
        realAgentRunner "Structured transport fixture." "Transport" false resume
        |> Async.RunSynchronously |> fst
    finally
        startAgentProcess <- originalStart
        File.Delete path
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

regression "explicit restart restores ordinary scheduler state" (fun () ->
    let item = setup false
    state <- { state with CurrentPhase = "Executing"; ActiveSprints = [item.FilePath] }
    SchedulerState.save state
    match SchedulerState.restoreExplicitRestart () with
    | SchedulerState.Resumed saved -> equal state saved
    | restored -> failwith $"Expected resumed state, got {restored}")

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
    equal (Some 43) (resumeCheckpoint "load raw evidence" false false)
    match SchedulerState.restore false with
    | SchedulerState.Finished saved -> state <- saved
    | restored -> failwith $"Expected completed raw evidence, got {restored}"
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

regression "native Git and approvals are bound to the target repository" (fun () ->
    let item = setup false
    let gitAt directory arguments =
        let info = ProcessStartInfo("git")
        info.UseShellExecute <- false
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        for arg in ["-C"; directory] @ arguments do info.ArgumentList.Add arg
        use proc = Process.Start info
        let output, error = proc.StandardOutput.ReadToEndAsync(), proc.StandardError.ReadToEndAsync()
        equal true (proc.WaitForExit 10000)
        if proc.ExitCode <> 0 then failwith error.Result
        output.Result.Trim()
    gitAt Config.workDir ["add"; "--all"] |> ignore
    gitAt Config.workDir ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid";
                          "commit"; "--quiet"; "--allow-empty"; "-m"; "Clean target provenance fixture"] |> ignore
    let target = gitAt Config.workDir ["rev-parse"; "HEAD"]
    let launcher = gitAt Config.scriptDir ["rev-parse"; "HEAD"]
    equal false (target = launcher)
    let originalDirectory = Directory.GetCurrentDirectory()
    let originalAgent = agentRunner
    try
        Directory.SetCurrentDirectory Config.scriptDir
        equal (Some target) (Git.getHeadCommit())
        equal 40 target.Length
        let legacy = Map.ofList ["FUNCTIONAL", "750e4a8"; "CODE-QUALITY", launcher]
        updateTiming item.FilePath (fun timing -> { timing with ApprovedCommits = legacy })
        let mutable observed = ""
        agentRunner <- fun prompt _ _ _ -> async {
            observed <- prompt
            return "VERIFY_PASSED", "fixture"
        }
        equal (Ok(Some target)) (verifyStage false item.FilePath "FUNCTIONAL" item |> Async.RunSynchronously)
        equal true (observed.Contains target)
        equal false (observed.Contains "750e4a8")
        equal false (observed.Contains launcher)
        equal legacy ((getItemTiming item.FilePath |> Option.get).ApprovedCommits)
        updateVerifierStatus item.FilePath "PERF" true None
        equal legacy ((getItemTiming item.FilePath |> Option.get).ApprovedCommits)
        updateVerifierStatus item.FilePath "FUNCTIONAL" true (Some target)
        equal target ((getItemTiming item.FilePath |> Option.get).ApprovedCommits["FUNCTIONAL"])
        equal launcher ((getItemTiming item.FilePath |> Option.get).ApprovedCommits["CODE-QUALITY"])
        agentRunner <- fun _ _ _ _ -> async {
            gitAt Config.workDir ["-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.invalid";
                                  "commit"; "--quiet"; "--allow-empty"; "-m"; "Move target during verification"] |> ignore
            return "VERIFY_PASSED", "fixture"
        }
        match verifyStage false item.FilePath "FUNCTIONAL" item |> Async.RunSynchronously with
        | Error reason -> equal true (reason.Contains "HEAD changed")
        | result -> failwith $"Moving target was incorrectly approved: {result}"
        let movedTarget = gitAt Config.workDir ["rev-parse"; "HEAD"]
        equal false (Git.isApprovalAncestor target movedTarget)
        equal true (Git.isApprovalAncestor movedTarget target)
        equal target ((getItemTiming item.FilePath |> Option.get).ApprovedCommits["FUNCTIONAL"])
    finally
        Directory.SetCurrentDirectory originalDirectory
        agentRunner <- originalAgent)

regression "uncommitted source cannot reuse or create a commit approval" (fun () ->
    let item = setup false
    let target = Git.getHeadCommit() |> Option.get
    let legacy = Map.ofList ["FUNCTIONAL", target]
    updateTiming item.FilePath (fun timing -> { timing with ApprovedCommits = legacy })
    let dirty = Path.Combine(Config.workDir, "profile-dirty-source.txt")
    let originalAgent = agentRunner
    try
        File.WriteAllText(dirty, "uncommitted source\n")
        let mutable observed = ""
        agentRunner <- fun prompt _ _ _ -> async {
            observed <- prompt
            return "VERIFY_PASSED", "fixture"
        }
        equal (Ok None) (verifyStage false item.FilePath "FUNCTIONAL" item |> Async.RunSynchronously)
        equal false (observed.Contains "PRIOR VERIFIER APPROVALS")
        equal true (observed.Contains target)
        updateVerifierStatus item.FilePath "FUNCTIONAL" true None
        equal legacy ((getItemTiming item.FilePath |> Option.get).ApprovedCommits)
        rejected (fun () -> updateVerifierStatus item.FilePath "FUNCTIONAL" true (Some target))
    finally
        File.Delete dirty
        agentRunner <- originalAgent)

regression "fresh resume and expert verifier paths require the requested isolated profile" (fun () ->
    setup false |> ignore
    let originalStart = startAgentProcess
    let mutable invocations = 0
    try
        startAgentProcess <- fun info ->
            invocations <- invocations + 1
            let args = info.ArgumentList |> Seq.toArray
            let value name = args[Array.findIndex ((=) name) args + 1]
            equal "gpt-6.1-sol" (value "--model")
            equal "xhigh" (value "--effort")
            equal "long_context" (value "--context")
            equal "json" (value "--output-format")
            equal "off" (value "--stream")
            equal true (Array.contains "--disable-builtin-mcps" args)
            equal false (Array.contains "--disable-mcp-server" args)
            equal Config.workDir info.WorkingDirectory
            if invocations = 1 then equal true (Array.contains "--name" args)
            else equal true (Array.contains "--resume=profile-existing-session" args)
            info.FileName <- "pwsh"
            info.ArgumentList.Clear()
            for arg in ["-NoProfile"; "-NonInteractive"; "-Command";
                        "$null=[Console]::In.ReadToEnd(); [Console]::Write($env:FIXTURE_STDOUT)"] do
                info.ArgumentList.Add arg
            info.Environment["FIXTURE_STDOUT"] <-
                String.concat "\n" [assistantMessage "VERIFY_PASSED\nRALPH_AGENT_COMPLETE"; agentResult 0]
            Process.Start info
        for resume in [None; Some "profile-existing-session"] do
            let output, _ =
                realAgentRunner "Read-only profile transport fixture." "Profile" false resume
                |> Async.RunSynchronously
            equal "VERIFY_PASSED\nRALPH_AGENT_COMPLETE" output
        equal 2 invocations
        for name in ["03-CODE-QUALITY.md"; "99-HONEST-ASSESSMENT.md"] do
            let prompt = File.ReadAllText(Path.Combine(Config.verifiersDir, name))
            for required in ["model: \"gpt-6.1-sol\""; "reasoning_effort: \"xhigh\""; "context_tier: \"long_context\""] do
                equal true (prompt.Contains required)
            equal false (prompt.Contains "gpt-6-astra")
    finally
        startAgentProcess <- originalStart)

let original2020Block =
    """SUBTASK_BLOCKED {"reason":"Required Debug OpenTypeDeclarationTests fail 8/59 at AddTypeContentsToNameEnv's assert(isAppTy g ty), reproduced after a fresh rebuild and with local Core; Release passes 59/59. Correcting this non-extension assertion requires edits outside the permitted sprint scope. Owner authorization or a Debug-validation waiver is required. Full Release component validation also has 34 failures. Implementation and regenerated XLF files remain uncommitted; nothing pushed.","retryCondition":"owner-launch-enrolled"}"""

for resume in [None; Some "transport-existing-session"] do
    regression $"original #2020 root block survives structured transport, resume={resume.IsSome}" (fun () ->
        let output = runTransportFixture resume [assistantMessage original2020Block; agentResult 0] 0
        equal original2020Block output
        use block = JsonDocument.Parse(original2020Block.Substring("SUBTASK_BLOCKED ".Length))
        equal (Some(block.RootElement.GetProperty("reason").GetString())) (SchedulerState.blockRequest output))

regression "structured root content preserves long Unicode and trailing whitespace" (fun () ->
    let content = "  " + String.replicate 2500 "žluťoučký 🦔 日本語\r\n" + "SUBTASK_INCOMPLETE\r\n \n"
    equal content (runTransportFixture None [assistantMessage content; agentResult 0] 0))

let foreignMessages = [
    """{"type":"user.message","data":{"content":"SUBTASK_COMPLETE\nRALPH_AGENT_COMPLETE"}}"""
    """{"type":"tool.execution_complete","data":{"result":{"content":"SUBTASK_COMPLETE\nRALPH_AGENT_COMPLETE"}}}"""
    """{"type":"assistant.message","agentId":"child","data":{"content":"SUBTASK_COMPLETE\nRALPH_AGENT_COMPLETE","phase":"final_answer"}}"""
    """{"type":"assistant.message","data":{"parentToolCallId":"child-tool","content":"SUBTASK_COMPLETE\nRALPH_AGENT_COMPLETE","phase":"final_answer"}}"""
    """{"type":"assistant.message","data":{"parentToolCallId":null,"content":"SUBTASK_COMPLETE\nRALPH_AGENT_COMPLETE","phase":"final_answer"}}"""
]

regression "user tool and subagent markers cannot change the root verdict" (fun () ->
    let commentary = """{"type":"assistant.message","data":{"content":"SUBTASK_COMPLETE","phase":"commentary"}}"""
    let toolRequest = """{"type":"assistant.message","data":{"content":"SUBTASK_COMPLETE","toolRequests":[{"name":"fixture"}]}}"""
    let lines = foreignMessages @ [commentary; toolRequest; assistantMessage blockedOutput; agentResult 0]
    let output = runTransportFixture (Some "transport-existing-session") lines 0
    equal blockedOutput output
    equal (Some "Missing [owner] launch") (SchedulerState.blockRequest output))

for index, foreign in List.indexed foreignMessages do
    regression $"non-root output cannot substitute for a root answer #{index}" (fun () ->
        rejected (fun () -> runTransportFixture None [foreign; agentResult 0] 0 |> ignore))

let invalidTransports = [
    "empty", []
    "missing terminal result", [assistantMessage "SUBTASK_COMPLETE"]
    "missing root answer", [agentResult 0]
    "result is not final content", ["""{"type":"result","exitCode":0,"content":"SUBTASK_COMPLETE"}"""]
    "plain terminal text", ["SUBTASK_COMPLETE"; "RALPH_AGENT_COMPLETE"]
    "truncated event", [assistantMessage "SUBTASK_COMPLETE"; """{"type":"result","exitCode":"""]
    "malformed event before answer", ["not json"; assistantMessage "SUBTASK_COMPLETE"; agentResult 0]
    "missing exit code", [assistantMessage "SUBTASK_COMPLETE"; """{"type":"result","usage":{}}"""]
    "wrong exit code type", [assistantMessage "SUBTASK_COMPLETE"; """{"type":"result","exitCode":"0"}"""]
    "native failure envelope", [assistantMessage "SUBTASK_COMPLETE"; agentResult 7]
    "duplicate exit code", [assistantMessage "SUBTASK_COMPLETE"; """{"type":"result","exitCode":7,"exitCode":0}"""]
    "duplicate content", ["""{"type":"assistant.message","data":{"content":"SUBTASK_INCOMPLETE","content":"SUBTASK_COMPLETE","phase":"final_answer"}}"""; agentResult 0]
    "non-string content", ["""{"type":"assistant.message","data":{"content":{"text":"SUBTASK_COMPLETE"}}}"""; agentResult 0]
    "non-array tools", ["""{"type":"assistant.message","data":{"content":"SUBTASK_COMPLETE","toolRequests":{}}}"""; agentResult 0]
    "tools in a final answer", ["""{"type":"assistant.message","data":{"content":"SUBTASK_COMPLETE","phase":"final_answer","toolRequests":[{}]}}"""; agentResult 0]
    "unsupported final phase", ["""{"type":"assistant.message","data":{"content":"SUBTASK_COMPLETE","phase":"final"}}"""; agentResult 0]
    "unfinished later root tool call", [assistantMessage "SUBTASK_COMPLETE"; """{"type":"assistant.message","data":{"content":"","toolRequests":[{}]}}"""; agentResult 0]
    "unfinished later root turn", [assistantMessage "SUBTASK_COMPLETE"; """{"type":"assistant.turn_start","data":{"turnId":"later"}}"""; agentResult 0]
    "session failure", ["""{"type":"session.error","data":{"message":"fixture error"}}"""; assistantMessage "SUBTASK_COMPLETE"; agentResult 0]
    "event after terminal result", [assistantMessage "SUBTASK_COMPLETE"; agentResult 0; assistantMessage "SUBTASK_INCOMPLETE"]
    "duplicate result", [assistantMessage "SUBTASK_COMPLETE"; agentResult 0; agentResult 0]
    "child result", [assistantMessage "SUBTASK_COMPLETE"; """{"type":"result","agentId":"child","exitCode":0}"""]
]

for name, lines in invalidTransports do
    regression $"structured transport rejects {name}" (fun () ->
        rejected (fun () -> runTransportFixture None lines 0 |> ignore))

regression "a natural nonzero process exit cannot be hidden by successful JSON" (fun () ->
    rejected (fun () -> runTransportFixture None [assistantMessage "SUBTASK_COMPLETE"; agentResult 0] 17 |> ignore))

for name, messages in [
    "contradictory answer", [blockedOutput + "\nSUBTASK_COMPLETE"]
    "contradictory root messages", [blockedOutput; "SUBTASK_COMPLETE"]
    "truncated block JSON", ["""SUBTASK_BLOCKED {"reason":"unfinished"""]
] do
    regression $"structured transport preserves and rejects {name}" (fun () ->
        let output = runTransportFixture None ((messages |> List.map assistantMessage) @ [agentResult 0]) 0
        equal (String.concat "\n" messages) output
        equal (Some "Invalid or contradictory SUBTASK_BLOCKED request; owner must inspect the retained attempt.")
              (SchedulerState.blockRequest output))

if fixtureArgs |> List.contains "--profile-only" then
    if not regressionFailures.IsEmpty then failwith $"Profile regressions failed: {regressionFailures}"
    printfn "PASS bounded target provenance and fresh/resume/subagent profile regressions"
    exit 0

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
    $response = $env:FIXTURE_RESPONSE + "`nRALPH_AGENT_COMPLETE"
    [Console]::WriteLine((@{ type='assistant.message'; data=@{ content=$response; phase='final_answer'; toolRequests=@() } } | ConvertTo-Json -Compress -Depth 4))
    [Console]::WriteLine('{"type":"result","exitCode":0,"usage":{}}')
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
equal (Some 0) (resumeCheckpoint "completed checkpoint" false false)
equal completedHistory state.Backlog
printfn "PASS completed dispatch terminates successfully without losing history"

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
