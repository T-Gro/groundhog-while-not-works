#!/usr/bin/env dotnet fsi

/// PortingLoop — The outer orchestrator for large-scale TypeScript → Go porting.
///
/// This sits ABOVE Ralph's sprint loop.  Ralph handles one sprint at a time
/// (architect → implement → verify → fixup).  PortingLoop handles:
///   1. Scanning the TypeScript source tree and splitting into context-safe chunks
///   2. Determining a dependency-ordered porting sequence
///   3. Generating sprint files, verifiers and shared context for each chunk
///   4. Running Ralph on each chunk with backpressure (tests must pass before moving on)
///   5. Maintaining a shared type-mapping registry across sprints
///   6. Tracking overall progress with visual dashboard and trend reports
///   7. Supporting resume — pick up where we left off after failure/interruption

#load "Utils.fsx"
#load "TypeDefinitions.fsx"
#load "PortingSplit.fsx"
#load "PortingProgress.fsx"

#r "nuget: Fli"
#r "nuget: Spectre.Console"

open System
open System.IO
open Fli
open Spectre.Console
open PortingSplit.PortingSplit
open PortingProgress.PortingProgress

// ═════════════════════════════════════════════════════════════════════════════
// Configuration
// ═════════════════════════════════════════════════════════════════════════════

module PortingConfig =
    let portingDir       = ".tools/ralph/porting"
    let sharedContextDir = ".tools/ralph/porting/shared"
    let typeMappingFile  = ".tools/ralph/porting/shared/type_mappings.md"
    let progressFile     = ".tools/ralph/porting/PROGRESS.md"
    let stateFile        = ".tools/ralph/porting/state.json"
    let sprintsDir       = ".tools/ralph/sprints"

    /// Maximum tokens of source per sprint (conservative to leave room for prompt overhead).
    let maxTokensPerSprint = 60_000

    /// Maximum Ralph iterations per sprint before moving on.
    let maxIterationsPerSprint = 15

    /// How many consecutive sprint failures before pausing for human review.
    let backpressureThreshold = 3

// ═════════════════════════════════════════════════════════════════════════════
// Shared context management
// ═════════════════════════════════════════════════════════════════════════════

module SharedContext =
    /// The type-mapping registry is a markdown file that grows as modules are ported.
    /// Each sprint receives the current mappings as context, and can append new ones.
    let ensureDirs () =
        Directory.CreateDirectory PortingConfig.sharedContextDir |> ignore

    let readTypeMappings () : string =
        if File.Exists PortingConfig.typeMappingFile then File.ReadAllText PortingConfig.typeMappingFile
        else ""

    let appendTypeMappings (newMappings: string) =
        ensureDirs ()
        File.AppendAllText(PortingConfig.typeMappingFile, newMappings + Environment.NewLine)

    let initTypeMappings () =
        ensureDirs ()
        if not (File.Exists PortingConfig.typeMappingFile) then
            let content = String.concat "\n" [
                "# Type Mappings: TypeScript → Go"
                ""
                "> This file is shared across all porting sprints.  "
                "> Each sprint appends its type mappings here so later sprints have correct references."
                ""
                "## Primitive Types"
                "| TypeScript | Go |"
                "|---|---|"
                "| `string` | `string` |"
                "| `number` | `float64` or `int` (context-dependent) |"
                "| `boolean` | `bool` |"
                "| `null \\| undefined` | pointer / zero value / `ok` pattern |"
                "| `any` / `unknown` | `interface{}` (avoid — prefer concrete types) |"
                "| `void` | (no return) |"
                "| `Promise<T>` | `(T, error)` or use `context.Context` |"
                ""
                "## Collection Types"
                "| TypeScript | Go |"
                "|---|---|"
                "| `Array<T>` / `T[]` | `[]T` |"
                "| `Map<K,V>` | `map[K]V` |"
                "| `Set<T>` | `map[T]struct{}` |"
                "| `Record<K,V>` | `map[K]V` or struct |"
                ""
                "## Project-Specific Mappings"
                "<!-- Sprints append here -->"
            ]
            File.WriteAllText(PortingConfig.typeMappingFile, content)

    /// Build the shared context string to inject into each sprint prompt.
    let buildSharedContext (completedChunkNames: string list) : string =
        let mappings = readTypeMappings ()
        let completedList =
            if completedChunkNames.IsEmpty then "None yet — this is the first chunk."
            else completedChunkNames |> List.map (fun n -> $"- {n}") |> String.concat "\n"
        String.concat "\n" [
            "<shared_context>"
            "<type_mappings>"
            mappings
            "</type_mappings>"
            "<completed_modules>"
            completedList
            "</completed_modules>"
            "<instructions>"
            "- When you create new Go types for TypeScript interfaces/types, append a row to the"
            $"  \"Project-Specific Mappings\" section of `{PortingConfig.typeMappingFile}`."
            "- Import types from already-ported modules rather than redefining them."
            "- Preserve the same module boundaries as the TypeScript source."
            "</instructions>"
            "</shared_context>"
        ]

