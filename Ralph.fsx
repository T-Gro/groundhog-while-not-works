#!/usr/bin/env dotnet fsi

#r "nuget: Fli"
#r "nuget: Spectre.Console"

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open Fli
open Spectre.Console
open Spectre.Console.Rendering

module Config =
    let Model = "claude-opus-4.6"
    let MaxIterations = 50
    // Use script location for verifiers, working directory for ralph output
    let scriptDir = __SOURCE_DIRECTORY__
    let ralphDir = Path.Combine(Directory.GetCurrentDirectory(), ".tools", "ralph")
    let backlogFile = Path.Combine(ralphDir, "BACKLOG.md")  // Single unified backlog file
    let verifiersDir = Path.Combine(scriptDir, "verifiers")  // Verifier prompts as .md files (relative to script)

module XmlHelpers =
    // Token-efficient XML helpers
    // - Attributes for fixed/known-size data
    // - Direct text content for free-form data (no wrapper elements)
    // - Element names match domain concepts (e.g. <Functional> not <Verifier step="Functional">)
    /// Core XML element builder: xe "name" [attrs] [children] "innerText"
    /// All parameters optional via shortcuts below
    let xe (name: string) (attrs: (string * string) list) (children: XElement list) (text: string) : XElement =
        let el = XElement(XName.Get name)
        for (k, v) in attrs do el.Add(XAttribute(XName.Get k, v))
        for c in children do el.Add(c)
        if not (String.IsNullOrEmpty text) then el.Add(text)
        el

    // Shortcuts - any subset of (attrs, children, text)
    let x   name                        = xe name [] [] ""          // <name/>

    let xt  name text                   = xe name [] [] text        // <name>text</name>
    let xc  name children               = xe name [] children ""    // <name><child/></name>
    let xat name attrs text             = xe name attrs [] text     // <name attr="val">text</name>
    let xac name attrs children         = xe name attrs children "" // <name attr="val"><child/></name>

    // xe is the full form: xe name attrs children text

    // Signal detection: match signal NOT inside quotes (avoids false positives from LLM quoting signals)
    let hasSignal (signal: string) (text: string) =
        let pattern = sprintf @"(?<![""'`])%s(?![""'`])" (Regex.Escape signal)
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)

open Config
open XmlHelpers

module TypeDefinitions =
    // DoD = Definition of Done - each item is a technically executable criterion
    type DoDResult = { Criterion: string; Passed: bool option }  // None = not yet evaluated
    type BacklogItem = { 
        Id: int
        Name: string           // Short name for table display
        Description: string    // Robust description from planner
        DoD: string list       // Definition of Done - list of executable criteria
    }
    type Plan = { Overview: string; Subtasks: BacklogItem list }
    // Phase is just Implement - verifiers handle all validation
    type Phase = Implement

    // Verifier names are now strings discovered from verifiers/*.md files
    // No hardcoded enum - fully file-driven
    type VerifierName = string
    // Track status AND iteration count for each verifier
    type VerifierStatus = NotStarted | Passed of iterations: int | Failed of iterations: int

    // Track full history of an iteration for retry context
    type IterationRecord = {
        Iteration: int
        AgentOutput: string
        VerifierResults: (VerifierName * bool * string) list
    }

    type BacklogItemTiming = {
        StartTime: DateTime
        EndTime: DateTime option
        IterationReasons: (int * string) list  // (iteration, reason for retry)
        Summary: string option
        LastDoDResults: DoDResult list         // DoD results from last iteration
        VerifierResults: Map<VerifierName, VerifierStatus>  // Track each verifier
        IterationHistory: IterationRecord list  // Full history for retry context
    }

    type BacklogStatus = 
        | Todo
        | Running of phase: Phase * iteration: int
        | Done of iterations: int

    type State = {
        Backlog: (BacklogItem * BacklogStatus * BacklogItemTiming) list
        StartTime: DateTime
        Message: string
        AgentStartTime: DateTime option  // When agent started (None = idle)
        TotalEstimatedIterations: int  // For progress calculation
        CompletedIterations: int
        FinalVerifierResults: Map<VerifierName, VerifierStatus>  // Final verification after all sprints
        FinalVerifierSummaries: Map<VerifierName, string>  // Management summaries from final verifiers
        CIStatus: (string * bool option) option  // (PR URL, passed: None=pending, Some true=passed, Some false=failed)
    }

    let emptyTiming = { 
        StartTime = DateTime.MinValue; EndTime = None; IterationReasons = []; Summary = None; LastDoDResults = []
        VerifierResults = Map.empty; IterationHistory = []
    }

open TypeDefinitions


let mutable state: State = { 
        Backlog = []; StartTime = DateTime.Now; Message = ""; AgentStartTime = None
        TotalEstimatedIterations = 0; CompletedIterations = 0
        FinalVerifierResults = Map.empty; FinalVerifierSummaries = Map.empty; CIStatus = None
    }
let mutable liveCtx: LiveDisplayContext option = None

// Verifier file system - verifiers are discovered from verifiers/*.md files
// File name (without extension) = verifier name
// First line = basic role description (for other agents to know what this verifier does)
// Rest = full prompt for the verifier
// NO CACHING - always read fresh from disk for live editing
// Ordering: files with numeric prefix (e.g., "01-FUNCTIONAL.md") are sorted by prefix
//           the prefix is stripped from the display name
module Verifiers =
    /// Parse filename to extract order and display name
    /// "01-FUNCTIONAL" -> (1, "FUNCTIONAL"), "PERF" -> (999, "PERF")
    let private parseFileName (name: string) : int * string =
        let m = Regex.Match(name, @"^(\d+)-(.+)$")
        if m.Success then
            (int m.Groups.[1].Value, m.Groups.[2].Value)
        else
            (999, name)  // No prefix = sort last among prefixed, but maintain relative order
    
    /// List all available verifier names from the verifiers directory, ordered by prefix
    /// Returns display names (without numeric prefix)
    let listAll () : VerifierName list =
        if Directory.Exists(Config.verifiersDir) then
            Directory.GetFiles(Config.verifiersDir, "*.md")
            |> Array.map Path.GetFileNameWithoutExtension
            |> Array.map (fun n -> (n, parseFileName n))
            |> Array.sortBy (fun (_, (order, _)) -> order)
            |> Array.map (fun (_, (_, displayName)) -> displayName)
            |> Array.toList
        else []
    
    /// Get the raw filename (with prefix) for a display name
    let private getFileName (displayName: string) : string option =
        if Directory.Exists(Config.verifiersDir) then
            Directory.GetFiles(Config.verifiersDir, "*.md")
            |> Array.map Path.GetFileNameWithoutExtension
            |> Array.tryFind (fun n ->
                let (_, name) = parseFileName n
                name = displayName)
        else None
    
    /// Read a verifier file and return (roleDescription, fullPrompt)
    /// Always reads from disk - no caching for live editing
    let private readFile (displayName: string) : (string * string) option =
        match getFileName displayName with
        | Some fileName ->
            let filePath = Path.Combine(Config.verifiersDir, fileName + ".md")
            if File.Exists(filePath) then
                let content = File.ReadAllText(filePath)
                let lines = content.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
                if lines.Length > 0 then
                    Some (lines.[0].Trim(), content)
                else None
            else None
        | None -> None
    
    /// Get the prompt for a verifier (reads from file each time)
    let getPrompt (name: VerifierName) : string =
        match readFile name with
        | Some (_, prompt) -> prompt
        | None -> $"Verifier {name}: No file found at verifiers/{name}.md. Please create one."
    
    /// Get the role description for a verifier (first line of file)
    let getRoleDescription (name: VerifierName) : string =
        match readFile name with
        | Some (role, _) -> role
        | None -> $"Verifier for {name}"
    


