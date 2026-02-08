#!/usr/bin/env dotnet fsi

// Ralph.fsx - Main orchestration script
// Loads satellite modules and provides agent execution + state management

// Load satellite files in dependency order
#load "Utils.fsx"
#load "TypeDefinitions.fsx"
#load "Prompting.fsx"
#load "GUI.fsx"
#load "CI_Retries.fsx"

// Additional dependencies not in satellites
#r "nuget: Fli"
#r "nuget: Spectre.Console"

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Fli
open Spectre.Console
open Spectre.Console.Rendering

// Import from satellite modules
open Utils
open XmlHelpers
open TypeDefinitions
open Prompting
open GUI
open CI_Retries

// ============================================================================
// MUTABLE STATE
// ============================================================================

let mutable state: State = emptyState
let mutable liveCtx: LiveDisplayContext option = None

// ============================================================================
// STATE UPDATE HELPERS (wrappers around StateOps for mutable state access)
// ============================================================================

let updateStatus sprintPath status msg =
    StateOps.updateStatus &state liveCtx sprintPath status msg

let updateTiming sprintPath (f: BacklogItemTiming -> BacklogItemTiming) =
    StateOps.updateTiming &state sprintPath f

let addIterationReason sprintPath iter reason =
    StateOps.addIterationReason &state sprintPath iter reason

let addIterationRecord sprintPath (record: IterationRecord) =
    StateOps.addIterationRecord &state sprintPath record

let addVerifierResultToLastIteration sprintPath verifierName passed summary =
    StateOps.addVerifierResultToLastIteration &state sprintPath verifierName passed summary

let updateDoDResults sprintPath (results: DoDResult list) =
    StateOps.updateDoDResults &state sprintPath results

let startItemTiming sprintPath =
    StateOps.startItemTiming &state sprintPath

let endItemTiming sprintPath summary =
    StateOps.endItemTiming &state liveCtx sprintPath summary

let getItemTiming sprintPath =
    StateOps.getItemTiming state sprintPath

let setMessage msg =
    StateOps.setMessage &state liveCtx msg

// ============================================================================
// DASHBOARD
// ============================================================================

let buildDashboard () =
    GUI.buildDashboard state (Verifiers.listAll ())

// ============================================================================
// AGENT EXECUTION
// ============================================================================

let runAgent (prompt: string) (title: string) (_showWindow: bool) = async {
    Directory.CreateDirectory Config.ralphDir |> ignore
    
    // Mark agent as running with task info
    state <- { state with AgentStartTime = Some DateTime.Now; CurrentAgentTask = title }
    liveCtx |> Option.iter (fun ctx -> ctx.UpdateTarget(buildDashboard()); ctx.Refresh())
    
    // Run copilot via Fli
    let result = 
        cli {
            Exec "copilot"
            Arguments [| "--allow-all-tools"; "--allow-all-paths"; "--no-ask-user";"--no-color";"--plain-diff";"-s";"--model"; Config.Model; "--stream"; "off" |]
            Input prompt
        }
        |> Command.execute
    
    state <- { state with AgentStartTime = None; CurrentAgentTask = "" }
    liveCtx |> Option.iter (fun ctx -> ctx.UpdateTarget(buildDashboard()); ctx.Refresh())
    
    return result.Text |> Option.defaultValue ""
}

// ============================================================================
// VERIFICATION
// ============================================================================

let verifyStage showWin sprintFilePath (verifierName: VerifierName) (sprintItem: BacklogItem) = async {
    setMessage $"Verifying {verifierName}..."
    
    let timing = getItemTiming sprintFilePath |> Option.defaultValue emptyTiming
    let approvedCommits = timing.ApprovedCommits
    let currentCommit = Git.getHeadCommit()
    let sprintContext = buildSprintVerificationContext sprintItem approvedCommits currentCommit verifierName
    let prompt = Verifiers.getPrompt verifierName + sprintContext + verifierSuffix
    let! out = runAgent prompt $"Verify-{verifierName}" showWin
    
    match interpretVerifierOutput out with
    | VPassed summary ->
        state <- { state with LastVerifierLog = Some (verifierName, true, summary) }
        addVerifierResultToLastIteration sprintFilePath verifierName true summary
        setMessage $"[green]✓ {verifierName} passed[/]"
        return Ok ()
    | VFailed summary ->
        state <- { state with LastVerifierLog = Some (verifierName, false, summary) }
        addVerifierResultToLastIteration sprintFilePath verifierName false summary
        setMessage $"[red]✗ {verifierName} failed[/]"
        return Error $"{verifierName}: {summary}"
    | VInconclusive summary ->
        state <- { state with LastVerifierLog = Some (verifierName, false, summary) }
        addVerifierResultToLastIteration sprintFilePath verifierName false summary
        setMessage $"[yellow]{verifierName} inconclusive[/]"
        return Error $"{verifierName} verification did not output VERIFY_PASSED or VERIFY_FAILED"
}

