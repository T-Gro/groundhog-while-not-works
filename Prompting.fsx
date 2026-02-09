open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Utils
open XmlHelpers
open TypeDefinitions

module Verifiers =
    let private parseFileName (name: string) =
        let m = Regex.Match(name, @"^(\d+)-(.+)$")
        if m.Success then (int m.Groups.[1].Value, m.Groups.[2].Value)
        else (999, name)
    
    let listAll () =
        if Directory.Exists(Config.verifiersDir) then
            Directory.GetFiles(Config.verifiersDir, "*.md")
            |> Array.map Path.GetFileNameWithoutExtension
            |> Array.sortBy id  // "01-X" < "02-Y" alphabetically
            |> Array.map (parseFileName >> snd)
            |> Array.toList
        else []
    
    let isValid name = listAll() |> List.contains name
    
    let private getFileNameRaw displayName =
        if Directory.Exists(Config.verifiersDir) then
            Directory.GetFiles(Config.verifiersDir, "*.md")
            |> Array.map Path.GetFileNameWithoutExtension
            |> Array.tryFind (fun n -> snd (parseFileName n) = displayName)
        else None
    
    let getPrompt name =
        match getFileNameRaw name with
        | Some fileName ->
            let path = Path.Combine(Config.verifiersDir, fileName + ".md")
            if File.Exists path then File.ReadAllText path
            else $"Verifier {name}: No file found."
        | None -> $"Verifier {name}: No file found."
    
    let getFilePath name =
        match getFileNameRaw name with
        | Some fileName -> Path.Combine(Config.verifiersDir, fileName + ".md")
        | None -> Path.Combine(Config.verifiersDir, $"{name}.md")

module SprintFiles =
    let private parseFileName name =
        let m = Regex.Match(name, @"^(\d+)_(.+)\.md$")
        if m.Success then (int m.Groups.[1].Value, m.Groups.[2].Value.Replace("_", " "))
        else (999, Path.GetFileNameWithoutExtension(name))
    
    let listSprints () =
        if Directory.Exists(Config.sprintsDir) then
            Directory.GetFiles(Config.sprintsDir, "*.md")
            |> Array.sortBy Path.GetFileName
            |> Array.toList
        else []
    
    let private findDoDIndex (lines: string[]) =
        lines |> Array.tryFindIndex (fun l -> 
            l.TrimStart().StartsWith("## Definition of Done") || l.TrimStart().StartsWith("## DoD"))
    
    let private parseDoD (body: string) =
        let lines = body.Split('\n')
        match findDoDIndex lines with
        | Some idx ->
            lines.[(idx + 1)..]
            |> Array.takeWhile (fun l -> not (l.TrimStart().StartsWith("## ")))
            |> Array.filter (fun l -> l.TrimStart().StartsWith("- "))
            |> Array.map (fun l -> Regex.Replace(l.TrimStart(), @"^- (\[[x ]\] )?", "").Trim())
            |> Array.toList
        | None -> []
    
    let private parseDescription (body: string) =
        let lines = body.Split('\n')
        match findDoDIndex lines with
        | Some idx -> lines.[0..(idx - 1)] |> String.concat "\n" |> fun s -> s.Trim()
        | None -> body.Trim()
    
    let readSprint filePath =
        if not (File.Exists filePath) then None
        else
            try
                let body = File.ReadAllText filePath |> YamlFrontmatter.extractBody
                let (order, name) = parseFileName (Path.GetFileName filePath)
                Some { FilePath = filePath; Order = order; Name = name; Description = parseDescription body; DoD = parseDoD body }
            with _ -> None
    
    let readAllSprints () = listSprints () |> List.choose readSprint
    let ensureDir () = Directory.CreateDirectory(Config.sprintsDir) |> ignore
    let clearSprints () = if Directory.Exists(Config.sprintsDir) then Directory.GetFiles(Config.sprintsDir, "*.md") |> Array.iter File.Delete

module BacklogFile =
    let writeOverview overview =
        Directory.CreateDirectory(Config.ralphDir) |> ignore
        File.WriteAllText(Config.backlogFile, $"# BACKLOG\n\n{overview}")
    
    let readOverview () = if File.Exists(Config.backlogFile) then Some (File.ReadAllText(Config.backlogFile)) else None
    let hasValidPlan () = File.Exists(Config.backlogFile) && SprintFiles.listSprints().Length > 0

