#!/usr/bin/env dotnet fsi

#load "Utils.fsx"
#load "TypeDefinitions.fsx"
#load "Prompting.fsx"
#load "GUI.fsx"
#load "CI_Retries.fsx"

#r "nuget: Fli"
#r "nuget: Spectre.Console"

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Fli
open Spectre.Console
open Spectre.Console.Rendering
open Utils
open XmlHelpers
open TypeDefinitions
open Prompting
open GUI
open CI_Retries

let mutable state: State = emptyState
let mutable liveCtx: LiveDisplayContext option = None

let updateStatus sprintPath status msg = StateOps.updateStatus &state liveCtx sprintPath status msg
let updateTiming sprintPath f = StateOps.updateTiming &state sprintPath f
let addIterationReason sprintPath iter reason = StateOps.addIterationReason &state sprintPath iter reason
let addIterationRecord sprintPath record = StateOps.addIterationRecord &state sprintPath record
let addVerifierResultToLastIteration sprintPath verifierName passed summary = StateOps.addVerifierResultToLastIteration &state sprintPath verifierName passed summary
let updateDoDResults sprintPath results = StateOps.updateDoDResults &state sprintPath results
let startItemTiming sprintPath = StateOps.startItemTiming &state sprintPath
let endItemTiming sprintPath summary = StateOps.endItemTiming &state liveCtx sprintPath summary
let getItemTiming sprintPath = StateOps.getItemTiming state sprintPath
let setMessage msg = StateOps.setMessage &state liveCtx msg

let buildDashboard () = GUI.buildDashboard state (Verifiers.listAll ())


/// Run a copilot agent session. Returns (output, sessionId) where sessionId can be used with askFollowUp.
/// If resumeSessionId is provided (Some), resumes that session instead of starting a new one.
let runAgent (prompt: string) (title: string) (_showWindow: bool) (resumeSessionId: string option) = async {
    Directory.CreateDirectory Config.ralphDir |> ignore
    
    let sessionId = resumeSessionId |> Option.defaultWith (fun () -> Guid.NewGuid().ToString())
    Logging.info $"Starting agent: {title} (session: {sessionId})"
    
    // Mark agent as running with task info
    state <- { state with AgentStartTime = Some DateTime.Now; CurrentAgentTask = title }
    liveCtx |> Option.iter (fun ctx -> ctx.UpdateTarget(buildDashboard()); ctx.Refresh())
    
    let mutable output = ""
    let mutable exn: exn option = None
    try
        // Escape curly braces - Fli uses StreamWriter.WriteLine(format, arg) internally
        // which interprets { and } as format placeholders
        let escapedPrompt = prompt.Replace("{", "{{").Replace("}", "}}")
        
        // Build arguments: include --resume {sessionId} for session tracking
        let baseArgs = [| "--allow-all-tools"; "--allow-all-paths"; "--no-ask-user";"--no-color";"--plain-diff";"-s";"--model"; Config.Model; "--effort"; Config.Effort; "--stream"; "off"; "--resume"; sessionId |]
        
        // Run copilot via Fli
        let result = 
            cli {
                Exec "copilot"
                Arguments baseArgs
                Input escapedPrompt
            }
            |> Command.execute
        
        output <- result.Text |> Option.defaultValue ""
        Logging.info $"Agent {title} completed, output length: {output.Length}"
        
        if result.ExitCode <> 0 then
            let err = result.Error |> Option.defaultValue "(no error text)"
            Logging.error $"Agent {title} exited with code {result.ExitCode}: {err}"
    with ex ->
        Logging.exn ex $"runAgent({title})"
        exn <- Some ex
    
    // Always cleanup agent state
    state <- { state with AgentStartTime = None; CurrentAgentTask = "" }
    liveCtx |> Option.iter (fun ctx -> ctx.UpdateTarget(buildDashboard()); ctx.Refresh())
    
    match exn with
    | Some ex -> return raise ex
    | None -> return (output, sessionId)
}

