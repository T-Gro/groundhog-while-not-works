#!/usr/bin/env dotnet fsi

/// FailureAnalyzer — Categorizes test failures to select highest-impact next sprint.
///
/// Language-agnostic. Groups failures by:
///   - Root cause category (parse error, crash, missing output, etc.)
///   - Feature area (inferred from sample file names or configured mapping)
///
/// Output: ranked list of failure categories with estimated impact.

#load "ProjectConfig.fsx"
#r "nuget: Fli"

open System
open System.IO
open System.Text.RegularExpressions
open Fli
open ProjectConfig.ProjectConfig

module FailureAnalyzer =

    type FailureCategory =
        | ParseError
        | Crash
        | Timeout
        | MissingOutput
        | ExtraOutput
        | WrongLocation
        | WrongContent
        | Unknown

    type FailureEntry = {
        SampleFile: string
        Category: FailureCategory
        Feature: string
        ErrorMessage: string
    }

    type FailureGroup = {
        Feature: string
        Category: FailureCategory
        Count: int
        Samples: string list
        EstimatedImpact: int
    }

    /// Infer a feature area from a sample file name.
    /// This is a heuristic — project-specific mappings can be added to beads memories.
    let inferFeature (sampleName: string) : string =
        let name = Path.GetFileNameWithoutExtension(sampleName).ToLowerInvariant()
        // Extract the alphabetic prefix before any numbers (e.g., "genericTypes1" → "generictypes")
        let prefix = Regex.Match(name, @"^([a-zA-Z_]+)").Groups.[1].Value
        if String.IsNullOrWhiteSpace prefix then "other"
        else prefix

    /// Categorize a failure from its error message.
    let categorizeError (errorMsg: string) : FailureCategory =
        let msg = errorMsg.ToLowerInvariant()
        if msg.Contains "panic" || msg.Contains "crash" || msg.Contains "segfault" then Crash
        elif msg.Contains "timeout" || msg.Contains "timed out" then Timeout
        elif msg.Contains "parse" || msg.Contains "syntax" then ParseError
        elif msg.Contains "missing" || msg.Contains "expected but not found" then MissingOutput
        elif msg.Contains "extra" || msg.Contains "unexpected" then ExtraOutput
        elif msg.Contains "wrong line" || msg.Contains "wrong position" || msg.Contains "off by" then WrongLocation
        elif msg.Contains "differs" || msg.Contains "mismatch" then WrongContent
        else Unknown

    /// Run the test comparison and parse failures.
    /// Uses the project's test command and parses output.
    let collectFailures (config: ProjectConfig) : FailureEntry list =
        try
            let parts = config.TestCommand.Split([|' '|], 2)
            let exec = parts.[0]
            let args = if parts.Length > 1 then parts.[1] else ""
            let result =
                cli {
                    Exec exec
                    Arguments (args.Split(' '))
                    WorkingDirectory config.TargetDir
                }
                |> Command.execute

            let output = result.Text |> Option.defaultValue ""
            let errOutput = result.Error |> Option.defaultValue ""
            let combined = output + "\n" + errOutput

            // Parse failure lines — look for common test failure patterns
            let failPattern = Regex(@"(?:FAIL|FAILED|ERROR|---\s*FAIL).*?(\S+\.\w+)", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

            failPattern.Matches(combined)
            |> Seq.cast<Match>
            |> Seq.map (fun m ->
                let sample = m.Groups.[1].Value
                let errorLine = m.Value
                { SampleFile = sample
                  Category = categorizeError errorLine
                  Feature = inferFeature sample
                  ErrorMessage = errorLine.Trim() })
            |> Seq.toList
            |> List.distinctBy (fun e -> e.SampleFile)
        with ex ->
            eprintfn $"Failure collection failed: {ex.Message}"
            []

    /// Group failures by feature area and category.
    let groupFailures (failures: FailureEntry list) : FailureGroup list =
        failures
        |> List.groupBy (fun f -> (f.Feature, f.Category))
        |> List.map (fun ((feature, category), entries) ->
            { Feature = feature
              Category = category
              Count = entries.Length
              Samples = entries |> List.map (fun e -> e.SampleFile) |> List.truncate 5
              EstimatedImpact = entries.Length })
        |> List.sortByDescending (fun g -> g.EstimatedImpact)

    /// Select the highest-impact failure group for the next sprint.
    let selectNextTarget (groups: FailureGroup list) : FailureGroup option =
        let priority = function
            | Crash -> 0 | ParseError -> 1 | MissingOutput -> 2
            | ExtraOutput -> 3 | WrongLocation -> 4 | WrongContent -> 5
            | Timeout -> 6 | Unknown -> 7
        groups
        |> List.sortBy (fun g -> (priority g.Category, -g.EstimatedImpact))
        |> List.tryHead

    /// Map a feature name to relevant source files using the layer config.
    /// Falls back to beads memories if no direct mapping exists.
    let featureToSourceDirs (config: ProjectConfig) (feature: string) : string list =
        // Check if any layer name matches the feature
        config.Layers
        |> List.tryFind (fun l -> l.Name.ToLowerInvariant().Contains(feature.ToLowerInvariant()))
        |> Option.map (fun l -> l.SourceDirs)
        |> Option.defaultValue []

    /// Print a ranked failure analysis report.
    let printReport (groups: FailureGroup list) (totalSamples: int) (passingCount: int) =
        let catStr = function
            | ParseError -> "PARSE" | Crash -> "CRASH" | Timeout -> "TIMEOUT"
            | MissingOutput -> "MISS" | ExtraOutput -> "EXTRA"
            | WrongLocation -> "LOC" | WrongContent -> "CONTENT" | Unknown -> "???"
        printfn "╔══════════════════════════════════════════════════════╗"
        printfn "║  FAILURE ANALYSIS                                    ║"
        printfn "╠══════════════════════════════════════════════════════╣"
        printfn $"║  Passing: {passingCount}/{totalSamples} ({if totalSamples > 0 then float passingCount / float totalSamples * 100.0 else 0.0:F1}%%)"
        printfn "╠══════════════════════════════════════════════════════╣"
        for g in groups |> List.truncate 15 do
            printfn $"║  {catStr g.Category,-8} {g.Feature,-25} {g.Count,4} samples"
        printfn "╚══════════════════════════════════════════════════════╝"
        match selectNextTarget groups with
        | Some t -> printfn $"\n→ RECOMMENDED: Fix '{t.Feature}' ({t.Count} samples affected)"
        | None -> printfn "\n✓ No failures!"