module XmlPrompt =
    type Role = Implementor | Arbiter
    
    type IterationHistory = { Iteration: int; AgentOutput: string; VerifierResults: (string * bool * string) list }
    type SprintHistory = { SprintId: int; SprintName: string; Summary: string }
    type StepHistory = { Iteration: int; Passed: bool; Summary: string }
    type FailedSprintContext = {
        SprintOrder: int; SprintName: string; SprintFilePath: string; IterationsSpent: int
        LastIterations: IterationHistory list; VerifierFailureCounts: Map<string, int>
    }
    
    let private roleElement (role: Role) =
        match role with
        | Implementor ->
            xt "Implementor" "YOU ARE AN IMPLEMENTOR for F# compiler. Write code fulfilling sprint requirements. Follow DoD. Minimize breaking changes. Reuse existing helpers. Minimize allocations. Build and tests MUST pass."
        | Arbiter ->
            xt "Arbiter" "YOU ARE THE ARBITER. A sprint has failed despite multiple attempts. Analyze WHY, then restructure the plan to fix the root cause."
    
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
            // Only keep last 3 iterations for prompt size, but preserve iteration numbers
            let maxHistory = 3
            let skippedCount = max 0 (history.Length - maxHistory)
            let recentHistory = history |> List.skip skippedCount
            
            let skippedNote = 
                if skippedCount > 0 then 
                    [xt "note" $"({skippedCount} earlier iteration(s) omitted for brevity)"]
                else []
            
            let items = 
                recentHistory |> List.collect (fun h ->
                    let verifierPart = 
                        h.VerifierResults |> List.map (fun (verifierName, passed, feedback) ->
                            let elName = verifierName.ToLowerInvariant().Replace("-", "_")
                            let suffix = if passed then "" else " [FAILED]"
                            xt elName (feedback + suffix))
                    let exchanges = [xt "out" h.AgentOutput] @ verifierPart
                    [xac ("i" + string h.Iteration) [] exchanges]
                )
            Some (xc "history" (skippedNote @ items))
    
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
    
    let buildArbiter (originalRequest: string) (failedSprint: FailedSprintContext option) (completedSprints: SprintHistory list) (pendingSprints: (int * string * string) list) =
        
        let completedSprintsEl = 
            if completedSprints.IsEmpty then [xt "none" "No sprints completed yet"]
            else completedSprints |> List.map (fun s -> 
                xat "done" [("order", string s.SprintId)] $"{s.SprintName}: {s.Summary}")
        
        let pendingSprintsEl = 
            if pendingSprints.IsEmpty then [xt "none" "No pending sprints"]
            else pendingSprints |> List.map (fun (order, name, path) -> 
                xac "pending" [("order", string order); ("path", path)] [xt "name" name])
        
        let failureContextEl = 
            match failedSprint with
            | None -> [xt "failed_at" "Pre-sprint (planning phase)"]
            | Some ctx ->
                let verifierIssues = 
                    ctx.VerifierFailureCounts 
                    |> Map.toList 
                    |> List.sortByDescending snd
                    |> List.map (fun (name, count) -> 
                        xat "verifier_issue" [("name", name); ("failures", string count)] 
                            $"{name} failed {count} times")
                
                let lastIterationsEl =
                    ctx.LastIterations 
                    |> List.map (fun h ->
                        let verifiers = h.VerifierResults |> List.map (fun (name, passed, summary) ->
                            let passedStr = if passed then "true" else "false"
                            xat name [("passed", passedStr)] summary)
                        xac "iteration" [("n", string h.Iteration)] (
                            [xt "agent_output" (if h.AgentOutput.Length > 2000 then h.AgentOutput.Substring(0, 2000) + "..." else h.AgentOutput)] 
                            @ verifiers))
                
                [
                    xc "failed_sprint" [
                        xat "info" [("order", string ctx.SprintOrder); ("path", ctx.SprintFilePath)] ctx.SprintName
                        xt "iterations_spent" (string ctx.IterationsSpent)
                    ]
                    xc "why_it_failed" verifierIssues
                    xc "last_iterations" lastIterationsEl
                ]
        
        xc "R" [
            roleElement Arbiter
            xt "original_request" originalRequest
            
            xc "FIRST_READ_THESE" [
                xat "backlog" [("path", Config.backlogFile)] "READ THIS FIRST - contains original plan, analysis, and approach"
                xat "failed_sprint" [("path", match failedSprint with Some s -> s.SprintFilePath | None -> "N/A")] "The sprint that failed - understand what was attempted"
            ]
            
            xc "system_context" [
                xt "sprints_dir" Config.sprintsDir
                xt "template" Config.templateFile
            ]
            
            xc "sprint_status" [
                xc "completed_DO_NOT_TOUCH" completedSprintsEl
                xc "remaining_CAN_MODIFY" pendingSprintsEl
            ]
            
            xc "failure_context" failureContextEl
            
            xc "your_analysis_steps" [
                xt "step1" "READ BACKLOG.md to understand the ORIGINAL PLAN and approach"
                xt "step2" "READ the failed sprint file to see what was attempted"
                xt "step3" "ANALYZE the iteration history - what did the agent try? What did verifiers reject?"
                xt "step4" "IDENTIFY the root cause - is the sprint too ambitious? Missing context? Wrong approach? Original plan flawed?"
                xt "step5" "DECIDE: split into smaller sprints? Add missing context? Change approach? Update BACKLOG.md if plan was wrong?"
            ]
            
            xc "your_powers" [
                xt "power" "DELETE any remaining sprint file"
                xt "power" "CREATE new sprint files (use higher numbers: 10_, 11_, etc.)"
                xt "power" "MODIFY remaining sprint files to add missing context or simplify"
                xt "power" "Update BACKLOG.md notes"
            ]
            
            xc "critical_rules" [
                xt "rule" "Each new/modified sprint must be SELF-CONTAINED with ALL context"
                xt "rule" "Include specific guidance based on what went wrong"
                xt "rule" "If verifier X kept failing, address that specifically in the new sprint"
                xt "rule" "DoD format: each item on its own line starting with '- '"
            ]
            
            xt "when_done" "Output: ARBITER_COMPLETE"
        ]
    
    let toPrompt (el: XElement) = el.ToString()