/// Resume a previous session with a short clarifying question.
/// Returns only the follow-up response (not the original output).
let askFollowUp (sessionId: string) (question: string) (title: string) = async {
    Logging.info $"askFollowUp: resuming session {sessionId} for {title}"
    let! (response, _) = runAgent question $"{title}-followup" false (Some sessionId)
    Logging.info $"askFollowUp response length: {response.Length}"
    return response
}

/// Generic disambiguation: ask a follow-up question and interpret two complementary signals.
/// Returns true if positiveSignal is found, false if negativeSignal is found or still ambiguous.
let disambiguateSignal (sessionId: string) (context: string) (question: string) (positiveSignal: string) (negativeSignal: string) = async {
    let! response = askFollowUp sessionId question $"Disambiguate-{context}"
    let hasPositive = hasSignalAny positiveSignal response
    let hasNegative = hasSignalAny negativeSignal response
    Logging.info $"disambiguate {context}: positive={hasPositive}, negative={hasNegative}, response='{response.Trim()}'"
    if hasPositive && not hasNegative then return true
    elif hasNegative && not hasPositive then return false
    else
        Logging.error $"disambiguateSignal: still ambiguous after follow-up for {context}"
        return false
}

/// When verifier output is ambiguous (VInconclusive), resume the session to get a clear answer.
let resolveVerifierAmbiguity (sessionId: string) (originalSummary: string) (verifierName: string) = async {
    let! passed = disambiguateSignal sessionId verifierName
                    "Your previous response did not contain a clear VERIFY_PASSED or VERIFY_FAILED signal (or contained both). Based on your full analysis above, is the final verdict VERIFY_PASSED or VERIFY_FAILED? Output ONLY that single token, nothing else."
                    "VERIFY_PASSED" "VERIFY_FAILED"
    return if passed then VPassed originalSummary else VFailed originalSummary
}

/// When implementor output is ambiguous about SUBTASK_COMPLETE, resume session to clarify.
let resolveSubtaskAmbiguity (sessionId: string) (sprintName: string) =
    disambiguateSignal sessionId $"Subtask-{sprintName}"
        "Your previous response was unclear about completion status. Did you complete ALL work for this sprint? Output ONLY either SUBTASK_COMPLETE or SUBTASK_INCOMPLETE, nothing else."
        "SUBTASK_COMPLETE" "SUBTASK_INCOMPLETE"

/// Interpret verifier output and automatically disambiguate if inconclusive.
let interpretAndDisambiguate (output: string) (sessionId: string) (verifierName: string) = async {
    match interpretVerifierOutput output with
    | VInconclusive summary ->
        Logging.info $"Verifier {verifierName} inconclusive, attempting disambiguation"
        return! resolveVerifierAmbiguity sessionId summary verifierName
    | outcome -> return outcome
}


let verifyStage showWin sprintFilePath (verifierName: string) (sprintItem: BacklogItem) = async {
    setMessage $"Verifying {verifierName}..."
    
    let timing = getItemTiming sprintFilePath |> Option.defaultValue emptyTiming
    let approvedCommits = timing.ApprovedCommits
    let currentCommit = Git.getHeadCommit()
    let sprintContext = buildSprintVerificationContext sprintItem approvedCommits currentCommit verifierName
    let prompt = verifierPreamble + Verifiers.getPrompt verifierName + sprintContext + verifierSuffix
    let! (out, sessionId) = runAgent prompt $"Verify-{verifierName}" showWin None
    let! outcome = interpretAndDisambiguate out sessionId verifierName
    
    match outcome with
    | VPassed summary ->
        state <- { state with LastVerifierLog = Some (sprintItem.Order, verifierName, true, summary) }
        addVerifierResultToLastIteration sprintFilePath verifierName true summary
        setMessage $"[green]✓ {verifierName} passed[/]"
        return Ok ()
    | VFailed summary | VInconclusive summary ->
        state <- { state with LastVerifierLog = Some (sprintItem.Order, verifierName, false, summary) }
        addVerifierResultToLastIteration sprintFilePath verifierName false summary
        setMessage $"[red]✗ {verifierName} failed[/]"
        return Error $"{verifierName}: {summary}"
}

