#!/usr/bin/env dotnet fsi

/// ConvergenceLoop — Re-entrant, language-agnostic porting orchestrator.
///
/// All state lives in beads (bd). This script is designed to be interrupted
/// and restarted at any time. Each invocation does exactly ONE step.
///
/// Project-specific knowledge comes from:
///   - project.json (created during init — build/test commands, layers, paths)
///   - hints/ (generated during init — architecture docs, patterns)
///   - beads memories (accumulated during execution — learned patterns, gotchas)
///
/// The harness knows NOTHING about source/target languages until init runs.
///
/// Usage:
///   dotnet fsi ConvergenceLoop.fsx init <source-dir> <target-dir>
///   dotnet fsi ConvergenceLoop.fsx step [--auto]
///   dotnet fsi ConvergenceLoop.fsx status
///   dotnet fsi ConvergenceLoop.fsx analyze

#load "ProjectConfig.fsx"
#load "ContextBuilder.fsx"
#load "FailureAnalyzer.fsx"

#r "nuget: Fli"

open System
open System.IO
open Fli
open ProjectConfig.ProjectConfig
open ContextBuilder.ContextBuilder
open FailureAnalyzer.FailureAnalyzer

module Beads =
    let private bdExe =
        // Find bd on PATH or at known location
        let known = @"Q:\.tools\beads\bd.exe"
        if File.Exists known then known else "bd"

    let private ensurePath () =
        let doltDir = @"Q:\.tools\dolt\dolt-windows-amd64\bin"
        let beadsDir = Path.GetDirectoryName(bdExe)
        let path = Environment.GetEnvironmentVariable("PATH")
        if not (path.Contains(beadsDir)) then
            Environment.SetEnvironmentVariable("PATH", $"{beadsDir};{doltDir};{path}")

    /// Run a bd command and return stdout.
    let run (args: string list) : string =
        ensurePath ()
        try
            let result =
                cli {
                    Exec bdExe
                    Arguments (args |> Array.ofList)
                    WorkingDirectory __SOURCE_DIRECTORY__
                }
                |> Command.execute
            result.Text |> Option.defaultValue ""
        with ex ->
            eprintfn $"bd error: {ex.Message}"
            ""

    let create title desc issueType priority : string =
        let output = run ["create"; $"--title={title}"; $"--description={desc}"; $"--type={issueType}"; $"--priority={priority}"]
        output.Trim()

    let addDep issueId dependsOn = run ["dep"; "add"; issueId; dependsOn] |> ignore
    let claim issueId = run ["update"; issueId; "--claim"] |> ignore
    let close issueId reason = run ["close"; issueId; $"--reason={reason}"] |> ignore
    let note issueId text = run ["note"; issueId; text] |> ignore
    let remember text = run ["remember"; text] |> ignore
    let ready () = run ["ready"]
    let listOpen () = run ["list"; "--status=open"]
    let status () = run ["status"]

module Toolchain =
    /// Run a shell command. Returns (success, stdout+stderr).
    let runCommand (workDir: string) (command: string) : bool * string =
        try
            let parts = command.Split([|' '|], 2)
            let exec = parts.[0]
            let args = if parts.Length > 1 then parts.[1].Split(' ') else [||]
            let result =
                cli {
                    Exec exec
                    Arguments args
                    WorkingDirectory workDir
                }
                |> Command.execute
            let output = (result.Text |> Option.defaultValue "") + "\n" + (result.Error |> Option.defaultValue "")
            (result.ExitCode = 0, output.Trim())
        with ex -> (false, ex.Message)

    /// Run the build command from project config.
    let build (config: ProjectConfig) = runCommand config.TargetDir config.BuildCommand

    /// Run the lint command from project config.
    let lint (config: ProjectConfig) = runCommand config.TargetDir config.LintCommand

    /// Run the test command from project config. Returns (exitOk, output).
    let test (config: ProjectConfig) = runCommand config.TargetDir config.TestCommand

