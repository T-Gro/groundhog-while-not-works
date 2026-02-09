#r "nuget: YamlDotNet"

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open System.Xml.Linq
open System.Collections.Generic
open YamlDotNet.Serialization

module Config =
    let Model = "claude-opus-4.6-fast"
    let MaxIterations = 15
    let ArbiterThreshold = 4
    let scriptDir = __SOURCE_DIRECTORY__
    let ralphDir = Path.Combine(Directory.GetCurrentDirectory(), ".tools", "ralph")
    let sprintsDir = Path.Combine(ralphDir, "sprints")
    let backlogFile = Path.Combine(ralphDir, "BACKLOG.md")
    let verifiersDir = Path.Combine(scriptDir, "verifiers")
    let templateFile = Path.Combine(scriptDir, "templates", "SPRINT_TEMPLATE.md")

module XmlHelpers =
    let xe name (attrs: (string * string) list) (children: XElement list) text : XElement =
        let el = XElement(XName.Get name)
        for (k, v) in attrs do el.Add(XAttribute(XName.Get k, v))
        for c in children do el.Add(c)
        if not (String.IsNullOrEmpty text) then el.Add(text)
        el

    let x   name                = xe name [] [] ""
    let xt  name text           = xe name [] [] text
    let xc  name children       = xe name [] children ""
    let xat name attrs text     = xe name attrs [] text
    let xac name attrs children = xe name attrs children ""

    let hasSignal signal (text: string) =
        Regex.IsMatch(text, sprintf @"(?<![""'`])%s(?![""'`])" (Regex.Escape signal), RegexOptions.IgnoreCase)

module PromptBuilder =
    open XmlHelpers
    let instruction name text = xt name text
    let userContent name content = xt name content
    let truncatedContent name maxLen (content: string) =
        xt name (if content.Length > maxLen then content.[..maxLen-4] + "..." else content)
    let list name formatter items = xc name (items |> List.map formatter)
    let optionalEl name = function Some v -> [xt name v] | None -> []

module Git =
    let getHeadCommit () =
        try
            let psi = ProcessStartInfo("git", "rev-parse --short HEAD", RedirectStandardOutput=true, UseShellExecute=false, CreateNoWindow=true)
            use p = Process.Start(psi)
            let output = p.StandardOutput.ReadToEnd().Trim()
            p.WaitForExit()
            if p.ExitCode = 0 && output.Length > 0 then Some output else None
        with _ -> None

module YamlFrontmatter =
    let private deserializer = DeserializerBuilder().Build()
    
    let extract (markdown: string) =
        let lines = markdown.Split([|'\n'|], StringSplitOptions.None)
        if lines.Length < 2 || lines.[0].Trim() <> "---" then None
        else lines |> Array.skip 1 |> Array.tryFindIndex (fun l -> l.Trim() = "---") |> Option.map (fun idx -> lines.[1..idx] |> String.concat "\n")
    
    let extractBody (markdown: string) =
        let lines = markdown.Split([|'\n'|], StringSplitOptions.None)
        if lines.Length < 2 || lines.[0].Trim() <> "---" then markdown
        else
            match lines |> Array.skip 1 |> Array.tryFindIndex (fun l -> l.Trim() = "---") with
            | Some idx -> lines.[(idx + 2)..] |> String.concat "\n" |> fun s -> s.TrimStart()
            | None -> markdown
    
    let parse (yaml: string) = try deserializer.Deserialize<Dictionary<string, obj>>(yaml) with _ -> Dictionary<string, obj>()

module Logging =
    let mutable logPath = lazy (Path.Combine(Config.ralphDir, "ralph.log"))
    
    let log level msg =
        try
            Directory.CreateDirectory(Config.ralphDir) |> ignore
            let ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            File.AppendAllText(logPath.Value, $"[{ts}] [{level}] {msg}" + Environment.NewLine)
        with _ -> ()
    
    let info msg = log "INFO" msg
    let error msg = log "ERROR" msg
    let exn (ex: Exception) ctx = log "EXCEPTION" $"{ctx}: {ex.GetType().Name}: {ex.Message}"
