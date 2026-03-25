#!/usr/bin/env dotnet fsi

/// ConvergenceLoop — Re-entrant, language-agnostic porting orchestrator.
///
/// INFORMATION FLOW:
///   Code changes       → git commits
///   Decisions/learnings → adr/ folder (.md files with index) + .github/copilot-instructions.md
///   Subtask progress   → beads (bd)
///   Test pass/fail     → per-sprint SQLite DBs (TestResultsDb)
///   Agent skills       → .github/skills/ (agents auto-discover these)
///
/// MAIN LOOP (each invocation = one step):
///   1. Receive context: test DB briefing, previous sprint summary, overall plan
///   2. Implementor: try improve (fresh session with briefing, or --resume from verifier)
///   3. Recalculate stats: re-run tests, update DB, HARD GATE on regression
///   4. Code quality review: verifier agent (read-only, plans actions for implementor)
///   5. Code dedup/engineering: verifier agent (read-only, plans actions)
///   6. Semantic search / rot prevention: verifier agent (read-only, plans actions)
///   If any verifier fails → resume implementor session with feedback, go to step 2
///
/// VERIFIER CONTRACT:
///   Verifiers do NOT make code changes. They produce a verdict + action plan.
///   On failure: the implementor is resumed (--resume sessionId) with verifier feedback.
///   On pass: next verifier runs, or sprint completes.
///
/// SESSION MANAGEMENT:
///   Fresh start: implementor gets briefing pack (plan, test DB, learnings, context)
///   Resume: implementor gets verifier feedback only (briefing was already in context)

#load "ProjectConfig.fsx"
#load "TestResultsDb.fsx"

#r "nuget: Fli"

open System
open System.IO
open Fli
open ProjectConfig.ProjectConfig
open TestResultsDb.TestResultsDb

// ═════════════════════════════════════════════════════════════════════════════
// Beads — task tracking
// ═════════════════════════════════════════════════════════════════════════════

module Beads =
    let private findBd () =
        let known = @"Q:\.tools\beads\bd.exe"
        if File.Exists known then known else "bd"

    let private ensurePath () =
        let path = Environment.GetEnvironmentVariable("PATH")
        for d in [@"Q:\.tools\beads"; @"Q:\.tools\dolt\dolt-windows-amd64\bin"] do
            if not (path.Contains(d)) then
                Environment.SetEnvironmentVariable("PATH", $"{d};{path}")

    let run (args: string list) : string =
        ensurePath ()
        try
            let result =
                cli { Exec (findBd()); Arguments (args |> Array.ofList); WorkingDirectory __SOURCE_DIRECTORY__ }
                |> Command.execute
            result.Text |> Option.defaultValue ""
        with ex -> eprintfn $"bd: {ex.Message}"; ""

    let create title desc itype prio = run ["create"; $"--title={title}"; $"--description={desc}"; $"--type={itype}"; $"--priority={prio}"] |> fun s -> s.Trim()
    let addDep a b = run ["dep"; "add"; a; b] |> ignore
    let claim id = run ["update"; id; "--claim"] |> ignore
    let close id reason = run ["close"; id; $"--reason={reason}"] |> ignore
    let note id text = run ["note"; id; text] |> ignore
    let remember text = run ["remember"; text] |> ignore
    let ready () = run ["ready"]
    let listOpen () = run ["list"; "--status=open"]
    let status () = run ["status"]

// ═════════════════════════════════════════════════════════════════════════════
// Toolchain — run build/test commands from project.json
// ═════════════════════════════════════════════════════════════════════════════

module Toolchain =
    let runCmd (workDir: string) (command: string) : bool * string =
        try
            let parts = command.Split([|' '|], 2)
            let result =
                cli { Exec parts.[0]; Arguments (if parts.Length > 1 then parts.[1].Split(' ') else [||]); WorkingDirectory workDir }
                |> Command.execute
            let out = (result.Text |> Option.defaultValue "") + "\n" + (result.Error |> Option.defaultValue "")
            (result.ExitCode = 0, out.Trim())
        with ex -> (false, ex.Message)

    let build (c: ProjectConfig) = runCmd c.TargetDir c.BuildCommand
    let lint  (c: ProjectConfig) = runCmd c.TargetDir c.LintCommand
    let test  (c: ProjectConfig) = runCmd c.TargetDir c.TestCommand

