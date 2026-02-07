// Prompting.fsx - All prompting logic, IO, verifiers, sprint files, backlog
// Loaded by Ralph.fsx after TypeDefinitions.fsx
// IMPORTANT: No #load here - Ralph.fsx loads all dependencies

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Utils
open XmlHelpers
open TypeDefinitions

/// Verifier file system - verifiers are discovered from verifiers/*.md files
module Verifiers =
    /// Parse filename to extract order and display name
    let private parseFileName (name: string) : int * string =
        let m = Regex.Match(name, @"^(\d+)-(.+)$")
        if m.Success then (int m.Groups.[1].Value, m.Groups.[2].Value)
        else (999, name)
    
    /// List all available verifier names from the verifiers directory, ordered by prefix
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
    let private readFile (displayName: string) : (string * string) option =
        match getFileName displayName with
        | Some fileName ->
            let filePath = Path.Combine(Config.verifiersDir, fileName + ".md")
            if File.Exists(filePath) then
                let content = File.ReadAllText(filePath)
                let lines = content.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
                if lines.Length > 0 then Some (lines.[0].Trim(), content)
                else None
            else None
        | None -> None
    
    /// Get the prompt for a verifier
    let getPrompt (name: VerifierName) : string =
        match readFile name with
        | Some (_, prompt) -> prompt
        | None -> $"Verifier {name}: No file found at verifiers/{name}.md. Please create one."
    
    /// Get the role description for a verifier (first line of file)
    let getRoleDescription (name: VerifierName) : string =
        match readFile name with
        | Some (role, _) -> role
        | None -> $"Verifier for {name}"
    
    /// Get the file path for a verifier by display name
    let getFilePath (name: string) : string =
        let files = 
            if Directory.Exists(Config.verifiersDir) then
                Directory.GetFiles(Config.verifiersDir, "*.md") 
                |> Array.filter (fun f -> 
                    let fn = Path.GetFileNameWithoutExtension(f)
                    let displayName = 
                        if fn.Length > 3 && fn.[2] = '-' && Char.IsDigit(fn.[0]) && Char.IsDigit(fn.[1]) 
                        then fn.Substring(3) else fn
                    displayName = name)
                |> Array.tryHead
            else None
        files |> Option.defaultValue (Path.Combine(Config.verifiersDir, $"{name}.md"))

/// Sprint files management - individual files per sprint
module SprintFiles =
    let private parseFileName (name: string) : int * string =
        let m = Regex.Match(name, @"^(\d+)_(.+)\.md$")
        if m.Success then (int m.Groups.[1].Value, m.Groups.[2].Value.Replace("_", " "))
        else (999, Path.GetFileNameWithoutExtension(name))
    
    let listSprints () : string list =
        if Directory.Exists(Config.sprintsDir) then
            Directory.GetFiles(Config.sprintsDir, "*.md")
            |> Array.map (fun p -> (p, parseFileName (Path.GetFileName p)))
            |> Array.sortBy (fun (_, (order, _)) -> order)
            |> Array.map fst
            |> Array.toList
        else []
    
    let private parseDoD (body: string) : string list =
        let lines = body.Split('\n')
        let dodStart = lines |> Array.tryFindIndex (fun l -> 
            l.TrimStart().StartsWith("## Definition of Done") || 
            l.TrimStart().StartsWith("## DoD"))
        match dodStart with
        | Some idx ->
            lines.[(idx + 1)..]
            |> Array.takeWhile (fun l -> not (l.TrimStart().StartsWith("## ")))
            |> Array.filter (fun l -> l.TrimStart().StartsWith("- "))
            |> Array.map (fun l -> 
                let trimmed = l.TrimStart()
                let content = Regex.Replace(trimmed, @"^- \[[x ]\] ", "")
                let content = if content.StartsWith("- ") then content.Substring(2) else content
                content.Trim())
            |> Array.toList
        | None -> []
    
    let private parseDescription (body: string) : string =
        let lines = body.Split('\n')
        let dodStart = lines |> Array.tryFindIndex (fun l -> 
            l.TrimStart().StartsWith("## Definition of Done") || 
            l.TrimStart().StartsWith("## DoD"))
        match dodStart with
        | Some idx -> lines.[0..(idx - 1)] |> String.concat "\n" |> fun s -> s.Trim()
        | None -> body.Trim()
    
    let readSprint (filePath: string) : BacklogItem option =
        if not (File.Exists filePath) then None
        else
            try
                let content = File.ReadAllText(filePath)
                let body = YamlFrontmatter.extractBody content
                let (order, name) = parseFileName (Path.GetFileName filePath)
                Some {
                    FilePath = filePath
                    Order = order
                    Name = name
                    Description = parseDescription body
                    DoD = parseDoD body
                }
            with _ -> None
    
    let readAllSprints () : BacklogItem list =
        listSprints () |> List.choose readSprint
    
    let ensureDir () =
        Directory.CreateDirectory(Config.sprintsDir) |> ignore
    
    let clearSprints () =
        if Directory.Exists(Config.sprintsDir) then
            Directory.GetFiles(Config.sprintsDir, "*.md") |> Array.iter File.Delete

