#r "nuget: Spectre.Console"

open System
open System.IO
open System.Text.RegularExpressions
open Spectre.Console
open Spectre.Console.Rendering
open Utils
open TypeDefinitions
open Prompting

module MarkupBuilder =
    let escape (raw: RawText) : SafeMarkup = RawText.escapeForMarkup raw
    
    let truncateRaw maxLen (suffix: string) (RawText s) : RawText =
        if s.Length > maxLen then RawText (s.[..maxLen - suffix.Length - 1] + suffix) else RawText s
    
    let concat (parts: SafeMarkup list) = parts |> List.map (fun (SafeMarkup s) -> s) |> String.concat "" |> SafeMarkup
    
    let literal s = SafeMarkup s
    
    let statusIcon = function
        | Passed 1 -> SafeMarkup "[green]✓[/]" 
        | Passed n -> SafeMarkup $"[green]✓{n}[/]"
        | Failed 1 -> SafeMarkup "[red]✗[/]" 
        | Failed n -> SafeMarkup $"[red]✗{n}[/]"
        | NotStarted -> SafeMarkup "[dim]○[/]"
    
    let dodIcon = function
        | Some true -> SafeMarkup "[green]✓[/]"
        | Some false -> SafeMarkup "[red]✗[/]"
        | None -> SafeMarkup "[dim]○[/]"
    
    let fileLink filePath displayName : SafeMarkup =
        SafeMarkup $"[link=file://{Markup.Escape filePath}]{Markup.Escape displayName}[/]"
    
    /// Build verifier log line with sprint context - all parts properly escaped
    let verifierLogLine (sprintOrder: int) (verifierName: string) (passed: bool) (summary: string) : SafeMarkup =
        let icon = if passed then literal "[green]✓[/]" else literal "[red]✗[/]"
        let sprintLabel = if sprintOrder = 0 then "Final" else $"S{sprintOrder}"
        let escapedName = escape (RawText verifierName)
        let escapedSummary = summary |> RawText |> truncateRaw 60 "..." |> escape
        concat [icon; literal $" [dim]({sprintLabel})[/] [bold]"; escapedName; literal "[/]: "; escapedSummary]
    
    let toString (SafeMarkup s) = s

module GUI =
    open MarkupBuilder
    
    let escapeMarkup (s: string) = Markup.Escape(s)
    
    let sprintDisplayName (filePath: string) = 
        Regex.Replace(Path.GetFileNameWithoutExtension(filePath), @"^\d+_", "").Replace("_", " ")
    
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
            let header = fileLink filePath shortName |> toString
            t.AddColumn(TableColumn(Markup(header)).Width(8)) |> ignore
        
        for (item, status, timing) in state.Backlog do
            let now = DateTime.Now
            let displayName = sprintDisplayName item.FilePath
            let shortDisplayName = if displayName.Length > 24 then displayName.Substring(0, 21) + "..." else displayName
            let clickableName = fileLink item.FilePath shortDisplayName |> toString
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
                let status = timing.VerifierResults.TryFind name |> Option.defaultValue NotStarted
                statusIcon status |> toString
            
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
                            let icon = dodIcon r.Passed |> toString
                            let criterion = RawText r.Criterion |> truncateRaw 60 "..." |> RawText.value
                            $"{icon} {escapeMarkup criterion}")
                    else
                        item.DoD |> List.map (fun c -> 
                            let criterion = RawText c |> truncateRaw 60 "..." |> RawText.value
                            $"{dodIcon None |> toString} {escapeMarkup criterion}")
                let dodSection = dodItems |> String.concat "\n"
                let lastFailure = 
                    match timing.IterationReasons |> List.tryLast with
                    | Some (i, r) -> 
                        let reason = RawText r |> truncateRaw 80 "..." |> escape |> toString
                        $"\n[red]Last issue (iter {i}):[/] {reason}"
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
            | Some (sprintOrder, name, passed, summary) ->
                Markup(verifierLogLine sprintOrder name passed summary |> toString) :> IRenderable
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
                    let verifierStatus = state.FinalVerifierResults.TryFind name |> Option.defaultValue NotStarted
                    let statusStr = statusIcon verifierStatus |> toString
                    let summary = 
                        state.FinalVerifierSummaries.TryFind name 
                        |> Option.defaultValue "" 
                        |> RawText
                        |> truncateRaw 50 "..."
                        |> escape
                        |> toString
                    ft.AddRow([| Markup(escapeMarkup name) :> IRenderable; Markup(statusStr) :> IRenderable; Markup(summary) :> IRenderable |]) |> ignore
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