// ═════════════════════════════════════════════════════════════════════════════
// ADR / Skills / Learnings — shared knowledge channels
// ═════════════════════════════════════════════════════════════════════════════

module Knowledge =
    let private adrDir (config: ProjectConfig) = Path.Combine(config.TargetDir, "adr")
    let private skillsDir (config: ProjectConfig) = Path.Combine(config.TargetDir, ".github", "skills")
    let private instructionsFile (config: ProjectConfig) = Path.Combine(config.TargetDir, ".github", "copilot-instructions.md")

    /// Ensure knowledge directories exist.
    let ensureDirs (config: ProjectConfig) =
        Directory.CreateDirectory(adrDir config) |> ignore
        Directory.CreateDirectory(skillsDir config) |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName(instructionsFile config)) |> ignore

    /// Read the ADR index (compact, token-efficient routing file).
    let readAdrIndex (config: ProjectConfig) : string =
        let indexPath = Path.Combine(adrDir config, "INDEX.md")
        if File.Exists indexPath then File.ReadAllText indexPath
        else "(No ADR index yet. Agents: create adr/INDEX.md listing decision records.)"

    /// Read a specific ADR by name.
    let readAdr (config: ProjectConfig) (name: string) : string option =
        let path = Path.Combine(adrDir config, name)
        if File.Exists path then Some (File.ReadAllText path) else None

    /// Read all skills (agents auto-discover these).
    let readSkills (config: ProjectConfig) : string =
        let dir = skillsDir config
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.md")
            |> Array.map (fun f -> $"### Skill: {Path.GetFileNameWithoutExtension f}\n{File.ReadAllText f}")
            |> String.concat "\n\n"
        else ""

    /// Read copilot instructions.
    let readInstructions (config: ProjectConfig) : string =
        let path = instructionsFile config
        if File.Exists path then File.ReadAllText path else ""

    /// Build the "learnings" section for agent context.
    /// Only non-trivial, deep, repo-specific decisions — NOT basic type mappings.
    let buildLearningsContext (config: ProjectConfig) : string =
        let adrIndex = readAdrIndex config
        let skills = readSkills config
        let instructions = readInstructions config
        String.concat "\n\n" [
            if not (String.IsNullOrWhiteSpace instructions) then
                "<copilot_instructions>"
                instructions
                "</copilot_instructions>"
            if not (String.IsNullOrWhiteSpace adrIndex) then
                "<adr_index>"
                adrIndex
                "</adr_index>"
            if not (String.IsNullOrWhiteSpace skills) then
                "<skills>"
                skills
                "</skills>"
        ]

    /// Guidance for agents on HOW to write ADRs and skills.
    let writingGuidance () : string = """
<knowledge_sharing>
You can share learnings with future sprints via these channels:

1. **ADR (Architecture Decision Records)** — for non-trivial design decisions:
   - Create files in `adr/NNNN-title.md` (NNNN = sequential number)
   - Update `adr/INDEX.md` with a one-line summary + file pointer
   - Example: "0003-sum-type-pattern.md" documenting the chosen union type approach
   - Only write ADRs for DEEP decisions, not obvious translations

2. **Skills** — for reusable agent techniques:
   - Create files in `.github/skills/skill-name.md`
   - Agents auto-discover these. Keep them actionable and concise.
   - Example: "error-wrapping.md" on the project's error handling convention

3. **Copilot instructions** — for global project context:
   - Edit `.github/copilot-instructions.md`
   - This is read by ALL copilot sessions in this repo
   - Keep it compact — overall project context, not per-sprint details

DO NOT store trivial mappings (number→int). Store DEEP insights:
- Discovered circular dependency requiring specific initialization order
- Non-obvious semantic difference between source and target behavior
- Performance-critical path requiring specific implementation pattern
</knowledge_sharing>"""

// ═════════════════════════════════════════════════════════════════════════════
// Briefing Pack — what the implementor receives on a FRESH start
// ═════════════════════════════════════════════════════════════════════════════

