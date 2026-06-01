#!/usr/bin/env dotnet fsi
#r "nuget: FSharp.SystemTextJson"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

/// Minimal config: what are we porting, from where, to where?
/// Lives in project.json in the target repo root.
module ProjectConfig =

    type Config = {
        ProjectName : string
        SourceDir   : string
        SourceLang  : string
        TargetLang  : string
    }

    let targetDir () = Environment.CurrentDirectory

    let private path () = Path.Combine(targetDir (), "project.json")
    let private opts = let o = JsonSerializerOptions(WriteIndented = true) in o.Converters.Add(JsonFSharpConverter()); o

    let save cfg  = File.WriteAllText(path (), JsonSerializer.Serialize(cfg, opts))
    let load ()   = let p = path () in if File.Exists p then try Some (JsonSerializer.Deserialize<Config>(File.ReadAllText p, opts)) with _ -> None else None
    let require() = match load () with Some c -> c | None -> eprintfn $"No project.json in {targetDir ()}."; exit 1