/// Backlog file management - overview only
module BacklogFile =
    let writeOverview (overview: string) =
        Directory.CreateDirectory(Config.ralphDir) |> ignore
        File.WriteAllText(Config.backlogFile, $"# BACKLOG\n\n{overview}")
    
    let readOverview () : string option =
        if File.Exists(Config.backlogFile) then Some (File.ReadAllText(Config.backlogFile))
        else None
    
    let hasValidPlan () : bool =
        File.Exists(Config.backlogFile) && SprintFiles.listSprints().Length > 0

/// XML prompt builder for subagents
module XmlPrompt =
    type Role = Implementor
    
    type IterationHistory = {
        Iteration: int
        AgentOutput: string
        VerifierResults: (VerifierName * bool * string) list
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
                            let elName = verifierName.ToLowerInvariant().Replace("-", "_")
                            let suffix = if passed then "" else " [FAILED]"
                            xt elName (feedback + suffix))
                    let exchanges = [xt "out" h.AgentOutput] @ verifierPart
                    [xac ("i" + string h.Iteration) [] exchanges]
                )
            Some (xc "history" items)
    
    let build 
        (role: Role) 
        (currentSprint: BacklogItem)
        (iteration: int)
        (dod: DoDResult list)
        (pastSprints: SprintHistory list)
        (pastSteps: StepHistory list)
        (iterationHistory: IterationHistory list) =
        
        xc "R" ([
            xc "your_file" [
                xt "path" currentSprint.FilePath
                xt "instruction" "THIS FILE IS YOUR ENTIRE WORLD. Read it. Implement it."
            ]
            roleElement role
            xat "impl" [("order", string currentSprint.Order); ("i", string iteration)] 
                (currentSprint.Name + ": " + currentSprint.Description)
            dodElement dod
            pastSprintsElement pastSprints
            pastStepsElement pastSteps
        ] @ 
        (match iterationHistoryElement iterationHistory with Some el -> [el] | None -> []))
    
    let toPrompt (el: XElement) = el.ToString()