// XML prompt builder for subagents
// Token-efficient format: attributes for fixed data, text for free-form
module XmlPrompt =
    type Role = Implementor
    
    type IterationHistory = {
        Iteration: int
        AgentOutput: string
        VerifierResults: (VerifierName * bool * string) list  // verifier name, passed, feedback
    }
    
    type SprintHistory = {
        SprintId: int
        SprintName: string
        Summary: string
    }
    
    type StepHistory = {
        Iteration: int
        Passed: bool
        Summary: string
    }
    
    let private roleElement (role: Role) =
        match role with
        | Implementor ->
            xt "Implementor" "YOU ARE AN IMPLEMENTOR for F# compiler. Write code fulfilling sprint requirements. Follow DoD. Minimize breaking changes. Reuse existing helpers. Minimize allocations. Build and tests MUST pass."
    
    let private dodElement (dod: DoDResult list) =
        let criteria = dod |> List.map (fun d ->
            let elName = match d.Passed with Some true -> "pass" | Some false -> "fail" | None -> "todo"
            xt elName d.Criterion)
        xc "dod" criteria
    
    let private pastSprintsElement (sprints: SprintHistory list) =
        if sprints.IsEmpty then x "first_sprint"
        else
            let items = sprints |> List.map (fun s ->
                xat "s" [("id", string s.SprintId)] (s.SprintName + ": " + s.Summary))
            xc "done" items
    
    let private pastStepsElement (steps: StepHistory list) =
        if steps.IsEmpty then x "first_step"
        else
            let items = steps |> List.map (fun s ->
                let suffix = if s.Passed then "" else " [FAILED]"
                xat "impl" [("i", string s.Iteration)] (s.Summary + suffix))
            xc "steps" items
    
    let private iterationHistoryElement (history: IterationHistory list) =
        if history.IsEmpty then None
        else
            let items = 
                history |> List.collect (fun h ->
                    let verifierPart = 
                        h.VerifierResults |> List.map (fun (verifierName, passed, feedback) ->
                            // Use verifier name directly as element name (lowercase for XML)
                            let elName = verifierName.ToLowerInvariant().Replace("-", "_")
                            let suffix = if passed then "" else " [FAILED]"
                            xt elName (feedback + suffix))
                    let exchanges = [xt "out" h.AgentOutput] @ verifierPart
                    [xac ("i" + string h.Iteration) [] exchanges]
                )
            Some (xc "history" items)
    
/// Build full XML prompt for an implementor
    let build 
        (role: Role) 
        (currentSprint: BacklogItem)
        (iteration: int)
        (dod: DoDResult list)
        (pastSprints: SprintHistory list)
        (pastSteps: StepHistory list)
        (iterationHistory: IterationHistory list) =
        
        xc "R" ([
            xt "backlog" $".tools/ralph/BACKLOG.md - YOUR TASK: ### Task {currentSprint.Id} - {currentSprint.Name}"
            xt "scope" "ONLY implement THIS task. Other tasks are NOT your concern."
            roleElement role
            // Sprint: impl as element name, attrs for id/iteration, text = "Name: Description"
            xat "impl" [("id", string currentSprint.Id); ("i", string iteration)] 
                (currentSprint.Name + ": " + currentSprint.Description)
            dodElement dod
            pastSprintsElement pastSprints
            pastStepsElement pastSteps
        ] @ 
        (match iterationHistoryElement iterationHistory with Some el -> [el] | None -> []))
    
    let toPrompt (el: XElement) = el.ToString()

// Unified BACKLOG.md file - single source of truth
module BacklogFile =
    type BacklogData = {
        Overview: string  // Original request + approach
        Tasks: (BacklogItem * BacklogStatus * Map<VerifierName, VerifierStatus> * string option) list  // Last item is summary when done
    }
    
    // Discover verifier names from files at runtime
    let private getVerifierNames () = Verifiers.listAll ()
    let private statusToIcon = function Passed _ -> "✅" | Failed _ -> "❌" | NotStarted -> "○"
    let private iconToStatus = function "✅" -> Passed 1 | "❌" -> Failed 1 | _ -> NotStarted
    
    let write (data: BacklogData) =
        let verifierNames = getVerifierNames ()
        let sb = System.Text.StringBuilder()
        sb.AppendLine("# BACKLOG") |> ignore
        sb.AppendLine("") |> ignore
        sb.AppendLine("## Overview") |> ignore
        sb.AppendLine(data.Overview) |> ignore
        sb.AppendLine("") |> ignore
        sb.AppendLine("## Tasks") |> ignore
        sb.AppendLine("") |> ignore
        
        // Table header - dynamically built from discovered verifiers
        let stageHeaders = verifierNames |> String.concat " | "
        let separators = verifierNames |> List.map (fun _ -> "---") |> String.concat " | "
        sb.AppendLine($"| ID | Task | Status | {stageHeaders} |") |> ignore
        sb.AppendLine($"| --- | --- | --- | {separators} |") |> ignore
        
        for (item, status, verifiers, _) in data.Tasks do
            let statusStr = match status with Todo -> "○ Todo" | Running (p, i) -> $"⏳ {p} ({i})" | Done i -> $"✅ Done ({i})"
            let verifierCells = verifierNames |> List.map (fun name -> 
                match verifiers.TryFind name with Some v -> statusToIcon v | None -> "○") |> String.concat " | "
            sb.AppendLine($"| {item.Id} | {item.Name} | {statusStr} | {verifierCells} |") |> ignore
        
        sb.AppendLine("") |> ignore
        sb.AppendLine("## Task Details") |> ignore
        sb.AppendLine("") |> ignore
        
        for (item, status, _, summary) in data.Tasks do
            sb.AppendLine($"### Task {item.Id} - {item.Name}") |> ignore
            sb.AppendLine("") |> ignore
            sb.AppendLine(item.Description) |> ignore
            sb.AppendLine("") |> ignore
            sb.AppendLine("**Definition of Done:**") |> ignore
            for dod in item.DoD do
                sb.AppendLine($"- {dod}") |> ignore
            match status, summary with
            | Done _, Some s -> 
                sb.AppendLine("") |> ignore
                sb.AppendLine($"**Completed:** {s}") |> ignore
            | _ -> ()
            sb.AppendLine("") |> ignore
        
        Directory.CreateDirectory(ralphDir) |> ignore
        // Create .bak backup before writing (safety net for fragile parsing logic)
        if File.Exists(backlogFile) then
            File.Copy(backlogFile, backlogFile + ".bak", overwrite = true)
        File.WriteAllText(backlogFile, sb.ToString())
    
    let read () : BacklogData option =
        if not (File.Exists backlogFile) then None
        else
            try
                let content = File.ReadAllText(backlogFile)
                let lines = content.Split('\n') |> Array.toList
                
                let getSection header =
                    lines 
                    |> List.skipWhile (fun l -> not (l.StartsWith($"## {header}")))
                    |> List.skip 1
                    |> List.takeWhile (fun l -> not (l.StartsWith("## ")))
                    |> String.concat "\n"
                    |> fun s -> s.Trim()
                
                let overview = getSection "Overview"
                
                // Parse task table
                let tableLines = 
                    lines 
                    |> List.skipWhile (fun l -> not (l.StartsWith("| ID")))
                    |> List.skip 2  // Skip header and separator
                    |> List.takeWhile (fun l -> l.StartsWith("|"))
                
                // Parse task details for full info
                let parseTaskDetail id =
                    let detailLines = 
                        lines 
                        |> List.skipWhile (fun l -> not (l.StartsWith($"### Task {id} -")))
                    if detailLines.IsEmpty then None
                    else
                        let headerLine = detailLines.Head
                        let name = headerLine.Substring(headerLine.IndexOf(" - ") + 3).Trim()
                        let contentLines = detailLines |> List.skip 1 |> List.takeWhile (fun l -> not (l.StartsWith("### ")))
                        let descLines = contentLines |> List.takeWhile (fun l -> not (l.StartsWith("**Definition")))
                        let dodLines = contentLines |> List.skipWhile (fun l -> not (l.StartsWith("- "))) |> List.filter (fun l -> l.StartsWith("- "))
                        let summaryLine = contentLines |> List.tryFind (fun l -> l.StartsWith("**Completed:**"))
                        let summary = summaryLine |> Option.map (fun l -> l.Substring(14).Trim())
                        Some ({ 
                            Id = id
                            Name = name
                            Description = descLines |> String.concat "\n" |> fun s -> s.Trim()
                            DoD = dodLines |> List.map (fun l -> l.Substring(2).Trim())
                        }, summary)
                
                let tasks = 
                    tableLines |> List.choose (fun line ->
                        let cells = line.Split('|') |> Array.map (fun c -> c.Trim()) |> Array.filter (fun c -> c <> "")
                        if cells.Length < 3 then None
                        else
                            try
                                let id = int cells.[0]
                                let statusCell = cells.[2]
                                let status = 
                                    if statusCell.Contains("Done") then Done 1
                                    elif statusCell.Contains("⏳") then Running (Implement, 1)
                                    else Todo
                                // Parse verifiers dynamically - header tells us which columns are which
                                let verifierNames = getVerifierNames ()
                                let verifiers = 
                                    if cells.Length > 3 then
                                        verifierNames 
                                        |> List.mapi (fun i name -> 
                                            if i + 3 < cells.Length then Some (name, iconToStatus cells.[i + 3])
                                            else None)
                                        |> List.choose (fun x -> x)
                                        |> Map.ofList
                                    else Map.empty
                                match parseTaskDetail id with
                                | Some (item, summary) -> Some (item, status, verifiers, summary)
                                | None -> None
                            with _ -> None
                    )
                
                Some { Overview = overview; Tasks = tasks }
            with _ -> None
    
    let updateTaskStatus (taskId: int) (status: BacklogStatus) (verifiers: Map<VerifierName, VerifierStatus>) (summary: string option) =
        match read() with
        | Some data ->
            let updatedTasks = data.Tasks |> List.map (fun (item, s, v, sum) ->
                if item.Id = taskId then (item, status, verifiers, summary) else (item, s, v, sum))
            write { data with Tasks = updatedTasks }
        | None -> ()
    
    let getOverview () = 
        match read() with Some d -> d.Overview | None -> ""
    
    // Convert BacklogData to Plan for compatibility with existing flow
    let readAsPlan () : Plan option =
        match read() with
        | Some data -> 
            Some { 
                Overview = data.Overview
                Subtasks = data.Tasks |> List.map (fun (item, _, _, _) -> item)
            }
        | None -> None