module BriefingPack =
    let private readHint (name: string) : string =
        let path = Path.Combine(__SOURCE_DIRECTORY__, "hints", name)
        if File.Exists path then File.ReadAllText path else ""

    /// Build the full briefing pack for a fresh implementor session.
    let build
        (config: ProjectConfig)
        (sprintNum: int)
        (targetBucket: string)
        (testDbBriefing: string)
        (failingSamples: (string * string) list)
        (totalTestCount: int)
        (alternativeBuckets: string list)
        : string =

        let plan = readHint "porting-plan.md"
        let learnings = Knowledge.buildLearningsContext config

        // Plan: truncate to ~3K tokens
        let planSection =
            if String.IsNullOrWhiteSpace plan then ""
            else
                let maxChars = 12_000
                let truncated = if plan.Length <= maxChars then plan else plan.Substring(0, maxChars) + "\n_(truncated)_"
                $"<porting_plan>\n{truncated}\n</porting_plan>"

        let failuresSection =
            let shown = failingSamples |> List.truncate 25
            let lines = shown |> List.map (fun (f, e) -> $"  {f}: {e}")
            String.concat "\n" [
                $"<failing_tests count=\"{failingSamples.Length}\" showing=\"{shown.Length}\">"
                yield! lines
                if failingSamples.Length > 25 then $"  ... and {failingSamples.Length - 25} more"
                "</failing_tests>"
            ]

        // Alternative buckets the agent CAN choose if it thinks it can make more progress elsewhere
        let altSection =
            if alternativeBuckets.IsEmpty then ""
            else
                let alts = alternativeBuckets |> List.map (fun b -> $"  - {b}") |> String.concat "\n"
                $"<alternative_buckets>\nSuggested: '{targetBucket}'. But you may pick a different one if you believe you can make more progress:\n{alts}\nExplain your choice.\n</alternative_buckets>"

        // The CONSTANT brief is implicit via .github/copilot-instructions.md (always loaded by copilot).
        // This is the DYNAMIC brief built from previous sprint state.
        String.concat "\n\n" [
            $"<sprint num=\"{sprintNum}\" bucket=\"{targetBucket}\">"
            $"Source dir: {config.SourceDir} | Target dir: {config.TargetDir}"
            "</sprint>"

            $"<test_summary total=\"{totalTestCount}\">\n{testDbBriefing}\n</test_summary>"

            "<guards>"
            $"Total test count must remain >= {totalTestCount}. Deleting tests to fake progress = instant fail."
            "Passing rate must not decrease. Regressions are hard-gated."
            "</guards>"

            planSection
            failuresSection
            altSection

            if not (String.IsNullOrWhiteSpace learnings) then learnings

            Knowledge.writingGuidance ()

            "<instructions>"
            "You are an implementor. Your goal: increase the passing test rate."
            $"Focus on bucket '{targetBucket}' (or pick from alternatives if you justify it)."
            "After making changes, run the build and test commands to verify."
            $"Build: {config.BuildCommand}"
            $"Test: {config.TestCommand}"
            "Commit your changes with a descriptive message."
            "If you discover a non-trivial insight, write an ADR or skill file."
            "</instructions>"
        ]

// ═════════════════════════════════════════════════════════════════════════════
// Agent Runner — run copilot agents with session management
// ═════════════════════════════════════════════════════════════════════════════

module Agent =
    /// Run a copilot agent. Returns (output, sessionId).
    let run (prompt: string) (title: string) (resumeSessionId: string option) : string * string =
        let sessionId = resumeSessionId |> Option.defaultWith (fun () -> Guid.NewGuid().ToString())
        try
            let baseArgs = [| "--allow-all-tools"; "--allow-all-paths"; "--no-ask-user"; "--no-color"; "--plain-diff"; "-s"; "--stream"; "off"; "--resume"; sessionId |]
            let escapedPrompt = prompt.Replace("{", "{{").Replace("}", "}}")
            let result =
                cli { Exec "copilot"; Arguments baseArgs; Input escapedPrompt }
                |> Command.execute
            let output = result.Text |> Option.defaultValue ""
            (output, sessionId)
        with ex ->
            eprintfn $"Agent '{title}' failed: {ex.Message}"
            ("", sessionId)

    /// Resume a session with follow-up (verifier feedback → implementor).
    let resume (sessionId: string) (feedback: string) (title: string) : string =
        let (output, _) = run feedback title (Some sessionId)
        output

// ═════════════════════════════════════════════════════════════════════════════
// Verifiers — read-only reviewers that plan actions for the implementor
// ═════════════════════════════════════════════════════════════════════════════