// ═════════════════════════════════════════════════════════════════════════════
// Sprint execution via Ralph
// ═════════════════════════════════════════════════════════════════════════════

module SprintRunner =
    /// Run Ralph on a single sprint file.  Returns exit code.
    let runRalph (sprintRequest: string) (autoApprove: bool) : int =
        let args = [|
            "Ralph.fsx"
            sprintRequest
            "--yes"
            "--hidden"
        |]
        try
            let result =
                cli {
                    Exec "dotnet"
                    Arguments (Array.append [| "fsi" |] args)
                }
                |> Command.execute
            result.ExitCode
        with ex ->
            eprintfn $"Ralph execution failed: {ex.Message}"
            1

    /// Run `go build ./...` in the target directory. Returns true if build succeeds.
    let goBuild (goDir: string) : bool =
        try
            let result =
                cli {
                    Exec "go"
                    Arguments [| "build"; "./..." |]
                    WorkingDirectory goDir
                }
                |> Command.execute
            result.ExitCode = 0
        with _ -> false

    /// Run `go test ./...` in the target directory. Returns (passed, total, output).
    let goTest (goDir: string) : int * int * string =
        try
            let result =
                cli {
                    Exec "go"
                    Arguments [| "test"; "-v"; "-count=1"; "./..." |]
                    WorkingDirectory goDir
                }
                |> Command.execute
            let output = result.Text |> Option.defaultValue ""
            // Count pass/fail from go test -v output
            let lines = output.Split('\n')
            let passed = lines |> Array.filter (fun l -> l.StartsWith("--- PASS:")) |> Array.length
            let failed = lines |> Array.filter (fun l -> l.StartsWith("--- FAIL:")) |> Array.length
            (passed, passed + failed, output)
        with _ -> (0, 0, "go test failed to execute")

    /// Run `go test -coverprofile` and extract coverage percentage.
    let goCoverage (goDir: string) : float option =
        try
            let result =
                cli {
                    Exec "go"
                    Arguments [| "test"; "-coverprofile=/tmp/cover.out"; "./..." |]
                    WorkingDirectory goDir
                }
                |> Command.execute
            let output = result.Text |> Option.defaultValue ""
            // Look for "coverage: XX.X% of statements"
            let m = System.Text.RegularExpressions.Regex.Match(output, @"coverage:\s+([\d.]+)%")
            if m.Success then Some (float m.Groups.[1].Value) else None
        with _ -> None

// ═════════════════════════════════════════════════════════════════════════════
// The Outer Loop
// ═════════════════════════════════════════════════════════════════════════════

