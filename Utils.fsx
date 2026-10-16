#r "nuget: YamlDotNet"

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open System.Text
open System.Text.Json
open System.Security.Cryptography
open System.Xml.Linq
open System.Collections.Generic
open YamlDotNet.Serialization

module Config =
    let Model = Environment.GetEnvironmentVariable("DM_COPILOT_MODEL") |> Option.ofObj |> Option.defaultValue "gpt-6.1-sol"
    let Effort = Environment.GetEnvironmentVariable("DM_COPILOT_EFFORT") |> Option.ofObj |> Option.defaultValue "xhigh"
    let Context = Environment.GetEnvironmentVariable("DM_COPILOT_CONTEXT") |> Option.ofObj |> Option.defaultValue "long_context"
    let AgentTimeoutMinutes =
        match Environment.GetEnvironmentVariable("RALPH_AGENT_TIMEOUT_MINUTES") |> Option.ofObj with
        | Some value ->
            match Int32.TryParse value with
            | true, minutes when minutes > 0 -> minutes
            | _ -> 180
        | None -> 180
    do
        let normalizedModel = Model.ToLowerInvariant()
        if ["anthropic"; "claude"; "opus"; "sonnet"; "haiku"] |> List.exists normalizedModel.Contains then
            failwith $"DM_COPILOT_MODEL must be non-Anthropic, but was '{Model}'"
    let MaxIterations = 15
    let ArbiterThreshold = 6  // Iterations per sprint before calling arbiter
    let MaxArbiterAttempts = 6  // Max arbiter invocations before giving up
    let scriptDir = __SOURCE_DIRECTORY__
    let workDir =
        Environment.GetEnvironmentVariable("RALPH_WORK_DIR")
        |> Option.ofObj
        |> Option.defaultValue (Directory.GetCurrentDirectory())
    /// Resolve copilot binary — Fli needs full path or .cmd extension on Windows
    let CopilotExe =
        // Try .cmd wrapper (npm global install)
        let npmGlobal =
            let baseDir = Environment.GetEnvironmentVariable("DM_BASE_DIR") |> Option.ofObj |> Option.defaultValue ""
            if baseDir <> "" then Path.Combine(baseDir, ".tools", ".npm-global")
            else ""
        let cmdPath = if npmGlobal <> "" then Path.Combine(npmGlobal, "copilot.cmd") else ""
        if cmdPath <> "" && File.Exists(cmdPath) then cmdPath
        else
            // Try finding copilot.cmd on PATH
            let pathDirs = (Environment.GetEnvironmentVariable("PATH") |> Option.ofObj |> Option.defaultValue "").Split(';')
            pathDirs |> Array.tryPick (fun d ->
                let candidate = Path.Combine(d, "copilot.cmd")
                if File.Exists(candidate) then Some candidate else None)
            |> Option.defaultValue "copilot"
    let ralphDir =
        let workspaceKey =
            Path.GetFullPath(workDir)
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> fun value -> value.Substring(0, 16)
        let defaultStateDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "groundhog", "workspaces", workspaceKey)
        let directory =
            Environment.GetEnvironmentVariable("RALPH_STATE_DIR")
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultValue defaultStateDir
            |> Path.GetFullPath
        let worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath workDir)
        let comparison = if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
        if directory.Equals(worktree, comparison) || directory.StartsWith(worktree + string Path.DirectorySeparatorChar, comparison) then
            failwith "RALPH_STATE_DIR must be outside the source worktree."
        directory
    let sprintsDir = Path.Combine(ralphDir, "sprints")
    let backlogFile = Path.Combine(ralphDir, "BACKLOG.md")
    let verifiersDir = Path.Combine(scriptDir, "verifiers")
    let templateFile = Path.Combine(scriptDir, "templates", "SPRINT_TEMPLATE.md")

module XmlHelpers =
    let sanitizeXmlText (text: string) =
        if isNull text then ""
        else
            let result = StringBuilder(text.Length)
            let mutable index = 0
            while index < text.Length do
                let c = text[index]
                if Char.IsHighSurrogate c && index + 1 < text.Length && Char.IsLowSurrogate text[index + 1] then
                    result.Append(c).Append(text[index + 1]) |> ignore
                    index <- index + 2
                else
                    let valid =
                        c = '\t' || c = '\n' || c = '\r'
                        || (c >= '\u0020' && c <> '\uFFFE' && c <> '\uFFFF' && not (Char.IsSurrogate c))
                    result.Append(if valid then c else '\uFFFD') |> ignore
                    index <- index + 1
            result.ToString()

    let xe name (attrs: (string * string) list) (children: XElement list) text : XElement =
        let el = XElement(XName.Get name)
        for (k, v) in attrs do el.Add(XAttribute(XName.Get k, sanitizeXmlText v))
        for c in children do el.Add(c)
        if not (String.IsNullOrEmpty text) then el.Add(sanitizeXmlText text)
        el

    let x   name                = xe name [] [] ""
    let xt  name text           = xe name [] [] text
    let xc  name children       = xe name [] children ""
    let xat name attrs text     = xe name attrs [] text
    let xac name attrs children = xe name attrs children ""

    let hasSignal signal (text: string) =
        Regex.IsMatch(text, sprintf @"(?<![""'`])%s(?![""'`])" (Regex.Escape signal), RegexOptions.IgnoreCase)

    /// Check for a signal trying both the original form and with underscores replaced by spaces.
    let hasSignalAny (signal: string) (text: string) =
        hasSignal signal text || hasSignal (signal.Replace("_", " ")) text