let updateVerifierStatus sprintFilePath (verifierName: VerifierName) (passed: bool) =
    updateTiming sprintFilePath (fun t -> 
        let prevCount = 
            match t.VerifierResults.TryFind verifierName with
            | Some (Passed n) | Some (Failed n) -> n
            | _ -> 0
        let newStatus = if passed then Passed (prevCount + 1) else Failed (prevCount + 1)
        // Capture commit hash when verifier passes (for incremental verification)
        let approvedCommits = 
            if passed then
                match Git.getHeadCommit() with
                | Some commit -> t.ApprovedCommits.Add(verifierName, commit)
                | None -> t.ApprovedCommits
            else t.ApprovedCommits
        { t with VerifierResults = t.VerifierResults.Add(verifierName, newStatus); ApprovedCommits = approvedCommits })
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

let runAllVerifiers showWin sprintFilePath (sprintItem: BacklogItem) = async {
    let verifierNames = Verifiers.listAll ()
    let mutable allPassed = true
    
    for name in verifierNames do
        match! verifyStage showWin sprintFilePath name sprintItem with
        | Ok () -> 
            updateVerifierStatus sprintFilePath name true
        | Error e -> 
            updateVerifierStatus sprintFilePath name false
            allPassed <- false
    
    if allPassed then
        return Ok ()
    else
        return Error "One or more verifiers failed"
}

let showPlan (sprints: BacklogItem list) (overview: string) =
    state <- { state with PlanOverview = $"{overview} ({sprints.Length} sprints)" }
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

// ============================================================================
// BACKLOG EXECUTION
// ============================================================================

let rec runBacklogItem (item: BacklogItem) iter totalIter feedback showWin = async {
    // Hard limit - give up completely
    if iter > Config.MaxIterations then return Error "Max iterations"
    // Arbiter threshold - request arbiter intervention (recoverable)
    elif iter > Config.ArbiterThreshold then return Error "ARBITER_NEEDED"
    else
        if iter = 1 then
            startItemTiming item.FilePath
        
        let timing = getItemTiming item.FilePath |> Option.defaultValue emptyTiming
        let prevDoDResults = timing.LastDoDResults
        
        let pastSprints: XmlPrompt.SprintHistory list = 
            state.Backlog 
            |> List.filter (fun (s, status, _) -> s.Order < item.Order && match status with Done _ -> true | _ -> false)
            |> List.map (fun (s, _, t) -> 
                { SprintId = s.Order
                  SprintName = s.Name
                  Summary = t.Summary |> Option.defaultValue "Completed" } : XmlPrompt.SprintHistory)
        
        let pastSteps: XmlPrompt.StepHistory list =
            timing.IterationReasons 
            |> List.map (fun (i, reason) ->
                { Iteration = i
                  Passed = false
                  Summary = reason } : XmlPrompt.StepHistory)
        
        let iterHistory: XmlPrompt.IterationHistory list =
            timing.IterationHistory 
            |> List.map (fun r ->
                { Iteration = r.Iteration
                  AgentOutput = r.AgentOutput
                  VerifierResults = r.VerifierResults } : XmlPrompt.IterationHistory)
        
        updateStatus item.FilePath (Running (Implement, iter)) $"Sprint {item.Order}: Implement iteration {iter}"
        
        let prompt = Prompts.implement item iter feedback prevDoDResults pastSprints pastSteps iterHistory
        
        let! out = runAgent prompt ($"Implement-{item.Order}") showWin
        
        let currentRecord: IterationRecord = {
            Iteration = iter
            AgentOutput = out
            VerifierResults = []
        }
        addIterationRecord item.FilePath currentRecord
        
        let retry fb dodResults = 
            let reason = fb |> List.tryHead |> Option.defaultValue "Unknown"
            addIterationReason item.FilePath iter reason
            updateDoDResults item.FilePath dodResults
            state <- { state with CompletedIterations = state.CompletedIterations + 1 }
            runBacklogItem item (iter + 1) (totalIter + 1) fb showWin
        
        let isComplete = hasSignal "SUBTASK_COMPLETE" out || hasSignal "SUBTASK COMPLETE" out
        
        if isComplete then
            state <- { state with CompletedIterations = state.CompletedIterations + 1 }
            match! runAllVerifiers showWin item.FilePath item with 
            | Ok _ -> 
                let allPassed = item.DoD |> List.map (fun c -> { Criterion = c; Passed = Some true })
                updateDoDResults item.FilePath allPassed
                endItemTiming item.FilePath $"Completed in {totalIter + 1} iterations"
                updateStatus item.FilePath (Done (totalIter + 1)) $"Sprint {item.Order} complete in {totalIter + 1} iterations"
                return Ok ()
            | Error e -> return! retry [e] prevDoDResults
        else
            return! retry (feedback @ [$"Did not output SUBTASK_COMPLETE"]) prevDoDResults
}