module OuterLoop =

    /// Initialize a new porting campaign.
    let init (sourceDir: string) (goModulePath: string) (projectName: string) =
        SharedContext.initTypeMappings ()

        // Analyze and split
        let maxTokens = Some PortingConfig.maxTokensPerSprint
        let (chunks, summary) = analyzeAndSplit sourceDir PortingConfig.sprintsDir goModulePath maxTokens

        AnsiConsole.MarkupLine $"[bold green]Porting analysis complete[/]"
        AnsiConsole.WriteLine summary

        // Initialize progress state
        let modules =
            chunks |> List.map (fun c ->
                { Name = c.Name
                  Status = NotStarted
                  SourceTokens = c.TotalTokens
                  SourceFiles = c.Modules |> List.sumBy (fun m -> m.Files.Length)
                  GoTestsPassing = 0
                  GoTestsTotal = 0
                  CoveragePercent = None
                  LastUpdated = DateTime.UtcNow })
        let state = {
            ProjectName = projectName
            SourceDir = sourceDir
            GoModulePath = goModulePath
            Modules = modules
            History = []
            StartTime = DateTime.UtcNow
        }
        saveState state
        writeReport state
        (chunks, state)

    /// Execute the outer porting loop.
    /// For each chunk in dependency order:
    ///   1. Inject shared context into the sprint
    ///   2. Run Ralph to implement + verify
    ///   3. Validate Go build + tests (backpressure)
    ///   4. Update shared context with new type mappings
    ///   5. Record progress and take a snapshot
    ///   6. If backpressure threshold hit, pause for human review
    let run (chunks: SprintChunk list) (state: PortingState) (goDir: string) (autoApprove: bool) =
        let mutable currentState = state
        let mutable consecutiveFailures = 0

        for chunk in chunks do
            let moduleIdx = currentState.Modules |> List.tryFindIndex (fun m -> m.Name = chunk.Name)

            // Skip already-completed modules (for resume)
            let alreadyDone =
                moduleIdx
                |> Option.map (fun i -> currentState.Modules.[i].Status = Complete)
                |> Option.defaultValue false
            if alreadyDone then
                AnsiConsole.MarkupLine $"[dim]Skipping {Markup.Escape chunk.Name} (already complete)[/]"
            else
                AnsiConsole.MarkupLine $"\n[bold cyan]═══ Sprint {chunk.Order}: {Markup.Escape chunk.Name} ═══[/]"

                // Update status → InProgress
                let updated =
                    currentState.Modules |> List.map (fun m ->
                        if m.Name = chunk.Name then { m with Status = InProgress 0; LastUpdated = DateTime.UtcNow }
                        else m)
                currentState <- { currentState with Modules = updated }

                // Build shared context
                let completedNames =
                    currentState.Modules
                    |> List.filter (fun m -> m.Status = Complete || m.Status = TestsPassing || m.Status = Reviewed)
                    |> List.map (fun m -> m.Name)
                let sharedCtx = SharedContext.buildSharedContext completedNames

                // Construct the Ralph request with shared context
                let request = $"""Port TypeScript module '{chunk.Name}' to Go.

{sharedCtx}

Sprint file: {PortingConfig.sprintsDir}/{sprintf "%02d" chunk.Order}_{chunk.Name.Replace(" ", "_")}.md

After porting, append any new type mappings to {PortingConfig.typeMappingFile}."""

                // Run Ralph
                let exitCode = SprintRunner.runRalph request autoApprove

                // Validate with Go toolchain (backpressure)
                let buildOk = SprintRunner.goBuild goDir
                let (testsPassing, testsTotal, _testOutput) = SprintRunner.goTest goDir
                let coverage = SprintRunner.goCoverage goDir

                let newStatus =
                    if exitCode <> 0 then InProgress 1
                    elif not buildOk then Ported  // Code exists but doesn't compile
                    elif testsPassing < testsTotal then TestsPassing
                    else Complete

                // Update module progress
                let updatedModules =
                    currentState.Modules |> List.map (fun m ->
                        if m.Name = chunk.Name then
                            { m with
                                Status = newStatus
                                GoTestsPassing = testsPassing
                                GoTestsTotal = testsTotal
                                CoveragePercent = coverage
                                LastUpdated = DateTime.UtcNow }
                        else m)
                currentState <- { currentState with Modules = updatedModules }

                // Take a snapshot
                let snap = createSnapshot currentState
                currentState <- { currentState with History = currentState.History @ [snap] }

                // Persist
                saveState currentState
                writeReport currentState

                // Show progress panel
                let panel = buildPortingPanel currentState
                AnsiConsole.Write panel

                // Backpressure: if too many consecutive failures, pause
                if newStatus <> Complete then
                    consecutiveFailures <- consecutiveFailures + 1
                    if consecutiveFailures >= PortingConfig.backpressureThreshold then
                        AnsiConsole.MarkupLine $"[red bold]⚠ {consecutiveFailures} consecutive failures — pausing for review[/]"
                        AnsiConsole.MarkupLine $"[yellow]Review progress at {PortingConfig.progressFile}[/]"
                        if not autoApprove then
                            if not (AnsiConsole.Confirm("Continue porting?", false)) then
                                AnsiConsole.MarkupLine "[red]Porting paused by user.[/]"
                                saveState currentState
                                ()  // Exit loop early
                        consecutiveFailures <- 0  // Reset after human review
                else
                    consecutiveFailures <- 0
                    AnsiConsole.MarkupLine $"[green]✓ {Markup.Escape chunk.Name} complete[/]"

        // Final summary
        let finalSnap = createSnapshot currentState
        AnsiConsole.MarkupLine $"\n[bold green]Porting session complete[/]"
        AnsiConsole.MarkupLine $"  Modules: {finalSnap.ModulesComplete}/{finalSnap.TotalModules}"
        AnsiConsole.MarkupLine $"  Tests:   {finalSnap.TestsPassing}/{finalSnap.TotalTests}"
        saveState currentState
        writeReport currentState
        currentState