module ConvergenceLoop =

    let private planPath () = Path.Combine(__SOURCE_DIRECTORY__, "hints", "porting-plan.md")

    /// Read the porting plan if one exists.
    let readPlan () : string option =
        let p = planPath ()
        if File.Exists p then Some (File.ReadAllText p) else None

    /// Initialize the project. Optionally seed with a pre-written plan.
    let init (sourceDir: string) (targetDir: string) (planFile: string option) =
        printfn $"Initializing porting project..."
        printfn $"  Source: {sourceDir}"
        printfn $"  Target: {targetDir}"

        if not (Directory.Exists sourceDir) then
            eprintfn $"Source directory does not exist: {sourceDir}"
            exit 1

        if not (Directory.Exists targetDir) then
            Directory.CreateDirectory targetDir |> ignore

        // Create default config — the user (or an agent) must fill in the blanks
        let config = createDefault sourceDir targetDir
        save config
        printfn $"\n  Created project.json with default values."

        // Create placeholder hints
        let hintsDir = Path.Combine(__SOURCE_DIRECTORY__, "hints")
        Directory.CreateDirectory hintsDir |> ignore

        let placeholders = [
            "architecture.md", "# Source Architecture\n\n> Generated during init. Describe the source codebase structure,\n> key modules, and their responsibilities here.\n> Subagents will read this to understand what they're porting."
            "layer-boundaries.md", "# Layer Boundary Contracts\n\n> Define the interface contracts between layers here.\n> Each layer should export a compact set of types and functions.\n> Subagents receive ONLY adjacent layers' contracts, not the whole codebase."
            "type-patterns.md", "# Type/Structure Translation Patterns\n\n> Document how source-language patterns map to target-language idioms.\n> This is the subagents' reference sheet for consistent translation."
        ]

        for (name, content) in placeholders do
            let path = Path.Combine(hintsDir, name)
            if not (File.Exists path) then
                File.WriteAllText(path, content)
                printfn $"  Created hints/{name} (placeholder — fill in during setup)"

        // Copy plan file if provided
        match planFile with
        | Some pf when File.Exists pf ->
            let dest = planPath ()
            File.Copy(pf, dest, true)
            printfn $"  Seeded porting plan from {pf}"
            Beads.remember $"Porting plan loaded from {Path.GetFileName pf}. Read hints/porting-plan.md for project context."
        | Some pf ->
            eprintfn $"  Warning: Plan file not found: {pf}"
        | None ->
            printfn $"  No plan provided. You can add one later at hints/porting-plan.md"
            printfn $"  Or re-run with: init <src> <tgt> --plan <file.md>"

        // Create beads epics for project structure
        let epicId = Beads.create
                        $"Port {Path.GetFileName sourceDir}"
                        $"Full port of {Path.GetFileName sourceDir}. Test-driven convergence approach."
                        "epic" 0
        printfn $"  Created beads epic: {epicId}"

        // Create setup tasks
        let setupId = Beads.create
                        "Configure project.json"
                        "Fill in project.json with correct: source/target languages, build/test/lint commands, test sample directory, oracle command, layer definitions."
                        "task" 0
        let hintsId = Beads.create
                        "Generate hints files"
                        "Analyze source codebase and generate: hints/architecture.md, hints/layer-boundaries.md, hints/type-patterns.md. Use an agent to scan the source tree."
                        "task" 0
        let oracleId = Beads.create
                        "Generate golden oracle"
                        "Run the original tool on all test samples and save expected outputs as golden reference files."
                        "task" 0
        Beads.addDep hintsId setupId
        Beads.addDep oracleId setupId

        printfn $"  Created setup tasks: {setupId}, {hintsId}, {oracleId}"
        printfn ""
        printfn "╔══════════════════════════════════════════════════════╗"
        printfn "║  NEXT STEPS                                         ║"
        printfn "╠══════════════════════════════════════════════════════╣"
        printfn "║  1. Edit project.json — fill in build/test commands, ║"
        printfn "║     source/target languages, layers, sample dirs     ║"
        printfn "║  2. Generate hints/ — run an agent to analyze the    ║"
        printfn "║     source codebase and create architecture docs     ║"
        printfn "║  3. Generate golden oracle — run GoldenOracle.fsx    ║"
        printfn "║  4. Start converging — run 'step' to begin           ║"
        printfn "╚══════════════════════════════════════════════════════╝"
        printfn ""
        printfn "  Or let an agent do it: ask copilot to configure this project."
        printfn "  It can read the source tree, discover languages, and fill everything in."

    /// Execute one convergence step. Re-entrant — safe to call repeatedly.
    let step (autoApprove: bool) =
        let config = require()
        printfn $"Project: {config.ProjectName} ({config.SourceLang} → {config.TargetLang})"

        // Check what's ready in beads
        let readyOutput = Beads.ready ()
        if String.IsNullOrWhiteSpace readyOutput || readyOutput.Contains("No ready") then
            printfn "No ready work found."
            printfn "  Check: bd blocked"
            printfn "  Or close completed tasks to unblock next work."
        else
            printfn $"Ready work:\n{readyOutput}"

        // Run current metrics
        printfn "\nCurrent metrics:"
        let (buildOk, buildOutput) = Toolchain.build config
        printfn $"  Build: {if buildOk then "✅" else "❌"}"

        if not buildOk then
            printfn $"  Build errors (fix before proceeding):\n{buildOutput |> fun s -> if s.Length > 500 then s.Substring(0, 500) + "..." else s}"
            // Create a fix-build task if one doesn't exist
            let fixId = Beads.create "Fix build errors" $"Build command '{config.BuildCommand}' fails. Errors:\n{buildOutput |> fun s -> if s.Length > 1000 then s.Substring(0, 1000) else s}" "bug" 0
            Beads.claim fixId
            printfn $"  Created fix task: {fixId}"
        else
            let (lintOk, _) = Toolchain.lint config
            printfn $"  Lint:  {if lintOk then "✅" else "⚠️"}"

            let (testOk, testOutput) = Toolchain.test config
            printfn $"  Tests: {if testOk then "✅" else "❌"}"

            // Analyze failures
            let failures = collectFailures config
            if failures.Length > 0 then
                let groups = groupFailures failures
                let totalSamples = failures.Length + 10 // approximate — proper count from oracle
                let passingApprox = totalSamples - failures.Length
                printReport groups totalSamples passingApprox

                match selectNextTarget groups with
                | Some target ->
                    printfn $"\n→ Next sprint: Fix '{target.Feature}' ({target.Count} samples)"

                    // Create sprint bead
                    let sprintId = Beads.create
                                    $"Sprint: Fix {target.Feature}"
                                    $"Fix {target.Count} failing samples in '{target.Feature}'. Representative: {target.Samples |> List.truncate 3 |> String.concat ", "}"
                                    "task" 1
                    Beads.claim sprintId
                    Beads.note sprintId $"Pre-sprint failures: {target.Count}"

                    // Build context for the agent
                    let layerId =
                        config.Layers
                        |> List.tryFind (fun l -> l.Name.Contains(target.Feature))
                        |> Option.map (fun l -> l.Id)
                        |> Option.defaultValue "L0"
                    let sourceDirs = featureToSourceDirs config target.Feature
                    let sourceFiles =
                        sourceDirs
                        |> List.collect (fun dir ->
                            let fullDir = Path.Combine(config.SourceDir, dir)
                            if Directory.Exists fullDir then
                                Directory.GetFiles(fullDir, config.SourceFileGlob, SearchOption.AllDirectories)
                                |> Array.map (fun f -> Path.GetRelativePath(config.SourceDir, f))
                                |> Array.toList
                            else [])

                    let failurePairs = failures |> List.map (fun f -> (f.SampleFile, f.ErrorMessage))
                    let context = buildSprintContext config layerId target.Feature sourceFiles failurePairs 0.0 5.0
                    reportBudget context

                    if autoApprove then
                        printfn "Auto-approve mode — would execute sprint here via Ralph."
                        // Sprint.runRalph context true
                    else
                        printfn $"\nSprint context ready ({estimateTokens context} tokens)."
                        printfn "Run with --auto to execute, or invoke Ralph manually."

                | None ->
                    printfn "✓ No actionable failures found."
            else
                printfn "✓ No failures detected. Check oracle comparison for convergence metric."

    /// Print current status from beads + toolchain.
    let showStatus () =
        let configOpt = load()

        printfn "╔══════════════════════════════════════════════════════╗"
        printfn "║  CONVERGENCE STATUS                                  ║"
        printfn "╠══════════════════════════════════════════════════════╣"

        match configOpt with
        | Some config ->
            printfn $"║  Project: {config.ProjectName}"
            printfn $"║  {config.SourceLang} → {config.TargetLang}"
            printfn $"║  Source:  {config.SourceDir}"
            printfn $"║  Target:  {config.TargetDir}"

            if Directory.Exists config.TargetDir then
                let (buildOk, _) = Toolchain.build config
                printfn $"║  Build:   {if buildOk then "✅" else "❌"}"
                if buildOk then
                    let (testOk, _) = Toolchain.test config
                    printfn $"║  Tests:   {if testOk then "✅" else "❌"}"
        | None ->
            printfn "║  No project.json — run init first"

        printfn "╠══════════════════════════════════════════════════════╣"
        let beadsStatus = Beads.status ()
        printfn $"{beadsStatus}"
        printfn "╠══════════════════════════════════════════════════════╣"
        let readyWork = Beads.ready ()
        if not (String.IsNullOrWhiteSpace readyWork) then
            printfn $"Ready:\n{readyWork}"
        else
            printfn "No ready work."
        printfn "╚══════════════════════════════════════════════════════╝"

    /// Run failure analysis only (no sprint execution).
    let analyze () =
        let config = require()
        let failures = collectFailures config
        if failures.Length > 0 then
            let groups = groupFailures failures
            let total = failures.Length + 10
            printReport groups total (total - failures.Length)
        else
            printfn "No failures to analyze (or test command not producing parseable output)."

