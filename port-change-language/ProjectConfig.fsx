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
        SourceLang: string
        TargetLang: string
        SourceFileGlob: string
        TestSampleGlob: string
        TestSampleDir: string
        GoldenDir: string
        Layers: LayerConfig list
        MaxTokensPerSprint: int
        MaxIterationsPerSprint: int
    }

    /// Target dir = wherever the user launched from (cwd).
    let targetDir () = Environment.CurrentDirectory

    /// project.json lives in the target repo root (cwd).
    let private configPath () = Path.Combine(targetDir(), "project.json")

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
            eprintfn $"No project.json found in {targetDir()}. Create one first."
            exit 1

    let createDefault (sourceDir: string) : ProjectConfig =
        { ProjectName = Path.GetFileName(targetDir())
          SourceDir = sourceDir
          SourceLang = "unknown"
          TargetLang = "unknown"
          SourceFileGlob = "*.*"
          TestSampleGlob = "*.*"
          TestSampleDir = ""
          GoldenDir = Path.Combine(targetDir(), "testdata", "golden")
          Layers = []
          MaxTokensPerSprint = 60_000
          MaxIterationsPerSprint = 15 }