let updateVerifierStatus sprintFilePath (verifierName: string) (passed: bool) =
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
    let allVerifiers = Verifiers.listAll ()
    // Filter by TargetVerifiers (fixup sprints only run failed ones) then exclude EliminatedVerifiers (cutter)
    let activeVerifiers =
        match sprintItem.TargetVerifiers with
        | Some targets -> targets |> List.filter (fun v -> Verifiers.isValid v)
        | None -> allVerifiers
        |> List.filter (fun v -> not (sprintItem.EliminatedVerifiers.Contains v))
    
    // Mark skipped verifiers in timing state
    let skippedVerifiers = allVerifiers |> List.filter (fun v -> not (activeVerifiers |> List.contains v))
    for name in skippedVerifiers do
        updateVerifierStatus sprintFilePath name true  // count as pass for display
    
    let mutable allPassed = true
    
    for name in activeVerifiers do
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

/// Run the live dashboard refresh loop while a task executes on a background thread.
let withLiveDashboard (isFinished: unit -> bool) (task: Task) =
    AnsiConsole.Live(buildDashboard())
        .AutoClear(false)
        .Overflow(VerticalOverflow.Ellipsis)
        .Start(fun ctx ->
            liveCtx <- Some ctx
            while not (isFinished()) do
                ctx.UpdateTarget(buildDashboard())
                ctx.Refresh()
                Thread.Sleep(500)
            ctx.UpdateTarget(buildDashboard())
            ctx.Refresh()
            liveCtx <- None
        )
    task.Wait()

let showPlan (sprints: BacklogItem list) (overview: string) =
    state <- { state with PlanOverview = $"{overview} ({sprints.Length} sprints)" }
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

