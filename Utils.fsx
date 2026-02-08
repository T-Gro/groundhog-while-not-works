// Utils.fsx - Core utilities: Config, XML helpers, YAML frontmatter
// Loaded by Ralph.fsx

#r "nuget: YamlDotNet"

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open System.Collections.Generic
open YamlDotNet.Serialization

module Config =
    let Model = "claude-opus-4.6"
    let MaxIterations = 15  // Hard limit before giving up
    let ArbiterThreshold = 4  // Invoke arbiter after this many failures (recoverable)
    // Use script location for verifiers, working directory for ralph output
    let scriptDir = __SOURCE_DIRECTORY__
    let ralphDir = Path.Combine(Directory.GetCurrentDirectory(), ".tools", "ralph")
    let sprintsDir = Path.Combine(ralphDir, "sprints")  // Individual sprint files
    let backlogFile = Path.Combine(ralphDir, "BACKLOG.md")  // Overview only (for planner context and final verifiers)
    let verifiersDir = Path.Combine(scriptDir, "verifiers")  // Verifier prompts as .md files (relative to script)
    let templateFile = Path.Combine(scriptDir, "templates", "SPRINT_TEMPLATE.md")  // Template for sprint files

module XmlHelpers =
    // Token-efficient XML helpers
    // - Attributes for fixed/known-size data
    // - Direct text content for free-form data (no wrapper elements)
    // - Element names match domain concepts (e.g. <Functional> not <Verifier step="Functional">)
    
    /// Core XML element builder: xe "name" [attrs] [children] "innerText"
    let xe (name: string) (attrs: (string * string) list) (children: XElement list) (text: string) : XElement =
        let el = XElement(XName.Get name)
        for (k, v) in attrs do el.Add(XAttribute(XName.Get k, v))
        for c in children do el.Add(c)
        if not (String.IsNullOrEmpty text) then el.Add(text)
        el

    // Shortcuts - any subset of (attrs, children, text)
    let x   name                        = xe name [] [] ""          // <name/>
    let xt  name text                   = xe name [] [] text        // <name>text</name>
    let xc  name children               = xe name [] children ""    // <name><child/></name>
    let xat name attrs text             = xe name attrs [] text     // <name attr="val">text</name>
    let xac name attrs children         = xe name attrs children "" // <name attr="val"><child/></name>

    // Signal detection: match signal NOT inside quotes (avoids false positives from LLM quoting signals)
    let hasSignal (signal: string) (text: string) =
        let pattern = sprintf @"(?<![""'`])%s(?![""'`])" (Regex.Escape signal)
        Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)

/// Git helpers
module Git =
    open System.Diagnostics
    
    /// Get current HEAD commit hash (short form)
    let getHeadCommit () : string option =
        try
            let psi = ProcessStartInfo("git", "rev-parse --short HEAD")
            psi.RedirectStandardOutput <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use p = Process.Start(psi)
            let output = p.StandardOutput.ReadToEnd().Trim()
            p.WaitForExit()
            if p.ExitCode = 0 && not (String.IsNullOrEmpty output) then Some output else None
        with _ -> None

/// YAML frontmatter parsing for sprint files
module YamlFrontmatter =
    let private deserializer = DeserializerBuilder().Build()
    
    /// Extract frontmatter from markdown (between first two --- lines)
    let extract (markdown: string) : string option =
        let lines = markdown.Split([|'\n'|], StringSplitOptions.None)
        if lines.Length < 2 || lines.[0].Trim() <> "---" then None
        else
            lines |> Array.skip 1 |> Array.tryFindIndex (fun l -> l.Trim() = "---")
            |> Option.map (fun idx -> lines.[1..idx] |> String.concat "\n")
    
    /// Extract body (everything after frontmatter)
    let extractBody (markdown: string) : string =
        let lines = markdown.Split([|'\n'|], StringSplitOptions.None)
        if lines.Length < 2 || lines.[0].Trim() <> "---" then markdown
        else
            match lines |> Array.skip 1 |> Array.tryFindIndex (fun l -> l.Trim() = "---") with
            | Some idx -> lines.[(idx + 2)..] |> String.concat "\n" |> fun s -> s.TrimStart()
            | None -> markdown
    
    /// Parse YAML to dictionary (for optional metadata)
    let parse (yaml: string) : Dictionary<string, obj> =
        try deserializer.Deserialize<Dictionary<string, obj>>(yaml)
        with _ -> Dictionary<string, obj>()