/// All prompt templates
module Prompts =
    let lines items = items |> String.concat "\n"
    let bullets items = items |> List.map (fun c -> $"- {c}") |> lines
    
    let architect request = 
        let templatePath = Config.templateFile
        let sprintsDir = Config.sprintsDir
        let backlogPath = Config.backlogFile
        xc "R" [
            xt "role" "ARCHITECT. Your job: create SELF-CONTAINED sprint files that other agents will implement."
            xt "request" request
            
            xc "how_this_system_works" [
                xt "fact" "You create sprint files. Each sprint file goes to a SEPARATE AGENT."
                xt "fact" "Each agent ONLY sees its own sprint file. It cannot see BACKLOG.md or other sprints."
                xt "fact" "Verifier agents check each sprint. They also only see that sprint file."
                xt "fact" "BACKLOG.md is only for YOU (planner) and FINAL verification at the very end."
                xt "conclusion" "Sprint files must contain EVERYTHING an implementor needs. No assumptions."
            ]
            
            xc "your_outputs" [
                xc "file1_backlog" [
                    xt "path" backlogPath
                    xt "purpose" "YOUR planning notes + context for final verification"
                    xt "format" "# BACKLOG\n\n## Original Request\n[paste request verbatim]\n\n## Analysis\n[your analysis]\n\n## Approach\n[solution strategy]\n\n## Sprint Overview\n| # | Name | Purpose |"
                ]
                xc "files_sprints" [
                    xt "directory" sprintsDir
                    xt "naming" "NN_SprintName.md - Examples: 01_Setup_Infrastructure.md, 02_Add_Parser.md, 03_Write_Tests.md"
                    xt "quantity" "Create as many sprint files as needed. Each is an independent unit of work."
                ]
            ]
            
            xc "sprint_file_format" [
                xt "line1" "---"
                xt "line2" "---"
                xt "required" "# Sprint: [title]"
                xt "required" "## Context - WHY this sprint exists"
                xt "required" "## Description - WHAT to implement with DETAILED guidance"
                xt "required" "## Definition of Done - bullet list starting with '- '"
            ]
            
            xc "read_template_first" [
                xt "path" templatePath
                xt "instruction" "READ this file BEFORE creating sprints. It shows the exact structure."
            ]
            
            xc "critical_rules" [
                xt "rule1" "Each sprint file is SELF-CONTAINED. Include ALL context in the file itself."
                xt "rule2" "Implementor has NO knowledge of the codebase except what YOU tell them in the sprint file."
                xt "rule3" "Include: file paths, function names, code patterns to follow, examples."
                xt "rule4" "Each sprint must be INDEPENDENTLY TESTABLE - include tests in same sprint, never separate."
                xt "rule5" "Definition of Done items must be CONCRETE: 'Tests pass' not 'Code is good'."
            ]
            
            xc "dod_format" [
                xt "format" "Each criterion on its own line starting with '- ' (dash space)"
                xt "example" "- Build succeeds with no warnings\n- Function X returns correct value for input Y\n- Tests pass locally"
                xt "bad" "Do NOT use '- [ ]' checkboxes. Just '- text'."
            ]
            
            xt "when_done" "Output: PLAN_COMPLETE"
        ] |> XmlPrompt.toPrompt

    let implement (s: BacklogItem) (iter: int) (feedback: string list) (prevDoDResults: DoDResult list) (pastSprints: XmlPrompt.SprintHistory list) (pastSteps: XmlPrompt.StepHistory list) (iterHistory: XmlPrompt.IterationHistory list) = 
        let dod = s.DoD |> List.map (fun c -> 
            let passed = prevDoDResults |> List.tryFind (fun r -> r.Criterion = c) |> Option.bind (fun r -> r.Passed)
            { Criterion = c; Passed = passed })
        let xml = XmlPrompt.build XmlPrompt.Implementor s iter dod pastSprints pastSteps iterHistory
        
        let feedbackEl = 
            if feedback.IsEmpty then []
            else [xc "fix" (feedback |> List.map (fun f -> xt "issue" f))]
        
        xc "R" ([xml] @ feedbackEl @ [
            xc "action" [
                xt "focus" $"Sprint {s.Order}: {s.Name}. Other sprints are NOT your concern."
                xt "step" "Implement code AND tests together"
                xt "step" "Run: dotnet build -c Release && dotnet test -c Release"
                xt "step" "Verify each DoD criterion for THIS sprint"
                xt "step" "Commit changes"
                xt "signal" "SUBTASK_COMPLETE"
            ]
        ]) |> XmlPrompt.toPrompt

    let arbiter (originalRequest: string) (errorReason: string) (sprintOrder: int option) (iterationsSpent: int) (finishedSprints: (int * string) list) (pendingSprints: (int * string) list) =
        let sprintInfo = match sprintOrder with Some o -> $"Sprint {o}" | None -> "Pre-sprint (planning)"
        let finishedList = 
            if finishedSprints.IsEmpty then [xt "none" "No sprints completed yet"]
            else finishedSprints |> List.map (fun (order, name) -> xt "done" $"{order}: {name}")
        let pendingList = 
            if pendingSprints.IsEmpty then [xt "none" "No pending sprints"]
            else pendingSprints |> List.map (fun (order, name) -> xt "pending" $"{order}: {name}")
        xc "R" [
            xt "role" "ARBITER - you fix failed sprints by restructuring the plan"
            
            xc "how_this_system_works" [
                xt "fact1" "Sprint files in sprints/ directory are executed one by one by separate agents"
                xt "fact2" "Each agent ONLY sees its own sprint file - nothing else"
                xt "fact3" "COMPLETED sprints are DONE and should NOT be touched"
                xt "fact4" "You can DELETE/CREATE/MODIFY remaining sprint files to fix the problem"
            ]
            
            xc "locations" [
                xt "backlog" Config.backlogFile
                xt "sprints_dir" Config.sprintsDir
                xt "template" Config.templateFile
            ]
            
            xc "sprint_status" [
                xc "completed_DO_NOT_TOUCH" finishedList
                xc "remaining_CAN_MODIFY" pendingList
            ]
            
            xc "failure" [
                xt "failed_at" sprintInfo
                xt "iterations_spent" (string iterationsSpent)
                xt "error" errorReason
            ]
            
            xt "original_request" originalRequest
            
            xc "your_powers" [
                xt "power" "DELETE any remaining sprint file"
                xt "power" "CREATE new sprint files (use higher numbers: 10_, 11_, etc.)"
                xt "power" "MODIFY remaining sprint files to add missing context"
                xt "power" "Update BACKLOG.md notes"
            ]
            
            xc "sprint_file_format" [
                xt "reminder" "New sprints must follow format: ---\\n---\\n# Sprint: Title\\n## Context\\n## Description\\n## Definition of Done"
                xt "dod_format" "Each DoD item on its own line starting with '- ' (dash space)"
                xt "self_contained" "Include ALL context in each sprint file - implementor sees NOTHING else"
            ]
            
            xc "task" [
                xt "analyze" "What went wrong? Root cause?"
                xt "decide" "How to restructure remaining work?"
                xt "action" "Delete/create/modify sprint files, then output ARBITER_COMPLETE"
            ]
        ] |> XmlPrompt.toPrompt

