// GUI.fsx - Terminal dashboard and state management
// Loaded by Ralph.fsx after Prompting.fsx
// IMPORTANT: No #load here - Ralph.fsx loads all dependencies

#r "nuget: Spectre.Console"

open System
open System.IO
open System.Text.RegularExpressions
open Spectre.Console
open Spectre.Console.Rendering
open Utils
open TypeDefinitions
open Prompting

/// Terminal GUI helpers and display functions
module GUI =
    let escapeMarkup (s: string) = Markup.Escape(s)
    
    let phaseName = function Implement -> "Implement"
    
    /// Format sprint name from filename for display (underscores to spaces, remove prefix)
    let sprintDisplayName (filePath: string) =
        let fileName = Path.GetFileNameWithoutExtension(filePath)
        let withoutPrefix = Regex.Replace(fileName, @"^\d+_", "")
        withoutPrefix.Replace("_", " ")
    
    /// Build the main dashboard widget
    let buildDashboard (state: State) (verifierNames: string list) =
        let elapsed = DateTime.Now - state.StartTime
        let elapsedStr = elapsed.ToString("hh\\:mm\\:ss")
        
        // Calculate progress
        let totalItems = state.Backlog.Length
        let completedItems = state.Backlog |> List.filter (fun (_, s, _) -> match s with Done _ -> true | _ -> false) |> List.length
        let currentItem = state.Backlog |> List.tryFind (fun (_, s, _) -> match s with Running _ -> true | _ -> false)
        let progress = 
            if state.TotalEstimatedIterations > 0 then
                float state.CompletedIterations / float state.TotalEstimatedIterations * 100.0
            else
                float completedItems / float (max 1 totalItems) * 100.0
        
        // Compact header with phase and progress
        let headerLine = 
            let phaseColor = 
                match state.CurrentPhase with
                | "Complete" -> "green"
                | "Planning" -> "cyan"
                | _ -> "yellow"
            let pct = sprintf "%.0f" progress
            let barWidth = 30
            let filled = min barWidth (max 0 (int (float barWidth * progress / 100.0)))
            let empty = max 0 (barWidth - filled)
            let barStr = String.replicate filled "█" + String.replicate empty "░"
            Markup($"[yellow bold]RALPH[/] │ [{phaseColor}]{escapeMarkup state.CurrentPhase}[/] │ {barStr} {pct}%% │ {completedItems}/{totalItems} sprints │ {elapsedStr}")
        
        // Current activity - single line showing what agent is doing
        let activityLine =
            match state.AgentStartTime with
            | Some startTime ->
                let agentElapsed = DateTime.Now - startTime
                let agentElapsedStr = agentElapsed.ToString("mm\\:ss")
                let task = if String.IsNullOrEmpty state.CurrentAgentTask then "Working..." else state.CurrentAgentTask
                Markup($"[green]▶ AGENT[/] ({agentElapsedStr}): {escapeMarkup task}")
            | None ->
                Markup($"[dim]Agent idle[/]")
        
        // Build Sprint Board table
        let t = Table().Border(TableBorder.Rounded).Expand()
        t.AddColumn(TableColumn("#").Width(3)) |> ignore
        t.AddColumn(TableColumn("Sprint").Width(25)) |> ignore
        t.AddColumn(TableColumn("Status").Width(12)) |> ignore
        t.AddColumn(TableColumn("Iter").Width(5)) |> ignore
        t.AddColumn(TableColumn("Time").Width(7)) |> ignore
        for name in verifierNames do
            let shortName = if name.Length > 8 then name.Substring(0, 7) + "…" else name
            let filePath = Verifiers.getFilePath name
            let header = $"[link=file://{filePath}]{shortName}[/]"
            t.AddColumn(TableColumn(Markup(header)).Width(8)) |> ignore
        
        for (item, status, timing) in state.Backlog do
            let now = DateTime.Now
            let displayName = sprintDisplayName item.FilePath
            let shortDisplayName = if displayName.Length > 24 then displayName.Substring(0, 21) + "..." else displayName
            let clickableName = $"[link=file://{item.FilePath}]{escapeMarkup shortDisplayName}[/]"
            let statusStr, iterStr = 
                match status with
                | Todo -> "[dim]Todo[/]", "[dim]-[/]"
                | Running (_, iter) -> 
                    $"[yellow]⏳ Run[/]", if iter > 1 then $"[yellow]⟲{iter}[/]" else $"[yellow]{iter}[/]"
                | Done iters -> "[green]✓ Done[/]", $"[green]{iters}[/]"
            let timeStr = 
                match status, timing.EndTime with
                | Done _, Some endT -> 
                    let mins = (endT - timing.StartTime).TotalMinutes
                    if mins >= 60.0 then $"[green]{mins / 60.0:F1}h[/]" else $"[green]{int mins}m[/]"
                | Running _, _ -> 
                    let mins = (now - timing.StartTime).TotalMinutes
                    if mins >= 60.0 then $"[yellow]{mins / 60.0:F1}h[/]" else $"[yellow]{int mins}m[/]"
                | _ -> "[dim]-[/]"
            
            let verifierIcon name =
                match timing.VerifierResults.TryFind name with
                | Some (Passed iters) -> if iters > 1 then $"[green]✓{iters}[/]" else "[green]✓[/]"
                | Some (Failed iters) -> if iters > 1 then $"[red]✗{iters}[/]" else "[red]✗[/]"
                | Some NotStarted | None -> "[dim]○[/]"
            
            let verifierCells = verifierNames |> List.map verifierIcon
            let allCells = [string item.Order; clickableName; statusStr; iterStr; timeStr] @ verifierCells
            t.AddRow(allCells |> Array.ofList) |> ignore
        
        // Current task detail panel
        let currentTaskPanel : IRenderable =
            match currentItem with
            | Some (item, Running (phase, iter), timing) ->
                let dodItems = 
                    if timing.LastDoDResults.Length > 0 then
                        timing.LastDoDResults |> List.map (fun r ->
                            let icon = match r.Passed with Some true -> "[green]✓[/]" | Some false -> "[red]✗[/]" | None -> "[dim]○[/]"
                            let criterion = if r.Criterion.Length > 60 then r.Criterion.Substring(0, 57) + "..." else r.Criterion
                            $"{icon} {escapeMarkup criterion}")
                    else
                        item.DoD |> List.map (fun c -> 
                            let criterion = if c.Length > 60 then c.Substring(0, 57) + "..." else c
                            $"[dim]○[/] {escapeMarkup criterion}")
                let dodSection = dodItems |> String.concat "\n"
                let lastFailure = 
                    match timing.IterationReasons |> List.tryLast with
                    | Some (i, r) -> 
                        let reason = if r.Length > 80 then r.Substring(0, 77) + "..." else r
                        $"\n[red]Last issue (iter {i}):[/] {escapeMarkup reason}"
                    | None -> ""
                Panel(Markup($"[bold]{escapeMarkup item.Name}[/]\n{dodSection}{lastFailure}"))
                    .Header($"[cyan]Current: Sprint {item.Order} (iter {iter})[/]")
                    .Expand()
                :> IRenderable
            | _ when not (String.IsNullOrEmpty state.PlanOverview) && state.CurrentPhase = "Planning" ->
                Panel(Markup($"[dim]{escapeMarkup state.PlanOverview}[/]")).Header("[cyan]Planning...[/]").Expand() :> IRenderable
            | _ -> 
                Text("") :> IRenderable
        
        // Last verifier result
        let lastVerifierLine : IRenderable =
            match state.LastVerifierLog with
            | Some (name, passed, summary) ->
                let icon = if passed then "[green]✓[/]" else "[red]✗[/]"
                let summaryText = if summary.Length > 70 then summary.Substring(0, 67) + "..." else summary
                Markup($"{icon} [bold]{escapeMarkup name}[/]: {escapeMarkup summaryText}") :> IRenderable
            | None -> Text("") :> IRenderable
        
        // Final verification table
        let finalVerificationPanel : IRenderable =
            if state.FinalVerifierResults.IsEmpty then
                Text("") :> IRenderable
            else
                let ft = Table().Border(TableBorder.Simple).Expand()
                ft.AddColumn(TableColumn("Final Verifier").Width(15)) |> ignore
                ft.AddColumn(TableColumn("").Width(8)) |> ignore
                ft.AddColumn(TableColumn("Summary").NoWrap()) |> ignore
                for name in verifierNames do
                    let status = 
                        match state.FinalVerifierResults.TryFind name with
                        | Some (Passed i) -> if i > 1 then $"[green]✓({i})[/]" else "[green]✓ Pass[/]"
                        | Some (Failed i) -> if i > 1 then $"[red]✗({i})[/]" else "[red]✗ Fail[/]"
                        | Some NotStarted | None -> "[dim]○[/]"
                    let summary = 
                        state.FinalVerifierSummaries.TryFind name 
                        |> Option.defaultValue "" 
                        |> fun s -> if s.Length > 50 then s.Substring(0, 47) + "..." else s
                        |> escapeMarkup
                    ft.AddRow([| Markup(escapeMarkup name) :> IRenderable; Markup(status) :> IRenderable; Markup(summary) :> IRenderable |]) |> ignore
                ft :> IRenderable
        
        // Error display
        let errorLine : IRenderable =
            match state.ErrorLog with
            | Some err -> 
                let errText = if err.Length > 100 then err.Substring(0, 97) + "..." else err
                Markup($"[red bold]Error:[/] {escapeMarkup errText}") :> IRenderable
            | None -> Text("") :> IRenderable
        
        // Message line
        let messageLine : IRenderable =
            if String.IsNullOrEmpty state.Message then Text("") :> IRenderable
            else Markup(state.Message) :> IRenderable
        
        // Build compact single-screen layout
        let rows = [
            yield Rule("").RuleStyle("dim") :> IRenderable
            yield headerLine :> IRenderable
            yield activityLine :> IRenderable
            yield Rule("").RuleStyle("dim") :> IRenderable
            if state.Backlog.Length > 0 then
                yield t :> IRenderable
            yield currentTaskPanel
            yield lastVerifierLine
            if not state.FinalVerifierResults.IsEmpty then
                yield Rule("[cyan]Final Verification[/]").RuleStyle("cyan") :> IRenderable
                yield finalVerificationPanel
            yield errorLine
            yield messageLine
        ]
        
        Rows(rows)