let rec runAllBacklogItems items showWin = async {
    match items with
    | [] -> return Ok ()
    | item :: rest ->
        match! runBacklogItem item 1 0 [] showWin with
        | Ok () -> return! runAllBacklogItems rest showWin
        | Error e -> return Error $"Sprint {item.Order} ({item.Name}) failed: {e}"
}

// ============================================================================
// FINAL VERIFICATION
// ============================================================================

let runFinalVerifiers showWin = async {
    state <- { state with CurrentPhase = "Final Verification" }
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
    
    let verifierNames = Verifiers.listAll ()
    let mutable allPassed = true
    
    let sprintFiles = SprintFiles.listSprints()
    let finalContext = buildFinalVerificationContext sprintFiles
    
    for name in verifierNames do
        setMessage $"Final verification: {name}..."
        
        let prevCount = 
            match state.FinalVerifierResults.TryFind name with
            | Some (Passed n) | Some (Failed n) -> n
            | _ -> 0
        
        state <- { state with FinalVerifierResults = state.FinalVerifierResults.Add(name, NotStarted) }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        
        let prompt = Verifiers.getPrompt name + finalContext + verifierSuffix
        let! out = runAgent prompt $"FinalVerify-{name}" showWin
        
        match interpretVerifierOutput out with
        | VPassed summary ->
            state <- { state with 
                        FinalVerifierResults = state.FinalVerifierResults.Add(name, Passed (prevCount + 1))
                        FinalVerifierSummaries = state.FinalVerifierSummaries.Add(name, summary)
                        LastVerifierLog = Some (name, true, summary) }
        | VFailed summary | VInconclusive summary ->
            state <- { state with 
                        FinalVerifierResults = state.FinalVerifierResults.Add(name, Failed (prevCount + 1))
                        FinalVerifierSummaries = state.FinalVerifierSummaries.Add(name, summary)
                        LastVerifierLog = Some (name, false, summary) }
            allPassed <- false
        
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
    
    return allPassed
}

let createFixupSprint (failedVerifiers: VerifierName list) (fixupNumber: int) : BacklogItem =
    let failedNames = failedVerifiers |> String.concat ", "
    let nextOrder = 
        state.Backlog 
        |> List.map (fun (item, _, _) -> item.Order) 
        |> List.max 
        |> (+) 1
    let fixupPath = Path.Combine(Config.sprintsDir, $"{nextOrder:D2}_Fixup_{fixupNumber}.md")
    {
        FilePath = fixupPath
        Order = nextOrder
        Name = $"Fixup #{fixupNumber}"
        Description = $"Fix issues identified by final verification. The following verifiers FAILED on the complete feature: {failedNames}. Review their feedback and make targeted fixes WITHOUT breaking existing functionality."
        DoD = [
            "All previously passing tests still pass"
            "Fixes address the specific issues flagged by verifiers"
            "No new regressions introduced"
        ] @ (failedVerifiers |> List.map (fun v -> $"{v} verifier passes"))
    }