// Terminal GUI helpers - display functions
module TerminalGUI =
    let escapeMarkup (s: string) = Markup.Escape(s)
    
    let getVerifierFilePath (name: string) =
        let files = 
            if Directory.Exists(verifiersDir) then
                Directory.GetFiles(verifiersDir, "*.md") 
                |> Array.filter (fun f -> 
                    let fn = Path.GetFileNameWithoutExtension(f)
                    let displayName = if fn.Length > 3 && fn.[2] = '-' && Char.IsDigit(fn.[0]) && Char.IsDigit(fn.[1]) 
                                      then fn.Substring(3) else fn
                    displayName = name)
                |> Array.tryHead
            else None
        files |> Option.defaultValue (Path.Combine(verifiersDir, $"{name}.md"))
    
    let phaseName = function Implement -> "Implement"

let escapeMarkup = TerminalGUI.escapeMarkup
let getVerifierFilePath = TerminalGUI.getVerifierFilePath
let phaseName = TerminalGUI.phaseName

// Update BACKLOG.md with completed task summary
let updateSharedContext (item: BacklogItem) (summary: string) =
    // Get current verifier results from state
    let verifiers = 
        state.Backlog 
        |> List.tryFind (fun (s, _, _) -> s.Id = item.Id) 
        |> Option.map (fun (_, _, timing) -> timing.VerifierResults)
        |> Option.defaultValue Map.empty
    BacklogFile.updateTaskStatus item.Id (Done 1) verifiers (Some summary)

module Prompts =
    let lines items = items |> String.concat "\n"
    let bullets items = items |> List.map (fun c -> $"- {c}") |> lines
    
    // All prompts should reference BACKLOG.md rather than embedding content
    let backlogRef = "Read .tools/ralph/BACKLOG.md for full context (Overview section for approach, Tasks section for status)."
    
    let architect request = 
        xc "R" [
            xt "role" "ARCHITECT and PRODUCT OWNER. Plan work as SPRINTS delivering tested product increments."
            xt "request" request
            xc "critical" [
                xt "warn" "SUBAGENTS START FROM SCRATCH - they have ZERO context beyond what you write in BACKLOG.md"
                xt "warn" "The Overview section is THE ONLY context they get - make it COMPREHENSIVE"
                xt "warn" "Each task description must be SELF-CONTAINED with all needed details"
            ]
            xc "overview_must_include" [
                xt "item" "The ORIGINAL USER REQUEST (quoted verbatim)"
                xt "item" "Your ANALYSIS of the problem - what files/functions are involved"
                xt "item" "Your APPROACH - how you decided to solve it and WHY"
                xt "item" "KEY DESIGN DECISIONS - patterns to use, edge cases to handle"
                xt "item" "CODEBASE CONTEXT - relevant existing code, conventions to follow"
            ]
            xc "task_must_include" [
                xt "item" "WHAT to FIX in CONTEXT"
                xt "item" "POINTER TO HOW/WHERE TO DO IT"
                xt "item" "WHY this approach (so implementor understands intent)"
                xt "item" "EXAMPLES of expected behavior or code patterns - important for sprint boundaries"
            ]
            xc "rules" [
                xt "rule" "NEVER create separate 'testing' or 'add tests' sprints - each sprint includes its own testing"
                xt "rule" "Each sprint is a PRODUCT INCREMENT with Definition of Done (DoD)"
                xt "rule" "A sprint is only complete when ALL DoD criteria pass"
                xt "rule" "DoD must include: Build succeeds, Tests pass, No duplication, Feature works"
            ]
            xc "output" [
                xt "file" ".tools/ralph/BACKLOG.md"
                xt "format" "Create/update the BACKLOG.md file with this exact structure"
                xc "structure" [
                    xt "heading" "# BACKLOG"
                    xt "section" "## Overview - COMPREHENSIVE context: original request, analysis, approach, design decisions, codebase context"
                    xt "section" "## Tasks - markdown table with columns:  ID | Task | Status | (Status = ○ Todo for all)"
                    xt "section" "## Task Details - for each task: ### Task N - Name, DETAILED description with what/where/how/why, **Definition of Done:** with criteria"
                ]
                xt "signal" "PLAN_COMPLETE"
            ]
        ] |> XmlPrompt.toPrompt

    let implement (s: BacklogItem) (iter: int) (feedback: string list) (prevDoDResults: DoDResult list) (pastSprints: XmlPrompt.SprintHistory list) (pastSteps: XmlPrompt.StepHistory list) (iterHistory: XmlPrompt.IterationHistory list) = 
        let dod = s.DoD |> List.map (fun c -> 
            let passed = prevDoDResults |> List.tryFind (fun r -> r.Criterion = c) |> Option.bind (fun r -> r.Passed)
            { Criterion = c; Passed = passed })
        let xml = XmlPrompt.build XmlPrompt.Implementor s iter dod pastSprints pastSteps iterHistory
        
        // Add feedback if any
        let feedbackEl = 
            if feedback.IsEmpty then []
            else [xc "fix" (feedback |> List.map (fun f -> xt "issue" f))]
        
        xc "R" ([xml] @ feedbackEl @ [
            xc "action" [
                xt "scope" $"Implement ONLY Task {s.Id}: {s.Name}. Other tasks are NOT your concern."
                xt "step" $"Read .tools/ralph/BACKLOG.md section '### Task {s.Id}' for full context"
                xt "step" "Implement code AND tests together"
                xt "step" "Run: dotnet build -c Release && dotnet test -c Release"
                xt "step" "Verify each DoD criterion for THIS task"
                xt "step" "Commit changes"
                xt "signal" "SUBTASK_COMPLETE"
            ]
        ]) |> XmlPrompt.toPrompt

    let arbiter (originalRequest: string) (errorReason: string) (sprintId: int option) (iterationsSpent: int) =
        let sprintInfo = match sprintId with Some id -> $"Sprint {id}" | None -> "Pre-sprint (planning)"
        xc "R" [
            xt "role" "ARBITER - conflict resolver when normal sprint execution has failed"
            xt "backlog" backlogRef
            xc "error" [
                xt "failed_at" sprintInfo
                xt "iterations" (string iterationsSpent)
                xt "reason" errorReason
            ]
            xt "request" originalRequest
            xc "task" [
                xt "analyze" "What went wrong? Root cause of failure"
                xt "decide" "Should remaining work be restructured?"
                xt "action" "Update .tools/ralph/BACKLOG.md with revised plan (keep completed tasks, restructure remaining)"
            ]
            xt "signal" "ARBITER_COMPLETE"
        ] |> XmlPrompt.toPrompt