/// State update operations (functions that modify state)
module StateOps =
    /// Update status of a sprint
    let updateStatus (state: byref<State>) (liveCtx: LiveDisplayContext option) sprintPath status msg =
        let newBacklog = state.Backlog |> List.map (fun (s, st, timing) -> 
            if s.FilePath = sprintPath then (s, status, timing) else (s, st, timing))
        state <- { state with Backlog = newBacklog; Message = msg }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

    /// Update timing of a sprint
    let updateTiming (state: byref<State>) sprintPath (f: BacklogItemTiming -> BacklogItemTiming) =
        let newBacklog = state.Backlog |> List.map (fun (s, st, timing) -> 
            if s.FilePath = sprintPath then (s, st, f timing) else (s, st, timing))
        state <- { state with Backlog = newBacklog }

    /// Add iteration reason for debugging
    let addIterationReason (state: byref<State>) sprintPath iter reason =
        updateTiming &state sprintPath (fun t -> { t with IterationReasons = t.IterationReasons @ [(iter, reason)] })

    /// Add iteration record for history
    let addIterationRecord (state: byref<State>) sprintPath (record: IterationRecord) =
        updateTiming &state sprintPath (fun t -> { t with IterationHistory = t.IterationHistory @ [record] })

    /// Update DoD results
    let updateDoDResults (state: byref<State>) sprintPath (results: DoDResult list) =
        updateTiming &state sprintPath (fun t -> { t with LastDoDResults = results })

    /// Start timing for an item
    let startItemTiming (state: byref<State>) sprintPath =
        updateTiming &state sprintPath (fun t -> { t with StartTime = DateTime.Now })

    /// End timing for an item
    let endItemTiming (state: byref<State>) (liveCtx: LiveDisplayContext option) sprintPath summary =
        updateTiming &state sprintPath (fun t -> { t with EndTime = Some DateTime.Now; Summary = Some summary })
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

    /// Get timing for an item
    let getItemTiming (state: State) sprintPath =
        state.Backlog |> List.tryFind (fun (s, _, _) -> s.FilePath = sprintPath) |> Option.map (fun (_, _, t) -> t)

    /// Set status message
    let setMessage (state: byref<State>) (liveCtx: LiveDisplayContext option) msg =
        state <- { state with Message = msg }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
