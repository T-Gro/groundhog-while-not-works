#!/usr/bin/env dotnet fsi

/// GoldenOracle — Generates golden reference files from the original tool.
///
/// Runs the original tool (whatever it is) on every test sample and captures
/// the expected output. These golden files are the test oracle for convergence.
///
/// Language-agnostic: reads commands from project.json.
///
/// Usage:
///   dotnet fsi GoldenOracle.fsx generate     -- run oracle on all samples, save golden files
///   dotnet fsi GoldenOracle.fsx stats         -- show statistics about golden files
///   dotnet fsi GoldenOracle.fsx compare       -- compare current tool output vs golden

#load "ProjectConfig.fsx"
#r "nuget: Fli"

open System
open System.IO
open System.Text.Json
open Fli
open ProjectConfig.ProjectConfig

module GoldenOracle =

    /// Find all test sample files based on project config.
    let findSampleFiles (config: ProjectConfig) : string list =
        let dir = config.TestSampleDir
        if String.IsNullOrWhiteSpace dir || not (Directory.Exists dir) then
            eprintfn $"Test sample directory not found or not configured: '{dir}'"
            []
        else
            Directory.EnumerateFiles(dir, config.TestSampleGlob, SearchOption.AllDirectories)
            |> Seq.filter (fun f -> not (f.Contains "node_modules") && not (f.Contains "__pycache__"))
            |> Seq.toList
            |> List.sort

    /// Run the original tool on a single sample file. Returns raw output.
    let runOracle (config: ProjectConfig) (samplePath: string) : string =
        try
            let cmd = config.OracleRunCommand.Replace("{sample}", samplePath)
            let parts = cmd.Split([|' '|], 2)
            let exec = parts.[0]
            let args = if parts.Length > 1 then parts.[1] else ""
            let result =
                cli {
                    Exec exec
                    Arguments (args.Split(' '))
                    WorkingDirectory config.SourceDir
                }
                |> Command.execute
            result.Text |> Option.defaultValue ""
        with ex ->
            $"ERROR: {ex.Message}"

    /// Generate golden file for one sample.
    let generateOne (config: ProjectConfig) (samplePath: string) : string * int =
        let relPath = Path.GetRelativePath(config.TestSampleDir, samplePath)
        let goldenPath = Path.Combine(config.GoldenDir, Path.ChangeExtension(relPath, ".golden.txt"))

        Directory.CreateDirectory(Path.GetDirectoryName goldenPath) |> ignore

        let output = runOracle config samplePath
        File.WriteAllText(goldenPath, output)

        let lineCount = output.Split('\n').Length
        (relPath, lineCount)

    /// Generate golden files for all samples.
    let generate (config: ProjectConfig) =
        let samples = findSampleFiles config
        printfn $"Found {samples.Length} test sample files (glob: {config.TestSampleGlob})"

        Directory.CreateDirectory config.GoldenDir |> ignore

        let mutable total = 0
        for sample in samples do
            let (rel, _) = generateOne config sample
            total <- total + 1
            if total % 50 = 0 then
                printfn $"  Processed {total}/{samples.Length} samples..."

        printfn $"\nGolden oracle generation complete:"
        printfn $"  Samples processed: {total}"
        printfn $"  Golden files written to: {config.GoldenDir}"

    /// Compare current tool output vs golden files. Returns (passing, total, failures).
    let compare (config: ProjectConfig) : int * int * (string * string) list =
        if not (Directory.Exists config.GoldenDir) then
            (0, 0, [])
        else
            let goldenFiles = Directory.GetFiles(config.GoldenDir, "*.golden.txt", SearchOption.AllDirectories)
            let mutable passing = 0
            let mutable failures = []

            for gf in goldenFiles do
                let relPath = Path.GetRelativePath(config.GoldenDir, gf)
                let sampleName = Path.ChangeExtension(relPath, Path.GetExtension(config.TestSampleGlob).TrimStart('*'))
                let samplePath = Path.Combine(config.TestSampleDir, sampleName)

                if File.Exists samplePath then
                    let expected = File.ReadAllText(gf).Trim()
                    let actual = (runOracle config samplePath).Trim()
                    if expected = actual then
                        passing <- passing + 1
                    else
                        failures <- (sampleName, $"Output differs") :: failures

            (passing, goldenFiles.Length, failures |> List.rev)

    /// Show statistics about golden files.
    let stats (config: ProjectConfig) =
        if not (Directory.Exists config.GoldenDir) then
            printfn "Golden directory does not exist. Run 'generate' first."
        else
            let files = Directory.GetFiles(config.GoldenDir, "*.golden.txt", SearchOption.AllDirectories)
            let mutable nonEmpty = 0
            let mutable empty = 0
            let mutable totalBytes = 0L
            for f in files do
                let info = FileInfo(f)
                totalBytes <- totalBytes + info.Length
                if info.Length > 0L then nonEmpty <- nonEmpty + 1
                else empty <- empty + 1
            printfn $"Golden file statistics:"
            printfn $"  Total files: {files.Length}"
            printfn $"  With content: {nonEmpty}"
            printfn $"  Empty: {empty}"
            printfn $"  Total size: {totalBytes / 1024L} KB"

// CLI entry point
let config = ProjectConfig.require()

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| ["generate"] -> GoldenOracle.generate config
| ["stats"]    -> GoldenOracle.stats config
| ["compare"]  ->
    let (p, t, f) = GoldenOracle.compare config
    printfn $"Oracle comparison: {p}/{t} passing ({if t > 0 then float p / float t * 100.0 else 0.0:F1}%%)"
    if f.Length > 0 then
        printfn $"Failures ({f.Length}):"
        for (s, e) in f |> List.truncate 20 do printfn $"  {s}: {e}"
| _ ->
    printfn "GoldenOracle — Generate test oracle from the original tool"
    printfn ""
    printfn "Usage:"
    printfn "  dotnet fsi GoldenOracle.fsx generate    Run original tool, save golden files"
    printfn "  dotnet fsi GoldenOracle.fsx stats        Show golden file statistics"
    printfn "  dotnet fsi GoldenOracle.fsx compare      Compare current output vs golden"
