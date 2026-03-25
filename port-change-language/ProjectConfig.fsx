#!/usr/bin/env dotnet fsi

/// ProjectConfig — Language-agnostic project configuration.
///
/// All project-specific knowledge lives in project.json, populated during `init`.
/// The harness scripts read this config and never hardcode languages, paths, or commands.

#r "nuget: FSharp.SystemTextJson"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

module ProjectConfig =

    type LayerConfig = {
        Id: string              // e.g., "L0", "L1"
        Name: string            // e.g., "parser", "checker"
        Description: string
        SourceDirs: string list // relative paths within source tree
        TargetPackage: string   // relative path within target tree
        DependsOn: string list  // layer IDs this depends on
    }

    type ProjectConfig = {
        ProjectName: string
        SourceDir: string
        TargetDir: string
        SourceLang: string          // discovered during init
        TargetLang: string          // discovered during init
        SourceFileGlob: string      // glob for source files to port
        TestSampleGlob: string      // glob for test input/sample files
        TestSampleDir: string       // where test samples live in source
        GoldenDir: string           // where golden reference files go
        BuildCommand: string        // target-language build command
        LintCommand: string         // target-language lint/vet command
        TestCommand: string         // target-language test command
        OracleRunCommand: string    // command to run original tool on a sample
        OracleCompareCommand: string // command to compare output vs golden
        Layers: LayerConfig list
        MaxTokensPerSprint: int
        MaxIterationsPerSprint: int
        BackpressureThreshold: int
    }

    let private configPath () =
        Path.Combine(__SOURCE_DIRECTORY__, "project.json")

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.Converters.Add(JsonFSharpConverter())
        opts

    let save (config: ProjectConfig) =
        let json = JsonSerializer.Serialize(config, jsonOptions)
        File.WriteAllText(configPath(), json)

    let load () : ProjectConfig option =
        let path = configPath()
        if File.Exists path then
            try Some (JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText path, jsonOptions))
            with ex ->
                eprintfn $"Failed to load project.json: {ex.Message}"
                None
        else None

    let require () : ProjectConfig =
        match load() with
        | Some c -> c
        | None ->
            eprintfn "No project.json found. Run 'init' first."
            exit 1

    /// Create a default config — the init command will override these with discovered values.
    let createDefault (sourceDir: string) (targetDir: string) : ProjectConfig =
        { ProjectName = Path.GetFileName(sourceDir)
          SourceDir = sourceDir
          TargetDir = targetDir
          SourceLang = "unknown"
          TargetLang = "unknown"
          SourceFileGlob = "*.*"
          TestSampleGlob = "*.*"
          TestSampleDir = ""
          GoldenDir = Path.Combine(targetDir, "testdata", "golden")
          BuildCommand = "echo 'no build command configured'"
          LintCommand = "echo 'no lint command configured'"
          TestCommand = "echo 'no test command configured'"
          OracleRunCommand = "echo 'no oracle command configured'"
          OracleCompareCommand = "echo 'no compare command configured'"
          Layers = []
          MaxTokensPerSprint = 60_000
          MaxIterationsPerSprint = 15
          BackpressureThreshold = 3 }