module Verifiers =
    let private verifiersDir = Path.Combine(__SOURCE_DIRECTORY__, "verifiers")

    /// Dynamically list all verifiers from the verifiers/ folder.
    /// Users can add/remove/rephrase .md files freely — the loop picks them all up.
    let listAll () : string list =
        if Directory.Exists verifiersDir then
            Directory.GetFiles(verifiersDir, "*.md")
            |> Array.map Path.GetFileNameWithoutExtension
            |> Array.sort
            |> Array.toList
        else []

    /// List only the "soft" verifiers (V05+) — the ones that run as LLM reviews.
    /// V01-V04 are handled as executable checks in the main loop.
    let listSoftVerifiers () : string list =
        listAll () |> List.filter (fun n -> not (n.StartsWith "V01") && not (n.StartsWith "V02") && not (n.StartsWith "V03") && not (n.StartsWith "V04"))

    let getPrompt (name: string) : string =
        let path = Path.Combine(verifiersDir, name + ".md")
        if File.Exists path then File.ReadAllText path else $"(verifier {name} not found)"

    let preamble = """
=== YOU ARE A VERIFIER AGENT ===
You REVIEW code. You do NOT make code changes.
Your job: assess quality and plan actions for the implementor.

- VERIFY_PASSED = work is acceptable, move on.
- VERIFY_FAILED = problems found. Your feedback becomes the implementor's next task.
  Write SPECIFIC, ACTIONABLE instructions the implementor can follow.
  "Fix the bug in X by doing Y" — not "there are some issues."

Output exactly one of VERIFY_PASSED or VERIFY_FAILED on its own line at the end.
"""

    /// Run a verifier agent. Returns (passed, feedback).
    let runVerifier (config: ProjectConfig) (verifierName: string) : bool * string =
        let prompt = String.concat "\n\n" [
            preamble
            getPrompt verifierName
            $"\nTarget directory: {config.TargetDir}"
            $"\nRun this to see the diff:"
            $"  cd {config.TargetDir} && git diff HEAD~1"
        ]
        let (output, _) = Agent.run prompt $"Verify-{verifierName}" None
        let passed = output.Contains("VERIFY_PASSED") && not (output.Contains("VERIFY_FAILED"))
        (passed, output)

// ═════════════════════════════════════════════════════════════════════════════
// Post-Sprint Knowledge Refinement
// ═════════════════════════════════════════════════════════════════════════════

module KnowledgeRefiner =
    /// After a successful sprint, invoke an agent to optionally capture learnings.
    /// Priority order (prefer improving existing over creating new):
    ///   1. Rephrase/amend an EXISTING skill — always compact it
    ///   2. Add a NEW skill (rare — only if no existing skill fits)
    ///   3. Add/amend a folder-scoped .instructions.md file (compact)
    ///   4. If it's a BIG architecture decision → write an ADR
    /// If nothing significant was learned, do nothing.
    let refine (config: ProjectConfig) (sprintNum: int) =
        let prompt = String.concat "\n" [
            $"You are a knowledge refinement agent. Sprint {sprintNum} just completed successfully."
            $"Working directory: {config.TargetDir}"
            ""
            "Review what was done this sprint and decide if any learnings should be captured."
            "Most sprints produce NO learnings worth capturing. Only act on genuinely non-trivial insights."
            ""
            "== YOUR OPTIONS (in priority order — prefer option 1 over 2, 2 over 3, etc.) =="
            ""
            "OPTION 1 (PREFERRED): Amend an EXISTING skill in .github/skills/"
            "  - Read existing skills: `ls .github/skills/`"
            "  - If this sprint's insight fits an existing skill, update it"
            "  - Always COMPACT when touching — remove fluff, tighten wording"
            "  - Follow https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices"
            ""
            "OPTION 2 (RARE): Create a NEW skill in .github/skills/<name>/SKILL.md"
            "  - Only if no existing skill covers this area"
            "  - Must have a clear trigger phrase (when does this skill activate?)"
            "  - YAML frontmatter: name (lowercase-hyphenated), description (third person, specific)"
            "  - Body: concise, under 200 lines. Progressive disclosure — link to reference files"
            "  - Follow https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices"
            ""
            "OPTION 3: Amend/create a folder-scoped .instructions.md in .github/instructions/"
            "  - Read existing: `ls .github/instructions/`"
            "  - These have `applyTo` globs — they load only for matching files"
            "  - Keep compact. Dedup against copilot-instructions.md and other instruction files"
            "  - Follow the Anthropic style guide for conciseness"
            ""
            "OPTION 4 (BIG decisions only): Write an ADR in adr/"
            $"  - Read the index: `cat {config.TargetDir}/adr/INDEX.md`"
            "  - Only for non-trivial ARCHITECTURE decisions that are impactful and worth recording"
            "  - Format: adr/NNNN-title.md with Status, Context, Decision, Consequences"
            "  - Update adr/INDEX.md with a one-line summary"
            ""
            "OPTION 5: Do nothing. Say 'No learnings.' and exit."
            "  This is the RIGHT choice for most sprints."
            ""
            "== RULES =="
            "- NEVER bloat. Every token must justify its cost."
            "- When amending, always leave the file SHORTER or same length, never longer (unless adding genuinely new content)"
            "- Do NOT capture obvious things (how to write a for loop, what types map to what)"
            "- DO capture: discovered gotchas, non-obvious behavioral differences, performance traps,"
            "  architectural constraints, circular dependency resolutions, import ordering requirements"
            ""
            "Start by reviewing: `git log --oneline -5` and `git diff HEAD~1 --stat`"
        ]
        let (output, _) = Agent.run prompt $"Knowledge-refine-S{sprintNum}" None
        if output.Length > 0 && not (output.Contains "No learnings") then
            printfn $"  📝 Knowledge captured after sprint {sprintNum}"