module Prompts =
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
                xt "rule" "Each sprint file is SELF-CONTAINED. Include ALL context in the file itself."
                xt "rule" "Implementor has NO knowledge of the codebase except what YOU tell them in the sprint file."
                xt "rule" "Include: file paths, function names, code patterns to follow, examples."
                xt "rule" "Each sprint must be INDEPENDENTLY TESTABLE - include tests in same sprint, never separate."
                xt "rule" "Definition of Done items must be CONCRETE: 'Tests pass' not 'Code is good'."
            ]
            
            xc "dod_format" [
                xt "format" "Each criterion on its own line starting with '- ' (dash space)"
                xt "example" "- Build succeeds with no warnings\n- Function X returns correct value for input Y\n- Tests pass locally"
                xt "bad" "Do NOT use '- [ ]' checkboxes. Just '- text'."
            ]
            
            xt "when_done" "Output: PLAN_COMPLETE"
        ] |> XmlPrompt.toPrompt

    /// Architect with CI failure context - sprints already exist, CI failed
    let architectWithCIContext request (ciFailureOutput: string) = 
        let templatePath = Config.templateFile
        let sprintsDir = Config.sprintsDir
        let backlogPath = Config.backlogFile
        xc "R" [
            xt "role" "ARCHITECT. Previous implementation passed local verification but FAILED CI. Your job: fix the sprint plan."
            xt "request" request
            
            xc "situation" [
                xt "fact" "Previous sprints were implemented and passed ALL local verifiers."
                xt "fact" "Code was pushed but CI (continuous integration) FAILED."
                xt "fact" "Existing sprint files are in the sprints directory."
                xt "fact" "BACKLOG.md contains the original plan."
            ]
            
            xc "ci_failure_output" [
                xt "raw" ciFailureOutput
            ]
            
            xc "your_options" [
                xt "option1" "CREATE new fixup sprint(s) to address CI failures specifically"
                xt "option2" "MODIFY existing sprint files if the original approach was flawed"
                xt "option3" "REPLACE a sprint entirely if needed"
                xt "tip" "Often a single targeted fixup sprint (e.g., 99_CI_Fixup.md) is sufficient"
            ]
            
            xc "locations" [
                xt "backlog" backlogPath
                xt "sprints_dir" sprintsDir
                xt "template" templatePath
            ]
            
            xc "analyze_first" [
                xt "step1" "Read BACKLOG.md to understand the feature"
                xt "step2" "Read existing sprint files to see what was implemented"
                xt "step3" "Analyze CI failure output to identify root causes"
                xt "step4" "Decide: new fixup sprint vs modify existing"
            ]
            
            xc "sprint_file_format" [
                xt "line1" "---"
                xt "line2" "---"
                xt "required" "# Sprint: [title]"
                xt "required" "## Context - WHY this sprint exists (mention CI failure!)"
                xt "required" "## Description - WHAT to fix with DETAILED guidance"
                xt "required" "## Definition of Done - bullet list starting with '- '"
            ]
            
            xc "critical_rules" [
                xt "rule1" "Each sprint file is SELF-CONTAINED. Include ALL context."
                xt "rule2" "For CI fixes, reference the SPECIFIC errors in the sprint file."
                xt "rule3" "Implementor has NO knowledge of CI output unless you include it."
                xt "rule4" "Include: exact error messages, file paths, what to change."
            ]
            
            xt "when_done" "Output: PLAN_COMPLETE"
        ] |> XmlPrompt.toPrompt

    /// Architect for restart - learn from previous failed run and clean up
    let architectRestart (originalRequest: string) (sprintsSummary: string) (logContent: string) (gitDiff: string) = 
        let templatePath = Config.templateFile
        let sprintsDir = Config.sprintsDir
        let backlogPath = Config.backlogFile
        xc "R" [
            xt "role" "ARCHITECT. Previous run CRASHED or FAILED. Your job: learn from the failure and create a NEW plan."
            xt "request" originalRequest
            
            xc "situation" [
                xt "fact" "A previous attempt to complete this request failed or crashed midway."
                xt "fact" "You have access to: sprint files from previous run, execution log, git diff of changes made."
                xt "fact" "Your job is to LEARN from what went wrong and create a BETTER plan."
                xt "instruction" "After analyzing, DELETE old sprint files and create new ones."
            ]
            
            xc "previous_sprints" [
                xt "files" sprintsSummary
            ]
            
            xc "execution_log" [
                xt "log" (if logContent.Length > 10000 then logContent.Substring(logContent.Length - 10000) else logContent)
            ]
            
            xc "changes_made" [
                xt "git_diff" (if gitDiff.Length > 15000 then gitDiff.Substring(0, 15000) + "\n... (truncated)" else gitDiff)
            ]
            
            xc "your_task" [
                xt "step1" "Analyze the log to understand what went wrong (verifier failures, errors, loops)"
                xt "step2" "Analyze the git diff to see what was partially implemented"
                xt "step3" "Decide: continue from where it left off, OR start fresh with different approach"
                xt "step4" "DELETE all files in the sprints directory using rm command"
                xt "step5" "Create NEW sprint files with improved approach"
                xt "step6" "Update BACKLOG.md with lessons learned"
            ]
            
            xc "locations" [
                xt "backlog" backlogPath
                xt "sprints_dir" sprintsDir
                xt "template" templatePath
            ]
            
            xc "sprint_file_format" [
                xt "line1" "---"
                xt "line2" "---"
                xt "required" "# Sprint: [title]"
                xt "required" "## Context - Include lessons from failed attempt"
                xt "required" "## Description - WHAT to implement"
                xt "required" "## Definition of Done - bullet list starting with '- '"
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

    let arbiter 
        (originalRequest: string) 
        (failedSprintContext: XmlPrompt.FailedSprintContext option)
        (completedSprints: XmlPrompt.SprintHistory list)
        (pendingSprints: (int * string * string) list) =  // (order, name, filePath)
        XmlPrompt.buildArbiter originalRequest failedSprintContext completedSprints pendingSprints
        |> XmlPrompt.toPrompt

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
let buildSprintVerificationContext (sprintItem: BacklogItem) (approvedCommits: Map<string, string>) (currentCommit: string option) (currentVerifier: string) =
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