let buildDashboard () =
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
    
    // Build progress bar
    let progressBar = 
        let barWidth = 50
        let filled = min barWidth (max 0 (int (float barWidth * progress / 100.0)))
        let empty = max 0 (barWidth - filled)
        let barStr = String.replicate filled "█" + String.replicate empty "░"
        let color = if progress >= 100.0 then "green" else if progress >= 50.0 then "yellow" else "blue"
        let pct = sprintf "%.1f" progress
        Markup($"[{color}]{barStr}[/] [{color}]{pct}%%[/] ({completedItems}/{totalItems} sprints)")
    
    // Build status panel
    let statusPanel = 
        let agentStatus = 
            match state.AgentStartTime with
            | Some startTime -> 
                let agentElapsed = DateTime.Now - startTime
                let agentElapsedStr = agentElapsed.ToString("mm\\:ss")
                $"[green bold]AGENT RUNNING[/] for {agentElapsedStr}"
            | None -> "[dim]Idle - no agent running[/]"
        Panel(Markup(agentStatus)).Header("[yellow]Current Activity[/]").Expand()
    
    // Build Product Backlog table - columns are dynamic based on discovered verifiers
    let verifierNames = Verifiers.listAll ()
    let t = Table().Border(TableBorder.Rounded).Expand()
    t.AddColumn("#") |> ignore
    t.AddColumn("Sprint") |> ignore
    t.AddColumn("Status") |> ignore
    t.AddColumn("DoD") |> ignore
    t.AddColumn("Iter") |> ignore
    // Add a column for each discovered verifier with clickable link to file
    for name in verifierNames do
        let filePath = getVerifierFilePath name
        let header = $"[link=file://{filePath}]{name}[/]"
        t.AddColumn(TableColumn(Markup(header))) |> ignore
    t.AddColumn("Time") |> ignore
    
    for (item, status, timing) in state.Backlog do
        let now = DateTime.Now
        let statusStr, iterStr = 
            match status with
            | Todo -> "[dim]Todo[/]", "[dim]-[/]"
            | Running (phase, iter) -> 
                let activePhase = "Implementing"
                $"[yellow]⏳ {activePhase}[/]", if iter > 1 then $"[yellow]⟲{iter}[/]" else $"[yellow]{iter}[/]"
            | Done iters -> "[green]✓ Done[/]", $"[green]{iters}[/]"
        // DoD status column with emoji counts
        let dodStr = 
            let total = item.DoD.Length
            let passed = timing.LastDoDResults |> List.filter (fun r -> r.Passed = Some true) |> List.length
            let failed = timing.LastDoDResults |> List.filter (fun r -> r.Passed = Some false) |> List.length
            match status with
            | Done _ -> $"[green]✅ {passed}/{total}[/]"
            | Running _ when failed > 0 -> $"[yellow]⚠️ {passed}/{total}[/]"
            | Running _ when passed > 0 -> $"[yellow]✅ {passed}/{total}[/]"
            | _ -> $"[dim]{total} items[/]"
        let timeStr = 
            match status, timing.EndTime with
            | Done _, Some endT -> 
                let mins = (endT - timing.StartTime).TotalMinutes
                if mins >= 60.0 then $"[green]{mins / 60.0:F1}h[/]"
                else $"[green]{int mins}min[/]"
            | Running _, _ -> 
                let mins = (now - timing.StartTime).TotalMinutes
                if mins >= 60.0 then $"[yellow]{mins / 60.0:F1}h[/]"
                else $"[yellow]{int mins}min[/]"
            | _ -> "[dim]-[/]"
        
        // Verifier columns - dynamically built from discovered verifiers (show status + iteration count)
        let verifierIcon name =
            match timing.VerifierResults.TryFind name with
            | Some (Passed iters) -> if iters > 1 then $"[green]✅{iters}[/]" else "[green]✅[/]"
            | Some (Failed iters) -> if iters > 1 then $"[red]❌{iters}[/]" else "[red]❌[/]"
            | Some NotStarted | None -> "[dim]○[/]"
        
        // Build row with dynamic verifier columns
        let verifierCells = verifierNames |> List.map verifierIcon
        let allCells = [string item.Id; escapeMarkup item.Name; statusStr; dodStr; iterStr] @ verifierCells @ [timeStr]
        t.AddRow(allCells |> Array.ofList) |> ignore
    
    // Build final verification mini table (sprint-agnostic, shown separately)
    let finalVerificationPanel : IRenderable =
        if state.FinalVerifierResults.IsEmpty then
            Text("") :> IRenderable
        else
            let ft = Table().Border(TableBorder.Simple).Expand()
            ft.AddColumn("Verifier") |> ignore
            ft.AddColumn("Status") |> ignore
            ft.AddColumn("Summary") |> ignore
            for name in verifierNames do
                let status = 
                    match state.FinalVerifierResults.TryFind name with
                    | Some (Passed i) -> if i > 1 then $"[green]✅ PASSED ({i})[/]" else "[green]✅ PASSED[/]"
                    | Some (Failed i) -> if i > 1 then $"[red]❌ FAILED ({i})[/]" else "[red]❌ FAILED[/]"
                    | Some NotStarted | None -> "[dim]○ Pending[/]"
                let summary = 
                    state.FinalVerifierSummaries.TryFind name 
                    |> Option.defaultValue "" 
                    |> escapeMarkup
                let filePath = getVerifierFilePath name
                let linkedName = $"[link=file://{filePath}]{name}[/]"
                ft.AddRow([| Markup(linkedName) :> IRenderable; Markup(status) :> IRenderable; Markup(summary) :> IRenderable |]) |> ignore
            Panel(ft).Header("[bold cyan]Final Verification (Complete Feature)[/]").Expand() :> IRenderable
    
    // Build CI checks panel (if CI monitoring is active)
    let ciPanel : IRenderable =
        match state.CIStatus with
        | None -> Text("") :> IRenderable
        | Some (prUrl, status) ->
            let statusStr = 
                match status with
                | None -> "[yellow]⏳ Pending[/]"
                | Some true -> "[green]✅ Passed[/]"
                | Some false -> "[red]❌ Failed[/]"
            let content = $"PR: [link={prUrl}]{prUrl}[/]\nStatus: {statusStr}"
            Panel(Markup(content)).Header("[bold]CI Checks[/]").Expand() :> IRenderable
    
    // Current sprint detail panel - shows description and DoD with status
    let summaryPanel =
        match currentItem with
        | Some (item, Running (phase, iter), timing) ->
            let desc = if item.Description.Length > 150 then item.Description.Substring(0, 147) + "..." else item.Description
            // Build DoD list with checkmarks/crosses for iter > 1
            let dodLines = 
                if iter > 1 && timing.LastDoDResults.Length > 0 then
                    timing.LastDoDResults |> List.map (fun r ->
                        let icon = match r.Passed with Some true -> "✅" | Some false -> "❌" | None -> "⬜"
                        $"  {icon} {escapeMarkup r.Criterion}"
                    ) |> String.concat "\n"
                else
                    item.DoD |> List.map (fun c -> $"  ⬜ {escapeMarkup c}") |> String.concat "\n"
            let lastReasonDetail = 
                match timing.IterationReasons |> List.tryLast with
                | Some (i, r) -> $"\n\n[red]Iteration {i} issue:[/] {escapeMarkup r}"
                | None -> ""
            let content = $"[bold]{escapeMarkup item.Name}[/]\n{escapeMarkup desc}\n\n[cyan]Definition of Done:[/]\n{dodLines}{lastReasonDetail}"
            Panel(Markup(content))
                .Header($"[cyan]Sprint {item.Id} - {phaseName phase} (iter {iter})[/]")
                .Expand()
            :> IRenderable
        | _ -> Text("") :> IRenderable
    
    let rows = [
        yield Rule($"[yellow bold]RALPH[/] - {elapsedStr}").RuleStyle("yellow") :> IRenderable
        yield Markup($"[dim]Backlog:[/] [link=file://{backlogFile}]{backlogFile}[/]") :> IRenderable
        yield progressBar :> IRenderable
        yield Text("") :> IRenderable
        yield statusPanel :> IRenderable
        yield summaryPanel
        yield Panel(t).Header("[bold]Product Backlog[/]").Expand() :> IRenderable
        yield finalVerificationPanel
        yield ciPanel
        yield Markup(state.Message) :> IRenderable
    ]
    
    Rows(rows)