// ═════════════════════════════════════════════════════════════════════════════
// The Main Loop
// ═════════════════════════════════════════════════════════════════════════════

module ConvergenceLoop =
    let private planPath () = Path.Combine(__SOURCE_DIRECTORY__, "hints", "porting-plan.md")

    /// Initialize a new porting project.
    let init (sourceDir: string) (targetDir: string) (planFile: string option) =
        printfn $"Initializing porting project: {sourceDir} → {targetDir}"

        if not (Directory.Exists sourceDir) then eprintfn $"Source not found: {sourceDir}"; exit 1
        if not (Directory.Exists targetDir) then Directory.CreateDirectory targetDir |> ignore

        let config = createDefault sourceDir targetDir
        save config

        // Create hint placeholders
        let hintsDir = Path.Combine(__SOURCE_DIRECTORY__, "hints")
        Directory.CreateDirectory hintsDir |> ignore
        for (name, content) in [
            "architecture.md", "# Source Architecture\n\n> Describe source codebase structure for subagents."
            "layer-boundaries.md", "# Layer Boundary Contracts\n\n> Interface contracts between layers."
            "type-patterns.md", "# Translation Patterns\n\n> How source patterns map to target idioms." ] do
            let p = Path.Combine(hintsDir, name)
            if not (File.Exists p) then File.WriteAllText(p, content)

        // Copy plan if provided
        match planFile with
        | Some pf when File.Exists pf ->
            File.Copy(pf, planPath(), true)
            printfn $"  Seeded plan from {pf}"
        | Some pf -> eprintfn $"  Plan file not found: {pf}"
        | None -> printfn "  No plan. Add one at hints/porting-plan.md or re-run with --plan"

        // Create knowledge dirs in target
        Knowledge.ensureDirs config

        // Init test results DB
        let conn = initSchema (currentDbPath())
        setMeta conn "sprint" "0"
        setMeta conn "project" config.ProjectName
        conn.Close()

        // Create beads structure
        let epicId = Beads.create $"Port {Path.GetFileName sourceDir}" "Test-driven convergence porting." "epic" 0
        Beads.create "Configure project.json" "Fill in build/test commands, languages, layers, sample dirs." "task" 0 |> ignore
        Beads.create "Generate hints" "Analyze source, create architecture.md, layer-boundaries.md, type-patterns.md." "task" 0 |> ignore
        Beads.create "Generate golden oracle" "Run original tool on all samples, save expected outputs." "task" 0 |> ignore

        printfn $"\n  Created project.json, test DB, beads epic ({epicId})"
        printfn "  Next: fill in project.json, generate hints, then run 'step'"

    /// Execute one convergence step.
    ///
    /// Flow:
    ///   1. Read test DB → find top failing buckets (with fuzziness — agent can pick)
    ///   2. Build dynamic brief → run implementor (fresh session)
    ///   3. Re-run tests → update DB → check for regression (HARD GATE)
    ///   4. Run ALL verifiers from verifiers/ folder (dynamic) → if fail, resume implementor
    ///   5. Sprint passes → archive DB, update beads, refine instructions, exit
    let step (maxRetries: int) =
        let config = require()
        printfn $"Project: {config.ProjectName} ({config.SourceLang} → {config.TargetLang})"

        // 1. Read current test state
        let dbPath = currentDbPath()
        if not (File.Exists dbPath) then
            eprintfn "No test results DB. Run oracle population first."
            exit 1

        let conn = initSchema dbPath
        let sprintNum = getMeta conn "sprint" |> Option.map int |> Option.defaultValue 0
        let nextSprint = sprintNum + 1
        let (prevPassing, prevTotal) = passRate conn
        let prevPct = if prevTotal > 0 then float prevPassing / float prevTotal * 100.0 else 0.0

        printfn $"Sprint {nextSprint} | Current: {prevPassing}/{prevTotal} passing ({prevPct:F1}%%)"

        // Find top buckets — provide alternatives for fuzziness (anti-stuckness)
        let ranked = bucketsRanked conn
        match ranked with
        | [] ->
            printfn "✓ No failing buckets. All tests pass!"
            conn.Close()
        | (targetBucket, layer, failing, bucketTotal) :: rest ->
            let alternativeBuckets = rest |> List.truncate 4 |> List.map (fun (b, l, f, _) -> $"{b} ({l}, {f} failing)")
            printfn $"Suggested target: '{targetBucket}' ({layer}) — {failing}/{bucketTotal} failing"
            if alternativeBuckets.Length > 0 then
                printfn $"  Alternatives: {alternativeBuckets |> String.concat ", "}"

            let failures = failingInBucket conn targetBucket 30
            let testBriefing = briefing conn
            conn.Close()

            // 2. Build dynamic brief & run implementor
            // Constant brief = .github/copilot-instructions.md (implicit, always loaded by copilot)
            // Dynamic brief = test DB summary + failures + plan + alternatives
            let briefingText = BriefingPack.build config nextSprint targetBucket testBriefing failures prevTotal alternativeBuckets
            let sprintBeadId = Beads.create $"Sprint {nextSprint}: {targetBucket}" $"Fix {failing} failures in '{targetBucket}'." "task" 1
            Beads.claim sprintBeadId
            Beads.note sprintBeadId $"Pre: {prevPassing}/{prevTotal} ({prevPct:F1}%%)"

            printfn $"Running implementor (briefing: {briefingText.Length / 4} est. tokens)..."
            let (implOutput, sessionId) = Agent.run briefingText $"Impl-S{nextSprint}" None
            printfn $"Implementor done ({implOutput.Length} chars output)"

            // 3-6. Verify loop (with retries via session resume)
            let mutable retryCount = 0
            let mutable allPassed = false

            while retryCount < maxRetries && not allPassed do
                // 3. Re-run tests, update DB
                printfn $"\nRecalculating stats (attempt {retryCount + 1})..."
                let (buildOk, buildOut) = Toolchain.build config
                if not buildOk then
                    printfn $"❌ Build failed. Resuming implementor with errors..."
                    let feedback = $"BUILD FAILED. Fix these errors:\n{buildOut |> fun s -> if s.Length > 3000 then s.[..2999] else s}"
                    Agent.resume sessionId feedback $"Fix-build-S{nextSprint}" |> ignore
                    retryCount <- retryCount + 1
                else
                    let (testOk, testOut) = Toolchain.test config
                    // TODO: parse test output, update test DB
                    printfn $"  Build: ✅ | Tests: {if testOk then "✅" else "❌"}"

                    // Regression check
                    let newConn = initSchema dbPath
                    let (newPassing, newTotal) = passRate newConn
                    newConn.Close()

                    if newTotal < prevTotal then
                        printfn $"❌ REGRESSION: test count dropped {prevTotal}→{newTotal}. Reverting."
                        let feedback = $"REGRESSION DETECTED: Total tests dropped from {prevTotal} to {newTotal}. You deleted tests. Revert and fix properly."
                        Agent.resume sessionId feedback $"Fix-regression-S{nextSprint}" |> ignore
                        retryCount <- retryCount + 1
                    elif newPassing < prevPassing then
                        printfn $"❌ REGRESSION: passing dropped {prevPassing}→{newPassing}."
                        let feedback = $"REGRESSION: Passing tests dropped from {prevPassing} to {newPassing}. Fix without breaking what worked."
                        Agent.resume sessionId feedback $"Fix-regression-S{nextSprint}" |> ignore
                        retryCount <- retryCount + 1
                    else
                        // 4. Run ALL soft verifiers from verifiers/ folder (dynamic — add/remove .md files freely)
                        let softVerifiers = Verifiers.listSoftVerifiers ()
                        let mutable verifiersPassed = true

                        for vName in softVerifiers do
                            if verifiersPassed && retryCount < maxRetries then
                                printfn $"  Running {vName}..."
                                let (passed, feedback) = Verifiers.runVerifier config vName
                                if passed then
                                    printfn $"  ✅ {vName}"
                                else
                                    printfn $"  ❌ {vName} — resuming implementor with feedback"
                                    let truncFeedback = if feedback.Length > 4000 then feedback.[..3999] else feedback
                                    Agent.resume sessionId truncFeedback $"Fix-{vName}-S{nextSprint}" |> ignore
                                    verifiersPassed <- false
                                    retryCount <- retryCount + 1

                        if verifiersPassed then
                            allPassed <- true

            // Finalize
            let finalConn = initSchema dbPath
            let (finalPassing, finalTotal) = passRate finalConn
            let finalPct = if finalTotal > 0 then float finalPassing / float finalTotal * 100.0 else 0.0
            let delta = finalPassing - prevPassing
            setMeta finalConn "sprint" (string nextSprint)
            finalConn.Close()

            archiveAndReset nextSprint

            let resultMsg = $"Post: {finalPassing}/{finalTotal} ({finalPct:F1}%%), Δ={delta:+#;-#;0}, retries={retryCount}"
            Beads.note sprintBeadId resultMsg
            if allPassed then
                Beads.close sprintBeadId $"Done. {resultMsg}"
                printfn $"✅ Sprint {nextSprint} complete. {resultMsg}"

                // 5. Post-sprint: capture learnings (only on success)
                printfn "  Running knowledge refinement..."
                KnowledgeRefiner.refine config nextSprint
            else
                Beads.note sprintBeadId "Max retries reached"
                printfn $"⚠ Sprint {nextSprint} finished with retries exhausted. {resultMsg}"

            Beads.remember $"Sprint {nextSprint} ({targetBucket}): {resultMsg}"

    /// Show current status.
    let showStatus () =
        match load() with
        | Some config ->
            printfn $"╔══════════════════════════════════════════════════════╗"
            printfn $"║  {config.ProjectName}: {config.SourceLang} → {config.TargetLang}"
            let dbPath = currentDbPath()
            if File.Exists dbPath then
                let conn = initSchema dbPath
                let sprint = getMeta conn "sprint" |> Option.defaultValue "0"
                let brief = briefing conn
                conn.Close()
                printfn $"║  Sprint: {sprint}"
                printfn $"║  {brief}"
            printfn $"╠══════════════════════════════════════════════════════╣"
            printfn $"{Beads.status()}"
            let rdy = Beads.ready()
            if not (String.IsNullOrWhiteSpace rdy) then printfn $"Ready:\n{rdy}"
            printfn $"╚══════════════════════════════════════════════════════╝"
        | None -> printfn "No project.json. Run init first."