let rec finalChecksWithFixup showWin maxFixupSprints currentFixup = async {
    setMessage $"Running final verification (fixup cycle {currentFixup + 1}/{maxFixupSprints + 1})..."
    let! passed = runFinalVerifiers showWin
    
    if passed then
        return true
    elif currentFixup >= maxFixupSprints then
        state <- { state with ErrorLog = Some $"Final verification failed after {maxFixupSprints} fixup sprints" }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        return false
    else
        let failed = 
            state.FinalVerifierResults 
            |> Map.toList 
            |> List.filter (fun (_, status) -> match status with Failed _ -> true | _ -> false)
            |> List.map fst
        
        let failedNames = failed |> String.concat ", "
        setMessage $"Fixup #{currentFixup + 1}: {failedNames}"
        
        let fixupItem = createFixupSprint failed (currentFixup + 1)
        
        state <- { state with 
                       Backlog = state.Backlog @ [(fixupItem, Todo, emptyTiming)]
                       TotalEstimatedIterations = state.TotalEstimatedIterations + 3
                 }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        
        match! runBacklogItem fixupItem 1 0 [] showWin with
        | Ok () ->
            return! finalChecksWithFixup showWin maxFixupSprints (currentFixup + 1)
        | Error e ->
            state <- { state with ErrorLog = Some $"Fixup sprint failed: {e}" }
            liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
            return false
}

let finalChecks showWin =
    setMessage "Running final verification on complete feature..."
    finalChecksWithFixup showWin 3 0 |> Async.RunSynchronously

// ============================================================================
// MAIN APPLICATION FLOW
// ============================================================================

let runWithLive (sprints: BacklogItem list) showWin (originalRequest: string) = 
    let estimatedIterations = sprints.Length * 3
    
    let overview = BacklogFile.readOverview() |> Option.defaultValue originalRequest
    
    let existingByPath = 
        state.Backlog 
        |> List.map (fun (item, status, timing) -> (item.FilePath, (item, status, timing)))
        |> Map.ofList
    
    let mergedBacklog = 
        sprints 
        |> List.map (fun s ->
            match existingByPath.TryFind s.FilePath with
            | Some (_, (Done _ as status), timing) -> (s, status, timing)
            | Some (_, status, timing) when state.CurrentPhase = "Arbiter" -> (s, Todo, emptyTiming)
            | Some (_, status, timing) -> (s, status, timing)
            | None -> (s, Todo, emptyTiming))
    
    state <- { 
        Backlog = mergedBacklog
        StartTime = if state.StartTime = DateTime.MinValue then DateTime.Now else state.StartTime
        Message = "Starting..."
        AgentStartTime = None
        TotalEstimatedIterations = estimatedIterations
        CompletedIterations = state.CompletedIterations
        FinalVerifierResults = Map.empty
        FinalVerifierSummaries = Map.empty
        CIStatus = None
        CurrentPhase = "Executing"
        CurrentAgentTask = ""
        LastVerifierLog = None
        PlanOverview = overview
        ErrorLog = None
    }
    
    Directory.CreateDirectory Config.ralphDir |> ignore
    SprintFiles.ensureDir()
    
    let mutable result: Result<unit, string> = Ok ()
    let mutable finished = false
    
    let workTask = Task.Run(fun () ->
        try
            let sprintsToRun = mergedBacklog |> List.filter (fun (_, status, _) -> match status with Done _ -> false | _ -> true) |> List.map (fun (s, _, _) -> s)
            let r = runAllBacklogItems sprintsToRun showWin |> Async.RunSynchronously
            result <- r
            match r with
            | Ok () ->
                let passed = finalChecks showWin
                if passed then
                    state <- { state with CurrentPhase = "Complete"; Message = "[green bold]WORKFLOW COMPLETE[/]" }
                else
                    state <- { state with CurrentPhase = "Complete"; Message = "[yellow]Completed with some issues[/]" }
            | Error e ->
                state <- { state with ErrorLog = Some e }
        with ex ->
            result <- Error ex.Message
            state <- { state with ErrorLog = Some ex.Message }
        finished <- true
    )
    
    AnsiConsole.Live(buildDashboard())
        .AutoClear(true)
        .Overflow(VerticalOverflow.Ellipsis)
        .Start(fun ctx ->
            liveCtx <- Some ctx
            while not finished do
                ctx.UpdateTarget(buildDashboard())
                ctx.Refresh()
                Thread.Sleep(500)
            ctx.UpdateTarget(buildDashboard())
            ctx.Refresh()
            liveCtx <- None
        )
    
    workTask.Wait()
    
    AnsiConsole.WriteLine()
    match result with
    | Ok () -> 
        AnsiConsole.Write(FigletText("COMPLETE").Color(Color.Green))
    | Error _ -> 
        AnsiConsole.Write(FigletText("FAILED").Color(Color.Red))
    
    result

