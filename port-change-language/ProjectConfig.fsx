#!/usr/bin/env dotnet fsi

#r "nuget: FSharp.SystemTextJson"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

module ProjectConfig =

    type ProjectConfig = {
        ProjectName: string
        SourceDir: string
        SourceLang: string
        TargetLang: string
    }

    let targetDir () = Environment.CurrentDirectory

    let private configPath () = Path.Combine(targetDir(), "project.json")

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.Converters.Add(JsonFSharpConverter())
        opts

    let save (config: ProjectConfig) =
        File.WriteAllText(configPath(), JsonSerializer.Serialize(config, jsonOptions))

    let load () : ProjectConfig option =
        let path = configPath()
        if File.Exists path then
            try Some (JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText path, jsonOptions))
            with ex -> eprintfn $"Failed to load project.json: {ex.Message}"; None
        else None

    let require () : ProjectConfig =
        match load() with
        | Some c -> c
        | None -> eprintfn $"No project.json in {targetDir()}."; exit 1