let updateStatus itemId status msg =
    let newBacklog = state.Backlog |> List.map (fun (s, st, timing) -> 
        if s.Id = itemId then (s, status, timing) else (s, st, timing))
    state <- { state with Backlog = newBacklog; Message = msg }
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

let updateTiming itemId (f: BacklogItemTiming -> BacklogItemTiming) =
    let newBacklog = state.Backlog |> List.map (fun (s, st, timing) -> 
        if s.Id = itemId then (s, st, f timing) else (s, st, timing))
    state <- { state with Backlog = newBacklog }

let addIterationReason itemId iter reason =
    updateTiming itemId (fun t -> { t with IterationReasons = t.IterationReasons @ [(iter, reason)] })

let addIterationRecord itemId (record: IterationRecord) =
    updateTiming itemId (fun t -> { t with IterationHistory = t.IterationHistory @ [record] })

let updateDoDResults itemId (results: DoDResult list) =
    updateTiming itemId (fun t -> { t with LastDoDResults = results })

let startItemTiming itemId =
    updateTiming itemId (fun t -> { t with StartTime = DateTime.Now })

let endItemTiming itemId summary =
    updateTiming itemId (fun t -> { t with EndTime = Some DateTime.Now; Summary = Some summary })
    updateSharedContext 
        (state.Backlog |> List.find (fun (s, _, _) -> s.Id = itemId) |> fun (s, _, _) -> s) 
        summary
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

let getItemTiming itemId =
    state.Backlog |> List.tryFind (fun (s, _, _) -> s.Id = itemId) |> Option.map (fun (_, _, t) -> t)

let setMessage msg =
    state <- { state with Message = msg }
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

// ============================================================================
// AGENT EXECUTION
// ============================================================================

let runAgent (prompt: string) (_title: string) (_showWindow: bool) = async {
    Directory.CreateDirectory ralphDir |> ignore
    
    // Mark agent as running
    state <- { state with AgentStartTime = Some DateTime.Now }
    liveCtx |> Option.iter (fun ctx -> ctx.UpdateTarget(buildDashboard()); ctx.Refresh())
    
    // Run copilot via Fli
    let result = 
        cli {
            Exec "copilot"
            Arguments [| "--allow-all-tools"; "--allow-all-paths"; "--no-ask-user";"--no-color";"--plain-diff";"-s";"--model"; Model; "--stream"; "off" |]
            Input prompt
        }
        |> Command.execute
    
    state <- { state with AgentStartTime = None }
    liveCtx |> Option.iter (fun ctx -> ctx.UpdateTarget(buildDashboard()); ctx.Refresh())
    
    return result.Text |> Option.defaultValue ""
}

// Verifier prompts read from verifiers/*.md files at runtime (no caching)
let getVerifierPrompt (name: VerifierName) : string =
    Verifiers.getPrompt name

// Standard suffix added to all verifier prompts for structured output
let verifierSuffix = """

=== OUTPUT FORMAT ===
At the very end of your response, provide:
<ManagementSummary>A 1-2 sentence executive summary of what you found</ManagementSummary>
"""

// Parse ManagementSummary from verifier output
let parseManagementSummary (output: string) : string option =
    let m = Regex.Match(output, @"<ManagementSummary>([^<]+)</ManagementSummary>", RegexOptions.Singleline)
    if m.Success then Some (m.Groups.[1].Value.Trim())
    else None

let verifyStage showWin subtaskId (verifierName: VerifierName) (sprintItem: BacklogItem) = async {
    setMessage $"Verifying {verifierName}..."
    
    // ALL verifiers get sprint-specific context so they know their scope
    let sprintContext = 
        [
            ""
            "=== YOUR VERIFICATION SCOPE ==="
            $"Sprint {sprintItem.Id}: {sprintItem.Name}"
            $"Description: {sprintItem.Description}"
            ""
            "Definition of Done for THIS sprint ONLY:"
            yield! sprintItem.DoD |> List.map (fun d -> $"  - {d}")
            ""
            "IMPORTANT: Verify ONLY this sprint's work. Other sprints are NOT your concern."
            $"Backlog file: .tools/ralph/BACKLOG.md (your task: ### Task {sprintItem.Id})"
            ""
        ] |> String.concat "\n"
    
    let prompt = getVerifierPrompt verifierName + sprintContext + verifierSuffix
    let! out = runAgent prompt $"Verify-{verifierName}" showWin
    
    // Extract and display management summary
    let summary = parseManagementSummary out |> Option.defaultValue "(no summary)"
    
    if hasSignal "VERIFY_PASSED" out || hasSignal "VERIFY PASSED" out then
        AnsiConsole.MarkupLine $"[green]✓ {verifierName}:[/] {escapeMarkup summary}"
        setMessage $"[green]✓ {verifierName} passed[/]"
        return Ok ()
    elif hasSignal "VERIFY_FAILED" out || hasSignal "VERIFY FAILED" out then
        AnsiConsole.MarkupLine $"[red]✗ {verifierName}:[/] {escapeMarkup summary}"
        setMessage $"[red]✗ {verifierName} failed[/]"
        return Error $"{verifierName}: {summary}"
    else
        AnsiConsole.MarkupLine $"[yellow]⚠ {verifierName}:[/] {escapeMarkup summary}"
        setMessage $"[yellow]{verifierName} inconclusive[/]"
        return Error $"{verifierName} verification did not output VERIFY_PASSED or VERIFY_FAILED"
}

// Update verifier status while preserving/incrementing iteration count
let updateVerifierStatus itemId (verifierName: VerifierName) (passed: bool) =
    updateTiming itemId (fun t -> 
        let prevCount = 
            match t.VerifierResults.TryFind verifierName with
            | Some (Passed n) | Some (Failed n) -> n
            | _ -> 0
        let newStatus = if passed then Passed (prevCount + 1) else Failed (prevCount + 1)
        { t with VerifierResults = t.VerifierResults.Add(verifierName, newStatus) })
    liveCtx |> Option.iter (fun ctx -> ctx.Refresh())