/// Run the verifier cutter on each sprint to eliminate irrelevant verifiers.
/// Returns updated sprint list with EliminatedVerifiers populated.
let runCutter (sprints: BacklogItem list) showWin =
    let allVerifiers = Verifiers.listAll ()
    sprints |> List.map (fun sprint ->
        try
            let prompt = Prompts.cutter sprint.Name sprint.Description sprint.DoD allVerifiers
            let (output, _) = runAgent prompt $"Cutter-{sprint.Order}" showWin None |> Async.RunSynchronously
            
            // Parse <EliminatedVerifiers>...</EliminatedVerifiers>
            let m = Regex.Match(output, @"<EliminatedVerifiers>(.*?)</EliminatedVerifiers>", RegexOptions.Singleline)
            if m.Success then
                let raw = m.Groups.[1].Value.Trim()
                if String.IsNullOrWhiteSpace raw then
                    Logging.info $"Cutter for sprint {sprint.Order} ({sprint.Name}): no verifiers eliminated"
                    sprint
                else
                    let eliminated = 
                        raw.Split([|','; ';'|], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map (fun s -> s.Trim())
                        // Never allow eliminating FUNCTIONAL or HONEST-ASSESSMENT
                        |> Array.filter (fun v -> v <> "FUNCTIONAL" && v <> "HONEST-ASSESSMENT" && Verifiers.isValid v)
                        |> Set.ofArray
                    let eliminatedStr = eliminated |> Set.toList |> String.concat ", "
                    Logging.info $"Cutter for sprint {sprint.Order} ({sprint.Name}): eliminated {eliminatedStr}"
                    { sprint with EliminatedVerifiers = eliminated }
            else
                Logging.info $"Cutter for sprint {sprint.Order} ({sprint.Name}): no tag found, keeping all verifiers"
                sprint
        with ex ->
            Logging.error $"Cutter failed for sprint {sprint.Order}: {ex.Message}. Keeping all verifiers."
            sprint
    )


let rec runBacklogItem (item: BacklogItem) iter totalIter feedback showWin = async {
    Logging.info $"runBacklogItem: Sprint {item.Order} ({item.Name}), iteration {iter}"
    
    // Hard limit - give up completely
    if iter > Config.MaxIterations then 
        Logging.error $"Sprint {item.Order} exceeded MaxIterations ({Config.MaxIterations})"
        return Error "Max iterations"
    // Arbiter threshold - request arbiter intervention (recoverable)
    elif iter > Config.ArbiterThreshold then 
        Logging.info $"Sprint {item.Order} exceeded ArbiterThreshold ({Config.ArbiterThreshold}), requesting arbiter"
        return Error "ARBITER_NEEDED"
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
        
        let! (out, sessionId) = runAgent prompt ($"Implement-{item.Order}") showWin None
        
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
        
        let hasCompleteSignal = hasSignalAny "SUBTASK_COMPLETE" out
        let hasIncompleteSignal = hasSignalAny "SUBTASK_INCOMPLETE" out
        
        // Determine completion: clear signal, or disambiguate via resume if ambiguous
        let! isComplete = async {
            if hasCompleteSignal && not hasIncompleteSignal then return true
            elif hasIncompleteSignal && not hasCompleteSignal then return false
            elif not hasCompleteSignal && not hasIncompleteSignal then
                // No signal at all — try to disambiguate
                Logging.info $"Sprint {item.Order}: no completion signal, attempting disambiguation"
                return! resolveSubtaskAmbiguity sessionId item.Name
            else
                // Both signals — ambiguous, disambiguate
                Logging.info $"Sprint {item.Order}: both complete/incomplete signals, attempting disambiguation"
                return! resolveSubtaskAmbiguity sessionId item.Name
        }
        
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

let rec runAllBacklogItems (items: BacklogItem list) showWin = async {
    Logging.info $"runAllBacklogItems: {items.Length} items remaining"
    match items with
    | [] -> 
        Logging.info "All backlog items completed successfully"
        return Ok ()
    | item :: rest ->
        match! runBacklogItem item 1 0 [] showWin with
        | Ok () -> return! runAllBacklogItems rest showWin
        | Error e -> 
            Logging.error $"Sprint {item.Order} failed: {e}"
            return Error $"Sprint {item.Order} ({item.Name}) failed: {e}"
}


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
        
        let prompt = verifierPreamble + Verifiers.getPrompt name + finalContext + verifierSuffix
        let! (out, sessionId) = runAgent prompt $"FinalVerify-{name}" showWin None
        let! outcome = interpretAndDisambiguate out sessionId name
        
        match outcome with
        | VPassed summary ->
            state <- { state with 
                        FinalVerifierResults = state.FinalVerifierResults.Add(name, Passed (prevCount + 1))
                        FinalVerifierSummaries = state.FinalVerifierSummaries.Add(name, summary)
                        LastVerifierLog = Some (0, name, true, summary) }
        | VFailed summary | VInconclusive summary ->
            state <- { state with 
                        FinalVerifierResults = state.FinalVerifierResults.Add(name, Failed (prevCount + 1))
                        FinalVerifierSummaries = state.FinalVerifierSummaries.Add(name, summary)
                        LastVerifierLog = Some (0, name, false, summary) }
            allPassed <- false
        
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
    
    return allPassed
}

let createFixupSprint (failedVerifiers: string list) (fixupNumber: int) (summaries: Map<string, string>) : BacklogItem =
    let failedNames = failedVerifiers |> String.concat ", "
    let nextOrder = 
        state.Backlog 
        |> List.map (fun (item, _, _) -> item.Order) 
        |> List.max 
        |> (+) 1
    let fixupPath = Path.Combine(Config.sprintsDir, $"{nextOrder:D2}_Fixup_{fixupNumber}.md")
    
    // Build the fixup sprint file content with verifier feedback
    let summarySection =
        failedVerifiers
        |> List.map (fun v ->
            let detail = summaries |> Map.tryFind v |> Option.defaultValue "(no summary available)"
            $"### {v}\n{detail}")
        |> String.concat "\n\n"
    
    let fileContent = $"""# Sprint: Fixup #{fixupNumber}

## Context
The following verifiers **FAILED** on the complete feature: {failedNames}.
Review their feedback below and make targeted fixes WITHOUT breaking existing functionality.

## Verifier Feedback
{summarySection}

## Description
Fix the issues identified by the failed verifiers above. Each verifier's feedback describes exactly what went wrong.

## Definition of Done
- All previously passing tests still pass
- Fixes address the specific issues flagged by verifiers
- No new regressions introduced
{failedVerifiers |> List.map (fun v -> $"- {v} verifier passes") |> String.concat "\n"}
"""
    
    // Write the fixup sprint file to disk so agents can read it
    File.WriteAllText(fixupPath, fileContent)
    Logging.info $"Wrote fixup sprint file to {fixupPath}"
    
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
        TargetVerifiers = Some failedVerifiers
        EliminatedVerifiers = Set.empty
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
        
        // Build fixup reason with verifier summaries for GUI visibility
        let fixupReasonText =
            failed
            |> List.map (fun v ->
                let summary = state.FinalVerifierSummaries |> Map.tryFind v |> Option.defaultValue "no details"
                $"[red]{v}[/]: {Markup.Escape(summary)}")
            |> String.concat "\n"
        
        state <- { state with 
                       CurrentPhase = "Fixup"
                       FixupReason = Some fixupReasonText }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        
        setMessage $"Fixup #{currentFixup + 1}: {failedNames}"
        
        let fixupItem = createFixupSprint failed (currentFixup + 1) state.FinalVerifierSummaries
        
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


let runWithLive (sprints: BacklogItem list) showWin (originalRequest: string) = 
    let estimatedIterations = sprints.Length * 3
    
    let overview = BacklogFile.readOverview() |> Option.defaultValue originalRequest
    
    let existingByPath = 
        state.Backlog 
        |> List.map (fun (item, status, timing) -> (item.FilePath, (item, status, timing)))
        |> Map.ofList
    
    let newPaths = sprints |> List.map (fun s -> s.FilePath) |> Set.ofList
    
    // Keep old backlog items not in the new sprints list (historical context for display)
    let oldHistorical = 
        state.Backlog 
        |> List.filter (fun (item, _, _) -> not (newPaths.Contains item.FilePath))
    
    // New items from sprints parameter, preserving Done status/timing from existing state
    let newItems = 
        sprints 
        |> List.map (fun s ->
            match existingByPath.TryFind s.FilePath with
            | Some (_, (Done _ as status), timing) -> (s, status, timing)
            | Some (_, status, timing) when state.CurrentPhase = "Arbiter" -> (s, Todo, emptyTiming)
            | Some (_, status, timing) -> (s, status, timing)
            | None -> (s, Todo, emptyTiming))
    
    let mergedBacklog = oldHistorical @ newItems
    
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
        FixupReason = None
        ArbiterAttempt = state.ArbiterAttempt
        RestartReason = state.RestartReason
    }
    
    Directory.CreateDirectory Config.ralphDir |> ignore
    SprintFiles.ensureDir()
    
    Logging.info $"Starting execution with {sprints.Length} sprints ({oldHistorical.Length} historical preserved)"
    
    let mutable result: Result<unit, string> = Ok ()
    let mutable finished = false
    
    let workTask = Task.Run(fun () ->
        try
            // Only run items from the NEW sprints list that aren't Done (skip historical items)
            let sprintsToRun = mergedBacklog |> List.filter (fun (s, status, _) -> newPaths.Contains s.FilePath && (match status with Done _ -> false | _ -> true)) |> List.map (fun (s, _, _) -> s)
            Logging.info $"Running {sprintsToRun.Length} sprints (skipping {mergedBacklog.Length - sprintsToRun.Length} already done)"
            
            // Sanity check: if we have 0 sprints to run but mergedBacklog has items, something's wrong
            if sprintsToRun.Length = 0 && mergedBacklog.Length > 0 then
                Logging.error "WARNING: All sprints marked as Done - this may indicate state corruption"
                for (item, status, _) in mergedBacklog do
                    Logging.info $"  Sprint {item.Order} ({item.Name}): {status}"
            
            if sprintsToRun.Length = 0 then
                Logging.info "No sprints to run, going directly to final checks"
            
            let r = runAllBacklogItems sprintsToRun showWin |> Async.RunSynchronously
            result <- r
            match r with
            | Ok () ->
                Logging.info "All sprints completed, running final checks"
                let passed = finalChecks showWin
                if passed then
                    Logging.info "Final checks passed"
                    state <- { state with CurrentPhase = "Complete"; Message = "[green bold]WORKFLOW COMPLETE[/]" }
                else
                    Logging.info "Final checks had issues"
                    state <- { state with CurrentPhase = "Complete"; Message = "[yellow]Completed with some issues[/]" }
            | Error e ->
                Logging.error $"Sprint execution failed: {e}"
                state <- { state with ErrorLog = Some e }
        with ex ->
            Logging.exn ex "workTask execution"
            let fullError = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
            result <- Error fullError
            state <- { state with ErrorLog = Some fullError }
        finished <- true
    )
    
    withLiveDashboard (fun () -> finished) workTask
    
    AnsiConsole.WriteLine()
    match result with
    | Ok () -> 
        AnsiConsole.Write(FigletText("COMPLETE").Color(Color.Green))
    | Error e when e.Contains("ARBITER_NEEDED") -> 
        // Don't show FAILED for arbiter threshold - it's recoverable
        AnsiConsole.Write(FigletText("ARBITER").Color(Color.Yellow))
        AnsiConsole.MarkupLine "[yellow]Sprint exceeded iteration threshold. Invoking arbiter for recovery...[/]"
    | Error e -> 
        AnsiConsole.Write(FigletText("FAILED").Color(Color.Red))
        AnsiConsole.WriteLine()
        AnsiConsole.MarkupLine "[red bold]Error details:[/]"
        // Escape markup characters in error message
        let safeError = e.Replace("[", "[[").Replace("]", "]]")
        AnsiConsole.WriteLine(safeError)
        AnsiConsole.WriteLine()
        AnsiConsole.MarkupLine $"[yellow]Full log: {Logging.logPath.Value}[/]"
    
    result