module StateTrans =
    let withStatus sprintPath status msg (state: State) : State =
        let newBacklog = state.Backlog |> List.map (fun (s, st, timing) -> 
            if s.FilePath = sprintPath then (s, status, timing) else (s, st, timing))
        { state with Backlog = newBacklog; Message = msg }

    /// Apply timing transformation to a sprint
    let withTiming sprintPath (f: BacklogItemTiming -> BacklogItemTiming) (state: State) : State =
        let newBacklog = state.Backlog |> List.map (fun (s, st, timing) -> 
            if s.FilePath = sprintPath then (s, st, f timing) else (s, st, timing))
        { state with Backlog = newBacklog }

    let withIterationReason sprintPath iter reason state =
        withTiming sprintPath (fun t -> { t with IterationReasons = t.IterationReasons @ [(iter, reason)] }) state

    let withIterationRecord sprintPath (record: IterationRecord) state =
        withTiming sprintPath (fun t -> { t with IterationHistory = t.IterationHistory @ [record] }) state

    let withVerifierResult sprintPath verifierName passed summary state =
        withTiming sprintPath (fun t -> 
            match t.IterationHistory with
            | [] -> t
            | history ->
                let lastIdx = history.Length - 1
                let lastRecord = history.[lastIdx]
                let updatedRecord = { lastRecord with VerifierResults = lastRecord.VerifierResults @ [(verifierName, passed, summary)] }
                { t with IterationHistory = history.[..lastIdx-1] @ [updatedRecord] }
        ) state

    let withDoDResults sprintPath results state =
        withTiming sprintPath (fun t -> { t with LastDoDResults = results }) state

    let withTimingStarted sprintPath state =
        withTiming sprintPath (fun t -> { t with StartTime = DateTime.Now }) state

    let withTimingEnded sprintPath summary state =
        withTiming sprintPath (fun t -> { t with EndTime = Some DateTime.Now; Summary = Some summary }) state

    let withMessage msg (state: State) : State =
        { state with Message = msg }

    let getItemTiming sprintPath (state: State) : BacklogItemTiming option =
        state.Backlog |> List.tryFind (fun (s, _, _) -> s.FilePath = sprintPath) |> Option.map (fun (_, _, t) -> t)

module StateOps =
    let updateStatus (state: byref<State>) (liveCtx: LiveDisplayContext option) sprintPath status msg =
        state <- StateTrans.withStatus sprintPath status msg state
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

    let updateTiming (state: byref<State>) sprintPath f =
        state <- StateTrans.withTiming sprintPath f state

    let addIterationReason (state: byref<State>) sprintPath iter reason =
        state <- StateTrans.withIterationReason sprintPath iter reason state

    let addIterationRecord (state: byref<State>) sprintPath record =
        state <- StateTrans.withIterationRecord sprintPath record state

    let addVerifierResultToLastIteration (state: byref<State>) sprintPath verifierName passed summary =
        state <- StateTrans.withVerifierResult sprintPath verifierName passed summary state

    let updateDoDResults (state: byref<State>) sprintPath results =
        state <- StateTrans.withDoDResults sprintPath results state

    let startItemTiming (state: byref<State>) sprintPath =
        state <- StateTrans.withTimingStarted sprintPath state

    let endItemTiming (state: byref<State>) (liveCtx: LiveDisplayContext option) sprintPath summary =
        state <- StateTrans.withTimingEnded sprintPath summary state
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

    let getItemTiming (state: State) (sprintPath: string) =
        StateTrans.getItemTiming sprintPath state

    let setMessage (state: byref<State>) (liveCtx: LiveDisplayContext option) msg =
        state <- StateTrans.withMessage msg state
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