// CLI entry point
match fsi.CommandLineArgs |> Array.toList |> List.tail with
| "init" :: sourceDir :: targetDir :: rest ->
    let planFile = match rest |> List.tryFindIndex ((=) "--plan") with Some i when i+1 < rest.Length -> Some rest.[i+1] | _ -> None
    ConvergenceLoop.init sourceDir targetDir planFile
| "init" :: _ ->
    printfn "Usage: dotnet fsi ConvergenceLoop.fsx init <source-dir> <target-dir> [--plan <file.md>]"
| "step" :: rest ->
    let maxRetries = rest |> List.tryFind (fun s -> s.StartsWith("--retries=")) |> Option.map (fun s -> int (s.Split('=').[1])) |> Option.defaultValue 3
    ConvergenceLoop.step maxRetries
| ["status"] -> ConvergenceLoop.showStatus ()
| ["--help"] | ["-h"] | [] ->
    printfn "ConvergenceLoop — Re-entrant porting orchestrator"
    printfn ""
    printfn "  init <src> <tgt> [--plan file.md]   Initialize project"
    printfn "  step [--retries=N]                   Run one convergence step"
    printfn "  status                               Show current progress"
    printfn ""
    printfn "Info flow: code→git, decisions→adr/, progress→beads, tests→SQLite, skills→.github/skills/"
| other -> printfn $"Unknown: {other |> String.concat " "}. Try --help"