let invokeArbiter (originalRequest: string) (showWin: bool) : Result<BacklogItem list, string> =
    state <- { state with CurrentPhase = "Arbiter"; Message = "Invoking arbiter for recovery..." }
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
    
    // Find the failed sprint (the one that's Running or first non-Done)
    let failedItem = 
        state.Backlog 
        |> List.tryFind (fun (_, status, _) -> 
            match status with Running _ -> true | _ -> false)
        |> Option.orElse (
            state.Backlog 
            |> List.tryFind (fun (_, status, _) -> 
                match status with Done _ -> false | _ -> true))
        |> Option.map (fun (item, _, _) -> item)
    
    // Build completed sprints as SprintHistory
    let completedSprints: XmlPrompt.SprintHistory list = 
        state.Backlog 
        |> List.choose (fun (item, status, timing) -> 
            match status with 
            | Done iters -> 
                Some { SprintId = item.Order
                       SprintName = item.Name
                       Summary = timing.Summary |> Option.defaultValue $"Completed in {iters} iterations" }
            | _ -> None)
    
    // Build pending sprints with file paths
    let pendingSprints: (int * string * string) list = 
        state.Backlog 
        |> List.choose (fun (item, status, _) -> 
            match status with Done _ -> None | _ -> Some (item.Order, item.Name, item.FilePath))
    
    // Build failed sprint context with iteration history
    let failedContext: XmlPrompt.FailedSprintContext option =
        failedItem |> Option.bind (fun item ->
            let timing = getItemTiming item.FilePath
            timing |> Option.map (fun t ->
                // Count verifier failures
                let verifierFailureCounts = 
                    t.IterationHistory 
                    |> List.collect (fun h -> h.VerifierResults)
                    |> List.filter (fun (_, passed, _) -> not passed)
                    |> List.groupBy (fun (name, _, _) -> name)
                    |> List.map (fun (name, failures) -> (name, failures.Length))
                    |> Map.ofList
                
                // Get last 3 iterations for context (avoid token bloat)
                let lastIterations = 
                    t.IterationHistory 
                    |> List.sortByDescending (fun h -> h.Iteration)
                    |> List.truncate 3
                    |> List.sortBy (fun h -> h.Iteration)
                    |> List.map (fun h -> 
                        { Iteration = h.Iteration
                          AgentOutput = h.AgentOutput
                          VerifierResults = h.VerifierResults } : XmlPrompt.IterationHistory)
                
                { SprintOrder = item.Order
                  SprintName = item.Name
                  SprintFilePath = item.FilePath
                  IterationsSpent = t.IterationHistory.Length
                  LastIterations = lastIterations
                  VerifierFailureCounts = verifierFailureCounts } : XmlPrompt.FailedSprintContext
            ))
    
    let completedBacklogItems = 
        state.Backlog 
        |> List.filter (fun (_, status, _) -> match status with Done _ -> true | _ -> false)
    let completedFilePaths = completedBacklogItems |> List.map (fun (item, _, _) -> item.FilePath) |> Set.ofList
    
    let arbiterPrompt = Prompts.arbiter originalRequest failedContext completedSprints pendingSprints
    let arbiterResult = runAgent arbiterPrompt "Arbiter" showWin |> Async.RunSynchronously
    
    let newSprintsFromDisk = SprintFiles.readAllSprints()
    
    let newUncompletedSprints = 
        newSprintsFromDisk 
        |> List.filter (fun s -> not (completedFilePaths.Contains s.FilePath))
    
    let completedItems = completedBacklogItems |> List.map (fun (item, _, _) -> item)
    let mergedSprints = completedItems @ newUncompletedSprints
    
    if mergedSprints.Length > 0 then
        setMessage "[green]Arbiter produced recovery plan[/]"
        Ok mergedSprints
    else
        state <- { state with ErrorLog = Some "Arbiter failed: No sprint files found" }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        Error "Arbiter could not produce valid plan"