let invokeArbiter (originalRequest: string) (showWin: bool) : Result<BacklogItem list, string> =
    Logging.info "invokeArbiter called"
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
    let (arbiterResult, _) = runAgent arbiterPrompt "Arbiter" showWin None |> Async.RunSynchronously
    
    Logging.info $"Arbiter completed, output length: {arbiterResult.Length}"
    
    let newSprintsFromDisk = SprintFiles.readAllSprints()
    Logging.info $"Arbiter: found {newSprintsFromDisk.Length} sprint files on disk"
    
    let newUncompletedSprints = 
        newSprintsFromDisk 
        |> List.filter (fun s -> not (completedFilePaths.Contains s.FilePath))
    
    // Run cutter on new sprints only (completed ones already ran)
    let cutNewSprints = runCutter newUncompletedSprints showWin
    
    let completedItems = completedBacklogItems |> List.map (fun (item, _, _) -> item)
    let mergedSprints = completedItems @ cutNewSprints
    
    Logging.info $"Arbiter: {completedItems.Length} completed + {newUncompletedSprints.Length} new = {mergedSprints.Length} total sprints"
    
    if mergedSprints.Length > 0 then
        setMessage "[green]Arbiter produced recovery plan[/]"
        Logging.info "Arbiter produced valid recovery plan"
        Ok mergedSprints
    else
        Logging.error "Arbiter failed: No sprint files found"
        state <- { state with ErrorLog = Some "Arbiter failed: No sprint files found" }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        Error "Arbiter could not produce valid plan"