let runAllVerifiers showWin subtaskId (sprintItem: BacklogItem) = async {
    // Discover verifiers from files
    let verifierNames = Verifiers.listAll ()
    let mutable allPassed = true
    
    for name in verifierNames do
        match! verifyStage showWin subtaskId name sprintItem with
        | Ok () -> 
            updateVerifierStatus subtaskId name true
        | Error e -> 
            updateVerifierStatus subtaskId name false
            allPassed <- false
    
    if allPassed then
        return Ok ()
    else
        return Error "One or more verifiers failed"
}

let showPlan plan =
    AnsiConsole.MarkupLine $"[bold]Overview:[/] {escapeMarkup plan.Overview}"
    AnsiConsole.MarkupLine $"[dim]Product Backlog: {plan.Subtasks.Length} sprints[/]\n"

// ============================================================================
// BACKLOG EXECUTION
// ============================================================================

let rec runBacklogItem (item: BacklogItem) iter totalIter feedback showWin = async {
    if iter > MaxIterations then return Error "Max iterations"
    else
        // Start timing on first iteration
        if iter = 1 then
            startItemTiming item.Id
        
        // Get previous DoD results and history for this item
        let timing = getItemTiming item.Id |> Option.defaultValue emptyTiming
        let prevDoDResults = timing.LastDoDResults
        
        // Build past sprints context (completed sprints before this one)
        let pastSprints: XmlPrompt.SprintHistory list = 
            state.Backlog 
            |> List.filter (fun (s, status, _) -> s.Id < item.Id && match status with Done _ -> true | _ -> false)
            |> List.map (fun (s, _, t) -> 
                { SprintId = s.Id
                  SprintName = s.Name
                  Summary = t.Summary |> Option.defaultValue "Completed" } : XmlPrompt.SprintHistory)
        
        // Build past steps context (previous iterations of THIS sprint)
        let pastSteps: XmlPrompt.StepHistory list =
            timing.IterationReasons 
            |> List.map (fun (i, reason) ->
                { Iteration = i
                  Passed = false
                  Summary = reason } : XmlPrompt.StepHistory)
        
        // Build iteration history (only for retries - full exchange)
        let iterHistory: XmlPrompt.IterationHistory list =
            timing.IterationHistory 
            |> List.map (fun r ->
                { Iteration = r.Iteration
                  AgentOutput = r.AgentOutput
                  VerifierResults = r.VerifierResults } : XmlPrompt.IterationHistory)
        
        updateStatus item.Id (Running (Implement, iter)) $"Sprint {item.Id}: Implement iteration {iter}"
        
        let prompt = Prompts.implement item iter feedback prevDoDResults pastSprints pastSteps iterHistory
        
        let! out = runAgent prompt ($"Implement-{item.Id}") showWin
        
        // Record this iteration for future retries
        let currentRecord: IterationRecord = {
            Iteration = iter
            AgentOutput = out
            VerifierResults = []
        }
        addIterationRecord item.Id currentRecord
        
        let retry fb dodResults = 
            let reason = fb |> List.tryHead |> Option.defaultValue "Unknown"
            addIterationReason item.Id iter reason
            updateDoDResults item.Id dodResults
            state <- { state with CompletedIterations = state.CompletedIterations + 1 }
            runBacklogItem item (iter + 1) (totalIter + 1) fb showWin
        
        // Check for completion signals (case-insensitive, flexible matching)
        let checkSignal (signal: string) = hasSignal signal out
        let isComplete = checkSignal "SUBTASK_COMPLETE" || checkSignal "SUBTASK COMPLETE"
        
        if isComplete then
            state <- { state with CompletedIterations = state.CompletedIterations + 1 }
            // Run verifiers - on success, sprint is done
            match! runAllVerifiers showWin item.Id item with 
            | Ok _ -> 
                // Mark all DoD as passed
                let allPassed = item.DoD |> List.map (fun c -> { Criterion = c; Passed = Some true })
                updateDoDResults item.Id allPassed
                endItemTiming item.Id $"Completed in {totalIter + 1} iterations"
                updateStatus item.Id (Done (totalIter + 1)) $"Sprint {item.Id} complete in {totalIter + 1} iterations"
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
        | Error e -> return Error $"Sprint {item.Id} failed: {e}"
}

// ============================================================================
// FINAL VERIFICATION
// ============================================================================

let runFinalVerifiers showWin = async {
    AnsiConsole.MarkupLine "[yellow]--- FINAL VERIFICATION - Complete Feature ---[/]"
    
    // Discover verifiers from files
    let verifierNames = Verifiers.listAll ()
    let mutable allPassed = true
    
    for name in verifierNames do
        setMessage $"Final verification: {name}..."
        
        // Get previous iteration count
        let prevCount = 
            match state.FinalVerifierResults.TryFind name with
            | Some (Passed n) | Some (Failed n) -> n
            | _ -> 0
        
        // Update state to show verifier is running
        state <- { state with FinalVerifierResults = state.FinalVerifierResults.Add(name, NotStarted) }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        
        let prompt = Verifiers.getPrompt name + verifierSuffix
        let! out = runAgent prompt $"FinalVerify-{name}" showWin
        
        // Extract and display management summary
        let summary = parseManagementSummary out |> Option.defaultValue "(no summary)"
        
        // Store summary for dashboard display
        state <- { state with FinalVerifierSummaries = state.FinalVerifierSummaries.Add(name, summary) }
        
        // All verifiers use the same signal detection
        if hasSignal "VERIFY_PASSED" out || hasSignal "VERIFY PASSED" out then
            AnsiConsole.MarkupLine $"[green]✓ Final {name}:[/] {escapeMarkup summary}"
            state <- { state with FinalVerifierResults = state.FinalVerifierResults.Add(name, Passed (prevCount + 1)) }
        elif hasSignal "VERIFY_FAILED" out || hasSignal "VERIFY FAILED" out then
            AnsiConsole.MarkupLine $"[red]✗ Final {name}:[/] {escapeMarkup summary}"
            state <- { state with FinalVerifierResults = state.FinalVerifierResults.Add(name, Failed (prevCount + 1)) }
            allPassed <- false
        else
            AnsiConsole.MarkupLine $"[yellow]⚠ Final {name}:[/] {escapeMarkup summary}"
            state <- { state with FinalVerifierResults = state.FinalVerifierResults.Add(name, Failed (prevCount + 1)) }
            allPassed <- false
        
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
    
    return allPassed
}

// Create a synthetic fixup sprint for failed final verifiers
// This goes through the FULL implement → verify cycle
let createFixupSprint (failedVerifiers: VerifierName list) (fixupNumber: int) : BacklogItem =
    let failedNames = failedVerifiers |> String.concat ", "
    let nextId = 
        state.Backlog 
        |> List.map (fun (item, _, _) -> item.Id) 
        |> List.max 
        |> (+) 1
    {
        Id = nextId
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
        AnsiConsole.MarkupLine $"[red]Final verification failed after {maxFixupSprints} fixup sprints[/]"
        return false
    else
        // Get failed verifiers
        let failed = 
            state.FinalVerifierResults 
            |> Map.toList 
            |> List.filter (fun (_, status) -> match status with Failed _ -> true | _ -> false)
            |> List.map fst
        
        let failedNames = failed |> String.concat ", "
        AnsiConsole.MarkupLine $"[yellow]--- FIXUP SPRINT #{currentFixup + 1} - Full implement/verify cycle ---[/]"
        AnsiConsole.MarkupLine $"[yellow]Failed verifiers: {failedNames}[/]"
        
        // Create and run a proper fixup sprint through the full cycle
        let fixupItem = createFixupSprint failed (currentFixup + 1)
        
        // Add to backlog and state for tracking
        state <- { state with 
                       Backlog = state.Backlog @ [(fixupItem, Todo, emptyTiming)]
                       TotalEstimatedIterations = state.TotalEstimatedIterations + 3
                 }
        liveCtx |> Option.iter (fun ctx -> ctx.Refresh())
        
        // Run full implement → verify cycle
        match! runBacklogItem fixupItem 1 0 [] showWin with
        | Ok () ->
            // Recurse to re-run final verification (counts are preserved and incremented)
            return! finalChecksWithFixup showWin maxFixupSprints (currentFixup + 1)
        | Error e ->
            AnsiConsole.MarkupLine $"[red]Fixup sprint failed: {escapeMarkup e}[/]"
            return false
}