let rec run request showWin autoApprove arbiterCount (ciFailureContext: string option) = 
    if arbiterCount >= 3 then
        AnsiConsole.MarkupLine "[red]Max arbiter attempts (3). Stopping.[/]"
        1
    else
        state <- { 
            Backlog = []; StartTime = DateTime.Now; Message = "Planning..."
            AgentStartTime = None; TotalEstimatedIterations = 0; CompletedIterations = 0
            FinalVerifierResults = Map.empty; FinalVerifierSummaries = Map.empty; CIStatus = None
            CurrentPhase = "Planning"; CurrentAgentTask = "Architect"
            LastVerifierLog = None; PlanOverview = request; ErrorLog = None
        }
        
        Directory.CreateDirectory Config.ralphDir |> ignore
        SprintFiles.ensureDir()
        
        let mutable sprintsResult: BacklogItem list = []
        let mutable planFinished = false
        let mutable architectOutput = ""
        
        // Use CI-aware architect if we have failure context
        let architectPrompt = 
            match ciFailureContext with
            | Some ciOutput -> Prompts.architectWithCIContext request ciOutput
            | None -> Prompts.architect request
        
        let planTask = Task.Run(fun () ->
            try
                architectOutput <- runAgent architectPrompt "Architect" showWin |> Async.RunSynchronously
                sprintsResult <- SprintFiles.readAllSprints()
            with ex ->
                state <- { state with ErrorLog = Some ex.Message }
            planFinished <- true
        )
        
        AnsiConsole.Live(buildDashboard())
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(fun ctx ->
                liveCtx <- Some ctx
                while not planFinished do
                    ctx.UpdateTarget(buildDashboard())
                    ctx.Refresh()
                    Thread.Sleep(500)
                ctx.UpdateTarget(buildDashboard())
                ctx.Refresh()
                liveCtx <- None
            )
        
        planTask.Wait()
        
        let overview = BacklogFile.readOverview() |> Option.defaultValue request
        
        if sprintsResult.Length = 0 then
            // 0 sprints is pathological - show output and let human decide
            AnsiConsole.MarkupLine "[red bold]PLANNING PRODUCED 0 SPRINTS[/]"
            AnsiConsole.WriteLine()
            AnsiConsole.MarkupLine "[yellow]Architect output:[/]"
            AnsiConsole.WriteLine()
            
            // Show truncated output in a panel
            let truncatedOutput = 
                if architectOutput.Length > 4000 then architectOutput.Substring(0, 4000) + "\n... (truncated)"
                else architectOutput
            AnsiConsole.Write(Panel(truncatedOutput).Header("Architect Response"))
            
            AnsiConsole.WriteLine()
            AnsiConsole.MarkupLine "[yellow]Check:[/]"
            AnsiConsole.MarkupLine $"  - Sprint files dir: {Config.sprintsDir}"
            AnsiConsole.MarkupLine $"  - Backlog file: {Config.backlogFile}"
            AnsiConsole.WriteLine()
            
            if autoApprove then
                // In auto mode, can't ask user - just fail
                AnsiConsole.MarkupLine "[red]Running in --yes mode, cannot prompt for input. Failing.[/]"
                1
            else
                let choice = AnsiConsole.Prompt(
                    SelectionPrompt<string>()
                        .Title("[green]What would you like to do?[/]")
                        .AddChoices([| "Retry with same request"; "Enter new request"; "Quit" |]))
                
                match choice with
                | "Retry with same request" -> run request showWin autoApprove 0 ciFailureContext
                | "Enter new request" ->
                    let newRequest = AnsiConsole.Ask<string> "[green]New request:[/] "
                    run newRequest showWin autoApprove 0 None
                | _ -> 1
        else
            showPlan sprintsResult overview
            if autoApprove || AnsiConsole.Confirm("Execute? ", true) then
                match runWithLive sprintsResult showWin request with
                | Ok () -> 0
                | Error e -> 
                    match invokeArbiter request showWin with
                    | Ok newSprints ->
                        showPlan newSprints overview
                        match runWithLive newSprints showWin request with
                        | Ok () -> 0
                        | Error e2 -> run request showWin autoApprove (arbiterCount + 1) None
                    | Error _ ->
                        run request showWin autoApprove (arbiterCount + 1) None
            else 0