module AgentTransport =
    type Output() =
        let responses = ResizeArray<string>()
        let mutable result: int option = None
        let mutable failure: string option = None
        let mutable lineNumber = 0
        let mutable lastRootIsFinal = false

        let objectValue (value: JsonElement) =
            if value.ValueKind <> JsonValueKind.Object then
                invalidOp "Expected a JSON object."
            let names = HashSet<string>(StringComparer.Ordinal)
            for property in value.EnumerateObject() do
                if not (names.Add property.Name) then invalidOp "Duplicate JSON property."
            value

        let property name (value: JsonElement) =
            match value.TryGetProperty(name: string) with
            | true, value -> Some value
            | _ -> None

        let stringValue (value: JsonElement) =
            if value.ValueKind <> JsonValueKind.String then invalidOp "Expected a JSON string."
            value.GetString()

        member _.AddLine(line: string) =
            lineNumber <- lineNumber + 1
            if failure.IsNone && not (String.IsNullOrWhiteSpace line) then
                try
                    if result.IsSome then invalidOp "Unexpected event after the terminal result."
                    use document = JsonDocument.Parse line
                    let root = objectValue document.RootElement
                    let kind =
                        property "type" root
                        |> Option.defaultWith (fun () -> invalidOp "Missing event type.")
                        |> stringValue
                    let data = property "data" root |> Option.map objectValue
                    let child =
                        (property "agentId" root).IsSome
                        || (property "parentToolCallId" root).IsSome
                        || (data |> Option.exists (fun value -> (property "parentToolCallId" value).IsSome))
                    match kind with
                    | "assistant.turn_start" when not child -> lastRootIsFinal <- false
                    | "assistant.message" when not child ->
                        let data = data |> Option.defaultWith (fun () -> invalidOp "Missing assistant data.")
                        let content =
                            property "content" data
                            |> Option.defaultWith (fun () -> invalidOp "Missing assistant content.")
                            |> stringValue
                        let phase = property "phase" data |> Option.map stringValue
                        let hasTools =
                            match property "toolRequests" data with
                            | None -> false
                            | Some tools when tools.ValueKind = JsonValueKind.Array -> tools.GetArrayLength() > 0
                            | _ -> invalidOp "Invalid assistant tool requests."
                        if phase = Some "final_answer" && hasTools then
                            invalidOp "A final assistant response cannot request tools."
                        lastRootIsFinal <- not hasTools && (phase.IsNone || phase = Some "final_answer") && content <> ""
                        if lastRootIsFinal then
                            responses.Add content
                    | "result" when not child ->
                        match property "exitCode" root with
                        | Some code when code.ValueKind = JsonValueKind.Number ->
                            match code.TryGetInt32() with
                            | true, code -> result <- Some code
                            | _ -> invalidOp "Invalid result exit code."
                        | _ -> invalidOp "Missing or invalid result exit code."
                    | "session.error" when not child -> invalidOp "The CLI reported a session error."
                    | _ -> ()
                with
                | :? JsonException as error ->
                    failure <- Some $"Invalid Copilot JSONL at line {lineNumber}: {error.Message}"
                | :? InvalidOperationException as error ->
                    failure <- Some $"Invalid Copilot JSONL at line {lineNumber}: {error.Message}"

        member _.IsFinished = failure.IsSome || result.IsSome

        member _.GetOutput() =
            failure |> Option.iter invalidOp
            match result with
            | Some 0 when lastRootIsFinal && responses.Count > 0 -> String.concat "\n" responses
            | Some 0 -> invalidOp "Copilot JSONL completed without a root final assistant response."
            | Some code -> invalidOp $"Copilot JSONL reported exit code {code}."
            | None -> invalidOp "Copilot JSONL ended without its terminal result."

module PromptBuilder =
    open XmlHelpers
    let instruction name text = xt name text
    let userContent name content = xt name content
    let truncatedContent name maxLen (content: string) =
        xt name (if content.Length > maxLen then content.[..maxLen-4] + "..." else content)
    let list name formatter items = xc name (items |> List.map formatter)
    let optionalEl name = function Some v -> [xt name v] | None -> []

module Git =
    let runProcess (command: string) (args: string) =
        try
            let psi = ProcessStartInfo(command, args, RedirectStandardOutput=true, RedirectStandardError=true,
                                       UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=Config.workDir)
            use p = Process.Start(psi)
            let output, error = p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync()
            if not (p.WaitForExit 60000) then
                p.Kill true
                if not (p.WaitForExit 10000) then failwith "Target repository Git process did not stop."
                failwith $"Target repository command timed out: {command} {args}"
            if not (Threading.Tasks.Task.WaitAll([|output :> Threading.Tasks.Task; error :> Threading.Tasks.Task|], 10000)) then
                failwith $"Target repository command output did not settle: {command} {args}"
            if p.ExitCode = 0 then Some(output.Result.Trim())
            else
                eprintfn "Target repository command failed (%d): %s %s\n%s" p.ExitCode command args error.Result
                None
        with :? System.ComponentModel.Win32Exception as error ->
            eprintfn "Target repository command could not start: %s: %s" command error.Message
            None

    let getHeadCommit () =
        match runProcess "git" "rev-parse --verify HEAD" with
        | Some head when Regex.IsMatch(head, @"\A[0-9a-f]{40}\z") -> Some head
        | _ -> invalidOp "Cannot verify the target repository's full HEAD; no approval can be recorded."

    let isApprovalAncestor (head: string) (commit: string) =
        Regex.IsMatch(commit, @"\A[0-9a-f]{40}\z")
        && Regex.IsMatch(head, @"\A[0-9a-f]{40}\z")
        && (runProcess "git" $"merge-base --is-ancestor {commit} {head}").IsSome

    let isWorktreeClean () =
        match runProcess "git" "status --porcelain=v1 --untracked-files=all" with
        | Some status -> status = ""
        | None -> invalidOp "Cannot verify target working-tree state."

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