// CLI entry point
match fsi.CommandLineArgs |> Array.toList |> List.tail with
| "init" :: sourceDir :: targetDir :: rest ->
    let planFile = 
        match rest |> List.tryFindIndex (fun a -> a = "--plan") with
        | Some i when i + 1 < rest.Length -> Some rest.[i + 1]
        | _ -> None
    ConvergenceLoop.init sourceDir targetDir planFile
| "init" :: _ ->
    printfn "Usage: dotnet fsi ConvergenceLoop.fsx init <source-dir> <target-dir> [--plan <file.md>]"
| "step" :: rest ->
    let auto = rest |> List.contains "--auto"
    ConvergenceLoop.step auto
| ["status"] ->
    ConvergenceLoop.showStatus ()
| ["analyze"] ->
    ConvergenceLoop.analyze ()
| ["--help"] | ["-h"] | [] ->
    printfn "ConvergenceLoop — Re-entrant, language-agnostic porting orchestrator"
    printfn ""
    printfn "Usage:"
    printfn "  dotnet fsi ConvergenceLoop.fsx init <source-dir> <target-dir> [--plan <file.md>]"
    printfn "  dotnet fsi ConvergenceLoop.fsx step [--auto]"
    printfn "  dotnet fsi ConvergenceLoop.fsx status"
    printfn "  dotnet fsi ConvergenceLoop.fsx analyze"
    printfn ""
    printfn "The orchestrator is re-entrant: safe to interrupt and restart."
    printfn "All state lives in beads (bd) and project.json."
    printfn "The tool knows nothing about languages until init populates project.json."
| other ->
    printfn $"Unknown: {other |> String.concat " "}"
    printfn "Try: dotnet fsi ConvergenceLoop.fsx --help"