// ============================================================================
// ENTRY POINT
// ============================================================================

let runInteractive () = 
    AnsiConsole.Write(FigletText("RALPH").Color(Color.Yellow))
    let showWin = AnsiConsole.Confirm("Show agent windows? ", true)
    let request = AnsiConsole.Ask<string> "[green]Request:[/] "
    run request showWin false 0 None

let rec runWithPush request showWin auto ciAttempt =
    if ciAttempt >= 3 then
        AnsiConsole.MarkupLine "[red]Max CI retry attempts (3). Stopping.[/]"
        1
    else
        let result = run request showWin auto 0 None
        if result = 0 then
            setMessage "[cyan]Pushing changes and monitoring CI...[/]"
            match CIMonitor.runGitPush() with
            | Ok _ ->
                setMessage "[green]✓ Pushed successfully[/]"
                let status = CIMonitor.pollCI setMessage 120 |> Async.RunSynchronously  // 2 hour timeout
                match status with
                | CIMonitor.Success -> 
                    AnsiConsole.MarkupLine "[green]✓ CI passed![/]"
                    0
                | CIMonitor.Pending -> 
                    AnsiConsole.MarkupLine "[yellow]CI still pending after timeout[/]"
                    0  // Treat pending as success (user can check manually)
                | CIMonitor.Failed ciOutput ->
                    AnsiConsole.MarkupLine "[red]✗ CI failed - restarting with CI context...[/]"
                    // Restart the ENTIRE flow with CI failure context
                    // Architect will see the failure and can create fixup sprints
                    runWithCIContext request showWin auto (ciAttempt + 1) ciOutput
            | Error e ->
                state <- { state with ErrorLog = Some $"Push failed: {e}" }
                1
        else result

and runWithCIContext request showWin auto ciAttempt ciOutput =
    let result = run request showWin auto 0 (Some ciOutput)
    if result = 0 then
        setMessage "[cyan]Pushing CI fixes...[/]"
        match CIMonitor.runGitPush() with
        | Ok _ ->
            setMessage "[green]✓ Pushed fixes[/]"
            let status = CIMonitor.pollCI setMessage 120 |> Async.RunSynchronously
            match status with
            | CIMonitor.Success -> 
                AnsiConsole.MarkupLine "[green]✓ CI passed after fixes![/]"
                0
            | CIMonitor.Pending -> 0
            | CIMonitor.Failed newCiOutput ->
                AnsiConsole.MarkupLine "[red]✗ CI still failing - another retry...[/]"
                runWithCIContext request showWin auto (ciAttempt + 1) newCiOutput
        | Error e ->
            state <- { state with ErrorLog = Some $"Push failed: {e}" }
            1
    else result

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| [] -> runInteractive () |> ignore
| ["--help"] | ["-h"] ->
    printfn "Ralph - Autonomous AI Coding Loop\n"
    printfn "Usage:  dotnet fsi Ralph.fsx [request] [--yes] [--hidden] [--push] [--help]"
    printfn ""
    printfn "Options:"
    printfn "  --yes       Auto-approve all prompts"
    printfn "  --hidden    Hide agent windows"
    printfn "  --push      Push after completion and monitor CI, fix failures"
| args ->
    let request = args |> List.filter (fun a -> not (a.StartsWith "--")) |> String.concat " "
    let showWin = not (List.contains "--hidden" args)
    let auto = List.contains "--yes" args
    let push = List.contains "--push" args
    if String.IsNullOrWhiteSpace request then printfn "No request.  Use --help"; exit 1
    if push then runWithPush request showWin auto 0 |> exit
    else run request showWin auto 0 None |> exit