/// Standard suffix added to all verifier prompts
let verifierSuffix = """

=== OUTPUT FORMAT ===
At the very end of your response, provide:
<ManagementSummary>A 1-2 sentence executive summary of what you found</ManagementSummary>
"""

/// Parse ManagementSummary from verifier output
let parseManagementSummary (output: string) : string option =
    let m = Regex.Match(output, @"<ManagementSummary>([^<]+)</ManagementSummary>", RegexOptions.Singleline)
    if m.Success then Some (m.Groups.[1].Value.Trim())
    else None

/// Build context for sprint-level verification
let buildSprintVerificationContext (sprintItem: BacklogItem) (approvedCommits: Map<VerifierName, string>) (currentCommit: string option) (currentVerifier: VerifierName) =
    let commitSection = 
        if approvedCommits.IsEmpty then []
        else
            let currentInfo = 
                match currentCommit with
                | Some c -> $"Current HEAD: {c}"
                | None -> "Current HEAD: unknown"
            let formatApproval (name, hash) =
                if name = currentVerifier then $"  - {name} (= this is you!): approved at {hash}"
                else $"  - {name}: approved at {hash}"
            [
                ""
                "=== PRIOR VERIFIER APPROVALS ==="
                currentInfo
                "Previously approved at these commits:"
                yield! approvedCommits |> Map.toList |> List.map formatApproval
                ""
                "Focus verification on changes SINCE these commits."
                "If current code matches approved commit, consider already verified."
                ""
            ]
    [
        ""
        "=== YOUR VERIFICATION SCOPE ==="
        $"Sprint file: {sprintItem.FilePath}"
        $"Sprint: {sprintItem.Name}"
        ""
        "THIS FILE IS YOUR COMPLETE SCOPE."
        "Verify ONLY work described in this sprint file."
        "Do NOT look at BACKLOG.md or other sprint files."
        ""
        "Definition of Done for THIS sprint:"
        yield! sprintItem.DoD |> List.map (fun d -> $"  - {d}")
        yield! commitSection
        ""
    ] |> String.concat "\n"

/// Build context for final verification (complete feature)
let buildFinalVerificationContext (sprintFiles: string list) =
    let sprintFilesList = sprintFiles |> List.map (fun f -> $"  - {f}") |> String.concat "\n"
    [
        ""
        "=== FINAL VERIFICATION - COMPLETE FEATURE ==="
        $"Overview: {Config.backlogFile}"
        "Sprint files:"
        sprintFilesList
        ""
        "Verify the COMPLETE FEATURE works as a whole."
        "Check integration between all sprints."
        "Verify the original request in BACKLOG.md is fully satisfied."
        ""
    ] |> String.concat "\n"

/// Result of interpreting verifier output
type VerifyOutcome = 
    | VPassed of summary: string
    | VFailed of summary: string
    | VInconclusive of summary: string

/// Interpret verifier output - parse result and summary
let interpretVerifierOutput (output: string) : VerifyOutcome =
    let summary = parseManagementSummary output |> Option.defaultValue "(no summary)"
    if XmlHelpers.hasSignal "VERIFY_PASSED" output || XmlHelpers.hasSignal "VERIFY PASSED" output then
        VPassed summary
    elif XmlHelpers.hasSignal "VERIFY_FAILED" output || XmlHelpers.hasSignal "VERIFY FAILED" output then
        VFailed summary
    else
        VInconclusive summary