let rec run request showWin autoApprove arbiterCount (ciFailureContext: string option) = 
    Logging.info $"run() called: arbiterCount={arbiterCount}, autoApprove={autoApprove}"
    
    if arbiterCount >= Config.MaxArbiterAttempts then
        Logging.error $"Max arbiter attempts ({Config.MaxArbiterAttempts}) reached"
        AnsiConsole.MarkupLine $"[red]Max arbiter attempts ({Config.MaxArbiterAttempts}). Stopping.[/]"
        1
    else
        // Preserve Done items and original start time across arbiter-driven restarts
        let preservedBacklog =
            if arbiterCount > 0 then
                state.Backlog |> List.filter (fun (_, s, _) -> match s with Done _ -> true | _ -> false)
            else []
        let preservedStart = if arbiterCount > 0 && state.StartTime > DateTime.MinValue then state.StartTime else DateTime.Now
        let restartReason = if arbiterCount > 0 then state.ErrorLog |> Option.orElse (Some "Previous attempt failed") else None
        
        state <- { 
            Backlog = preservedBacklog; StartTime = preservedStart; Message = "Planning..."
            AgentStartTime = None; TotalEstimatedIterations = 0; CompletedIterations = 0
            FinalVerifierResults = Map.empty; FinalVerifierSummaries = Map.empty; CIStatus = None
            CurrentPhase = "Planning"; CurrentAgentTask = "Architect"
            LastVerifierLog = None; PlanOverview = request; ErrorLog = None
            FixupReason = None
            ArbiterAttempt = arbiterCount
            RestartReason = restartReason
        }
        
        Directory.CreateDirectory Config.ralphDir |> ignore
        SprintFiles.ensureDir()
        
        // Clear stale sprint files — keep only those backing preserved Done items
        let preservedPaths = preservedBacklog |> List.map (fun (item, _, _) -> item.FilePath) |> Set.ofList
        SprintFiles.clearSprintsExcept preservedPaths
        Logging.info $"Cleared stale sprint files (preserved {preservedPaths.Count} done sprint files)"
        
        let mutable sprintsResult: BacklogItem list = []
        let mutable planFinished = false
        let mutable architectOutput = ""
        
        // Use CI-aware architect if we have failure context
        let architectPrompt = 
            match ciFailureContext with
            | Some ciOutput -> Prompts.architectWithCIContext request ciOutput
            | None -> Prompts.architect request
        
        Logging.info "Starting Architect agent"
        
        let planTask = Task.Run(fun () ->
            try
                let (archOut, _) = runAgent architectPrompt "Architect" showWin None |> Async.RunSynchronously
                architectOutput <- archOut
                sprintsResult <- SprintFiles.readAllSprints()
                Logging.info $"Planning completed: {sprintsResult.Length} sprints found"
            with ex ->
                Logging.exn ex "planTask (Architect)"
                state <- { state with ErrorLog = Some $"{ex.GetType().Name}: {ex.Message}" }
            planFinished <- true
        )
        
        withLiveDashboard (fun () -> planFinished) planTask
        
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
            // Escape brackets so Spectre.Console doesn't interpret them as markup
            let safeOutput = truncatedOutput.Replace("[", "[[").Replace("]", "]]")
            AnsiConsole.Write(Panel(safeOutput).Header("Architect Response"))
            
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
            // Run cutter to eliminate irrelevant verifiers per sprint
            setMessage "Running verifier cutter..."
            let cutSprints = runCutter sprintsResult showWin
            sprintsResult <- cutSprints
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
                        | Error e2 -> 
                            state <- { state with ErrorLog = Some $"Arbiter recovery also failed: {e2}" }
                            run request showWin autoApprove (arbiterCount + 1) None
                    | Error arbErr ->
                        state <- { state with ErrorLog = Some $"Arbiter could not produce a valid plan" }
                        run request showWin autoApprove (arbiterCount + 1) None
            else 0