let finalChecks showWin =
    setMessage "Running final verification on complete feature..."
    finalChecksWithFixup showWin 3 0 |> Async.RunSynchronously  // Allow up to 3 fixup sprints

// ============================================================================
// MAIN APPLICATION FLOW
// ============================================================================

let runWithLive (plan: Plan) showWin (originalRequest: string) = 
    // Estimate ~2 iterations per sprint (implement, maybe retry)
    let estimatedIterations = plan.Subtasks.Length * 3
    
    state <- { 
        Backlog = plan.Subtasks |> List.map (fun s -> (s, Todo, emptyTiming))
        StartTime = DateTime.Now
        Message = "Starting..."
        AgentStartTime = None
        TotalEstimatedIterations = estimatedIterations
        CompletedIterations = 0
        FinalVerifierResults = Map.empty
        FinalVerifierSummaries = Map.empty
        CIStatus = None
    }
    
    Directory.CreateDirectory ralphDir |> ignore
    
    // Write initial BACKLOG.md with plan
    let initialBacklog: BacklogFile.BacklogData = {
        Overview = $"{originalRequest}\n\n**Approach:** {plan.Overview}"
        Tasks = plan.Subtasks |> List.map (fun s -> (s, Todo, Map.empty, None))
    }
    BacklogFile.write initialBacklog
    
    let mutable result: Result<unit, string> = Ok ()
    let mutable finished = false
    
    // Start the main work in a .NET Task (actually runs in background)
    let workTask = Task.Run(fun () ->
        try
            let r = runAllBacklogItems plan.Subtasks showWin |> Async.RunSynchronously
            result <- r
            match r with
            | Ok () ->
                let passed = finalChecks showWin
                if passed then
                    setMessage "[green bold]WORKFLOW COMPLETE[/]"
                else
                    setMessage "[red bold]Completed but some checks failed[/]"
            | Error e ->
                setMessage $"[red]{escapeMarkup e}[/]"
        with ex ->
            result <- Error ex.Message
            setMessage $"[red]Exception: {escapeMarkup ex.Message}[/]"
        finished <- true
    )
    
    // Run the live display with refresh loop
    AnsiConsole.Live(buildDashboard())
        .AutoClear(false)
        .Overflow(VerticalOverflow.Ellipsis)
        .Start(fun ctx ->
            liveCtx <- Some ctx
            while not finished do
                ctx.UpdateTarget(buildDashboard())
                ctx.Refresh()
                Thread.Sleep(1000)
            ctx.UpdateTarget(buildDashboard())
            ctx.Refresh() // Final refresh
            liveCtx <- None
        )
    
    workTask.Wait() // Ensure task completes
    
    // Print clean completion message after Live display ends
    AnsiConsole.WriteLine()
    AnsiConsole.WriteLine()
    match result with
    | Ok () -> 
        AnsiConsole.MarkupLine "[green bold]✓ Workflow complete![/]"
        AnsiConsole.WriteLine()
        AnsiConsole.Write(FigletText("COMPLETE").Color(Color.Green))
    | Error _ -> 
        AnsiConsole.MarkupLine "[red bold]✗ Workflow finished with errors[/]"
    
    result

// Arbiter: Invoked when normal execution fails, attempts recovery with full context
// Returns Result<Plan, string> - caller decides what to do with the plan
let invokeArbiter (originalRequest: string) (errorReason: string) (failedSprintId: int option) (showWin: bool) : Result<Plan, string> =
    AnsiConsole.MarkupLine "[yellow]--- ARBITER INVOKED ---[/]"
    AnsiConsole.MarkupLine $"[dim]Error: {escapeMarkup errorReason}[/]"
    
    // Run arbiter agent with Prompts.arbiter and 'Arbiter' name
    let arbiterPrompt = Prompts.arbiter originalRequest errorReason failedSprintId 0
    let arbiterResult = runAgent arbiterPrompt "Arbiter" showWin |> Async.RunSynchronously
    
    // Arbiter updates BACKLOG.md directly, read the result
    match BacklogFile.readAsPlan() with
    | Some plan ->
        // Log arbiter decision on success
        let timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        let sprintStr = match failedSprintId with Some id -> string id | None -> "Planning"
        // Arbiter log removed - no separate file
        AnsiConsole.MarkupLine "[green]Arbiter produced recovery plan.[/]"
        Ok plan
    | None ->
        AnsiConsole.MarkupLine $"[red]Arbiter failed: BACKLOG.md not updated or invalid[/]"
        Error "Arbiter could not produce valid plan"

// Helper that wraps invokeArbiter with retry logic and execution for run loop integration
let rec invokeArbiterWithRetry (originalRequest: string) (errorReason: string) (sprintId: int option) showWin arbiterCount =
    if arbiterCount > 3 then
        AnsiConsole.MarkupLine "[red]Arbiter failed to recover after 3 attempts. Stopping.[/]"
        1
    else
        AnsiConsole.MarkupLine $"[dim]Arbiter attempt {arbiterCount + 1}/3[/]"
        match invokeArbiter originalRequest errorReason sprintId showWin with
        | Ok plan ->
            showPlan plan
            match runWithLive plan showWin originalRequest with
            | Ok () -> 0
            | Error e when e.StartsWith("REPLAN_REQUESTED") ->
                AnsiConsole.MarkupLine "[yellow]Recovery requested replanning.[/]"
                invokeArbiterWithRetry originalRequest e None showWin (arbiterCount + 1)
            | Error e ->
                invokeArbiterWithRetry originalRequest e None showWin (arbiterCount + 1)
        | Error _ ->
            invokeArbiterWithRetry originalRequest "Arbiter parse failed" sprintId showWin (arbiterCount + 1)

let rec run request showWin autoApprove replanCount arbiterCount = 
    if arbiterCount >= 3 then
        AnsiConsole.MarkupLine "[red]Max arbiter attempts (3). Stopping.[/]"
        1
    elif replanCount > 5 then
        AnsiConsole.MarkupLine "[red]Too many replans (5). Stopping.[/]"
        1
    else
        AnsiConsole.Clear()
        AnsiConsole.Write(FigletText("RALPH").Color(Color.Yellow))
        Directory.CreateDirectory ralphDir |> ignore
        
        if replanCount > 0 then
            AnsiConsole.MarkupLine $"[yellow]REPLANNING (attempt {replanCount + 1})...[/]\n"
        
        // Run planning with a spinner
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Planning...", fun ctx ->
                // Start planning in background
                let planTask = Task.Run(fun () ->
                    runAgent (Prompts.architect request) "Architect" showWin |> Async.RunSynchronously
                )
                // Update status while waiting
                while not planTask.IsCompleted do
                    match state.AgentStartTime with
                    | Some startTime ->
                        let elapsed = (DateTime.Now - startTime).ToString("mm\\:ss")
                        ctx.Status <- $"Planning... Agent running {elapsed}"
                    | None -> 
                        ctx.Status <- "Planning... Starting agent..."
                    Thread.Sleep(1000)
                planTask.Result |> ignore  // We don't need the output, LLM writes directly to BACKLOG.md
            )
        
        // Read plan from BACKLOG.md (LLM created/updated it directly)
        match BacklogFile.readAsPlan() with
        | None -> 
            AnsiConsole.MarkupLine $"[red]Planning failed: BACKLOG.md not found or invalid[/]"
            AnsiConsole.MarkupLine $"[dim]Arbiter attempt {arbiterCount + 1}/3[/]"
            match invokeArbiter request "Planning failed: BACKLOG.md not created or invalid" None showWin with
            | Ok newPlan ->
                showPlan newPlan
                match runWithLive newPlan showWin request with
                | Ok () -> 0
                | Error e2 -> run request showWin autoApprove 0 (arbiterCount + 1)
            | Error _ ->
                run request showWin autoApprove 0 (arbiterCount + 1)
        | Some plan ->
            showPlan plan
            if autoApprove || AnsiConsole.Confirm("Execute? ", true) then
                match runWithLive plan showWin request with
                | Ok () -> 0
                | Error e when e.StartsWith("REPLAN_REQUESTED") ->
                    AnsiConsole.MarkupLine $"[yellow]Subtask requested replanning.[/]"
                    AnsiConsole.MarkupLine $"[dim]{escapeMarkup e}[/]"
                    run request showWin autoApprove (replanCount + 1) 0  // Reset arbiterCount on explicit replan
                | Error e -> 
                    AnsiConsole.MarkupLine $"[dim]Arbiter attempt {arbiterCount + 1}/3[/]"
                    match invokeArbiter request e None showWin with
                    | Ok newPlan ->
                        showPlan newPlan
                        match runWithLive newPlan showWin request with
                        | Ok () -> 0
                        | Error e2 -> run request showWin autoApprove 0 (arbiterCount + 1)
                    | Error _ ->
                        run request showWin autoApprove 0 (arbiterCount + 1)
            else 0