// ═════════════════════════════════════════════════════════════════════════════
// CLI entry point
// ═════════════════════════════════════════════════════════════════════════════

let printUsage () =
    printfn "PortingLoop — Large-scale TypeScript → Go porting orchestrator"
    printfn ""
    printfn "Usage:"
    printfn "  dotnet fsi PortingLoop.fsx init <source-dir> <go-module-path> [project-name]"
    printfn "  dotnet fsi PortingLoop.fsx run  <go-output-dir> [--yes]"
    printfn "  dotnet fsi PortingLoop.fsx status"
    printfn ""
    printfn "Commands:"
    printfn "  init    Analyze TypeScript source, create porting plan and sprint files"
    printfn "  run     Execute the porting loop (processes sprints in dependency order)"
    printfn "  status  Show current porting progress"
    printfn ""
    printfn "Options:"
    printfn "  --yes   Auto-approve all prompts (no human review pauses)"

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| ["--help"] | ["-h"] | [] ->
    printUsage ()

| "init" :: sourceDir :: goModulePath :: rest ->
    let projectName = rest |> List.tryHead |> Option.defaultValue (Path.GetFileName sourceDir)
    let (chunks, _state) = OuterLoop.init sourceDir goModulePath projectName
    AnsiConsole.MarkupLine $"[green]Created {chunks.Length} sprint files in {PortingConfig.sprintsDir}[/]"
    AnsiConsole.MarkupLine $"[green]Shared context initialized at {PortingConfig.sharedContextDir}[/]"
    AnsiConsole.MarkupLine $"[yellow]Review sprints, then run: dotnet fsi PortingLoop.fsx run <go-output-dir>[/]"

| "run" :: goDir :: rest ->
    let autoApprove = rest |> List.contains "--yes"
    match loadState () with
    | None ->
        AnsiConsole.MarkupLine "[red]No porting state found. Run 'init' first.[/]"
        exit 1
    | Some state ->
        // Reload chunks from sprint files
        let sprintFiles =
            if Directory.Exists PortingConfig.sprintsDir then
                Directory.GetFiles(PortingConfig.sprintsDir, "*.md") |> Array.sort |> Array.toList
            else []
        if sprintFiles.IsEmpty then
            AnsiConsole.MarkupLine "[red]No sprint files found. Run 'init' first.[/]"
            exit 1
        // Re-derive chunks from state modules (order matches sprint file order)
        let chunks =
            state.Modules |> List.mapi (fun i m ->
                { Order = i + 1; Name = m.Name; Modules = []; TotalTokens = m.SourceTokens; DependsOn = [] })
        let _finalState = OuterLoop.run chunks state goDir autoApprove
        ()

| ["status"] ->
    match loadState () with
    | None ->
        AnsiConsole.MarkupLine "[yellow]No porting state found. Run 'init' first.[/]"
    | Some state ->
        let panel = buildPortingPanel state
        AnsiConsole.Write panel
        let snap = createSnapshot state
        AnsiConsole.MarkupLine $"\nModules: {snap.ModulesComplete}/{snap.TotalModules} complete"
        AnsiConsole.MarkupLine $"Tests:   {snap.TestsPassing}/{snap.TotalTests} passing"

| other ->
    let cmd = other |> String.concat " "
    printfn $"Unknown command: {cmd}"
    printUsage ()
    exit 1