let runInteractive () = 
    AnsiConsole.Write(FigletText("RALPH").Color(Color.Yellow))
    let showWin = AnsiConsole.Confirm("Show agent windows? ", true)
    let request = AnsiConsole.Ask<string> "[green]Request:[/] "
    run request showWin false 0 None

/// Restart: Learn from previous failed run and create new plan
let runRestart (request: string) showWin autoApprove =
    AnsiConsole.Write(FigletText("RESTART").Color(Color.Yellow))
    AnsiConsole.MarkupLine "[yellow]Learning from previous run...[/]"
    
    // Gather context from previous run
    let sprintsSummary = 
        if Directory.Exists Config.sprintsDir then
            Directory.GetFiles(Config.sprintsDir, "*.md")
            |> Array.map (fun f -> 
                let name = Path.GetFileName f
                let content = File.ReadAllText f
                let lines = content.Split('\n') |> Array.truncate 20 |> String.concat "\n"
                $"=== {name} ===\n{lines}\n...")
            |> String.concat "\n\n"
        else "(no previous sprints)"
    
    let logContent = 
        let logPath = Path.Combine(Config.ralphDir, "ralph.log")
        if File.Exists logPath then File.ReadAllText logPath
        else "(no log file)"
    
    let gitDiff = Git.runProcess "git" "diff HEAD~5 --stat -p" |> Option.defaultValue "(git diff failed)"
    
    Logging.info "Restart: gathered context from previous run"
    Logging.info $"Sprints summary: {sprintsSummary.Length} chars"
    Logging.info $"Log content: {logContent.Length} chars"
    Logging.info $"Git diff: {gitDiff.Length} chars"
    
    // Call restart architect
    state <- { 
        Backlog = []; StartTime = DateTime.Now; Message = "Restart planning..."
        AgentStartTime = None; TotalEstimatedIterations = 0; CompletedIterations = 0
        FinalVerifierResults = Map.empty; FinalVerifierSummaries = Map.empty; CIStatus = None
        CurrentPhase = "Restart Planning"; CurrentAgentTask = "Architect"
        LastVerifierLog = None; PlanOverview = request; ErrorLog = None
        FixupReason = None
        ArbiterAttempt = 0; RestartReason = None
    }
    
    // Clear old sprint files deterministically (summaries already captured above)
    SprintFiles.clearSprints()
    SprintFiles.ensureDir()
    Logging.info "Cleared all sprint files for restart"
    
    let restartPrompt = Prompts.architectRestart request sprintsSummary logContent gitDiff
    let (architectOutput, _) = runAgent restartPrompt "Restart-Architect" showWin None |> Async.RunSynchronously
    
    Logging.info "Restart architect completed"
    
    // Continue with normal execution
    let rawSprints = SprintFiles.readAllSprints()
    if rawSprints.IsEmpty then
        AnsiConsole.MarkupLine "[red]Restart architect did not create any sprints[/]"
        1
    else
        let sprints = runCutter rawSprints showWin
        AnsiConsole.MarkupLine $"[green]Restart created {sprints.Length} new sprints[/]"
        let overview = BacklogFile.readOverview() |> Option.defaultValue request
        showPlan sprints overview
        if autoApprove || AnsiConsole.Confirm("Execute new plan? ", true) then
            match runWithLive sprints showWin request with
            | Ok () -> 0
            | Error e -> 
                AnsiConsole.MarkupLine $"[red]Restart failed: {e}[/]"
                1
        else 0

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
    printfn "Usage:  dotnet fsi Ralph.fsx [request] [--yes] [--hidden] [--push] [--restart] [--help]"
    printfn ""
    printfn "Options:"
    printfn "  --yes       Auto-approve all prompts"
    printfn "  --hidden    Hide agent windows"
    printfn "  --push      Push after completion and monitor CI, fix failures"
    printfn "  --restart   Learn from previous failed run and restart with new plan"
| args ->
    let request = args |> List.filter (fun a -> not (a.StartsWith "--")) |> String.concat " "
    let showWin = not (List.contains "--hidden" args)
    let auto = List.contains "--yes" args
    let push = List.contains "--push" args
    let restart = List.contains "--restart" args
    if String.IsNullOrWhiteSpace request then printfn "No request.  Use --help"; exit 1
    if restart then runRestart request showWin auto |> exit
    elif push then runWithPush request showWin auto 0 |> exit
    else run request showWin auto 0 None |> exit