// CI/AzDo Monitoring module
module CIMonitor =
    type BuildStatus = Pending | Success | Failed of failures: string list
    
    let runGitPush () =
        try
            let result = cli { Exec "git"; Arguments [| "push" |] } |> Command.execute
            if result.ExitCode = 0 then Ok (result.Text |> Option.defaultValue "")
            else Error (result.Error |> Option.defaultValue "" |> fun e -> e + (result.Text |> Option.defaultValue ""))
        with ex -> Error ex.Message
    
    let extractUniqueFailuresWithLLM (buildOutput: string) showWin = async {
        // Use LLM to extract unique test/build failures
        let prompt = Prompts.lines [
            "--- CI FAILURE ANALYZER ---"
            ""
            "Your job is to extract UNIQUE failures from CI build output."
            "This CI has many dimensions (OS, config, product switches) so same failure may appear multiple times."
            ""
            "ANALYZE THIS OUTPUT AND LIST:"
            "1. UNIQUE build failures (don't repeat the same error)"
            "2. UNIQUE test failures (don't repeat the same failing test)"
            ""
            "Format your response as:"
            "BUILD_FAILURES:"
            "- error 1"
            "- error 2"
            ""
            "TEST_FAILURES:"
            "- TestName1"
            "- TestName2"
            ""
            "If no failures, output: NO_FAILURES"
            ""
            "BUILD OUTPUT:"
            buildOutput
        ]
        let! result = runAgent prompt "CI-FailureAnalyzer" showWin
        if result.Contains("NO_FAILURES") then return []
        else 
            // Parse the failure lists
            let lines = result.Split('\n') |> Array.filter (fun l -> l.StartsWith("- "))
            return lines |> Array.map (fun l -> l.Substring(2).Trim()) |> Array.toList
    }
    
    let checkBuildStatus showWin = async {
        // Check AzDo/GitHub Actions status via gh CLI
        try
            let result = cli { Exec "gh"; Arguments [| "pr"; "checks"; "--fail-fast" |] } |> Command.execute
            let output = result.Text |> Option.defaultValue ""
            let errors = result.Error |> Option.defaultValue ""
            
            if output.Contains("pass") && not (output.Contains("fail")) then
                return Success
            elif output.Contains("pending") || output.Contains("in_progress") then
                return Pending
            else
                let! failures = extractUniqueFailuresWithLLM (output + "\n" + errors) showWin
                return Failed failures
        with _ ->
            return Pending  // If we can't check, assume pending
    }
    
    let monitorCI showWin maxWaitMinutes = async {
        let mutable status = Pending
        let mutable waited = 0
        let intervalMinutes = 30
        
        AnsiConsole.MarkupLine "[cyan]Starting CI monitoring...[/]"
        
        while status = Pending && waited < maxWaitMinutes do
            let! s = checkBuildStatus showWin
            status <- s
            
            match status with
            | Success ->
                AnsiConsole.MarkupLine "[green]✓ CI passed![/]"
            | Failed failures ->
                AnsiConsole.MarkupLine $"[red]✗ CI failed with {failures.Length} unique failures[/]"
                for f in failures do
                    AnsiConsole.MarkupLine $"[red]  - {escapeMarkup f}[/]"
            | Pending ->
                AnsiConsole.MarkupLine $"[yellow]CI still pending. Waiting {intervalMinutes} minutes (total waited: {waited} min)...[/]"
                Thread.Sleep(intervalMinutes * 60 * 1000)
                waited <- waited + intervalMinutes
        
        return status
    }
    
    let runCIFixupLoop request showWin = async {
        let mutable status = Pending
        let mutable iteration = 0
        let maxIterations = 5
        
        while not (status = Success) && iteration < maxIterations do
            iteration <- iteration + 1
            AnsiConsole.MarkupLine $"[cyan]CI Fixup iteration {iteration}/{maxIterations}[/]"
            
            // Monitor CI
            let! s = monitorCI showWin 180  // Max 3 hours wait
            status <- s
            
            match status with
            | Success -> ()
            | Pending -> 
                AnsiConsole.MarkupLine "[yellow]CI still pending after max wait time[/]"
            | Failed failures ->
                if iteration < maxIterations then
                    // Create a fixup task
                    let failureLines = failures |> List.map (fun f -> "- " + f)
                    let fixupPromptLines = 
                        [
                            "--- CI FIXUP AGENT ---"
                            ""
                            "CI has failed with the following UNIQUE issues:"
                        ] @ failureLines @ [
                            ""
                            "Your job is to fix these CI failures."
                            ""
                            "1. Read .tools/ralph/BACKLOG.md to understand the feature"
                            "2. Investigate each failure"
                            "3. Make targeted fixes"
                            "4. Commit and push the fixes"
                            ""
                            "Focus only on fixing CI failures, not redesigning."
                        ]
                    let fixupPrompt = fixupPromptLines |> String.concat "\n"
                    
                    let! _ = runAgent fixupPrompt "CI-Fixup" showWin
                    
                    // Push the fixes
                    match runGitPush() with
                    | Ok _ -> AnsiConsole.MarkupLine "[green]Pushed fixes[/]"
                    | Error e -> AnsiConsole.MarkupLine $"[red]Push failed: {escapeMarkup e}[/]"
        
        return status
    }

let runInteractive () = 
    AnsiConsole.Clear()
    AnsiConsole.Write(FigletText("RALPH").Color(Color.Yellow))
    let showWin = AnsiConsole.Confirm("Show agent windows? ", true)
    AnsiConsole.MarkupLine "\n[cyan]What do you want to build?[/]"
    let request = AnsiConsole.Ask<string> "[green]>[/] "
    run request showWin false 0 0

let runWithPush request showWin auto =
    let result = run request showWin auto 0 0
    if result = 0 then
        AnsiConsole.MarkupLine "[cyan]--push enabled: Pushing changes and monitoring CI...[/]"
        match CIMonitor.runGitPush() with
        | Ok _ ->
            AnsiConsole.MarkupLine "[green]✓ Pushed successfully[/]"
            let status = CIMonitor.runCIFixupLoop request showWin |> Async.RunSynchronously
            match status with
            | CIMonitor.Success -> 0
            | CIMonitor.Pending -> 
                AnsiConsole.MarkupLine "[yellow]CI still pending[/]"
                0
            | CIMonitor.Failed _ ->
                AnsiConsole.MarkupLine "[red]CI failed after all retry attempts[/]"
                1
        | Error e ->
            AnsiConsole.MarkupLine $"[red]Push failed: {escapeMarkup e}[/]"
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
    if push then runWithPush request showWin auto |> exit
    else run request showWin auto 0 0 |> exit