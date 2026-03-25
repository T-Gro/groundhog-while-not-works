#!/usr/bin/env dotnet fsi

/// ralph-port — Autonomous porting loop with backpressure.
/// Run from target project root. Needs project.json.

#load "ProjectConfig.fsx"
#load "TestResultsDb.fsx"

#r "nuget: Fli"

open System
open System.IO
open Fli
open ProjectConfig.ProjectConfig
open TestResultsDb.TestResultsDb

module Beads =
    let private bd () =
        let known = @"Q:\.tools\beads\bd.exe"
        if File.Exists known then known else "bd"

    let run (args: string list) : string =
        try
            let result = cli { Exec (bd()); Arguments (args |> Array.ofList); WorkingDirectory __SOURCE_DIRECTORY__ } |> Command.execute
            result.Text |> Option.defaultValue ""
        with ex -> eprintfn $"bd: {ex.Message}"; ""

    let create title desc = run ["create"; $"--title={title}"; $"--description={desc}"; "--type=task"; "--priority=1"] |> fun s -> s.Trim()
    let claim id = run ["update"; id; "--claim"] |> ignore
    let close id reason = run ["close"; id; $"--reason={reason}"] |> ignore
    let note id text = run ["note"; id; text] |> ignore
    let remember text = run ["remember"; text] |> ignore

module Agent =
    let private model = "claude-opus-4.6"

    let run (prompt: string) (title: string) (resumeId: string option) : string * string =
        let sid = resumeId |> Option.defaultWith (fun () -> Guid.NewGuid().ToString())
        try
            let result =
                cli { Exec "copilot"; Arguments [| "-p"; prompt; "--model"; model; "--resume"; sid; "--allow-all"; "--no-ask-user"; "--autopilot"; "--no-color"; "--plain-diff"; "--stream"; "off" |] }
                |> Command.execute
            (result.Text |> Option.defaultValue "", sid)
        with ex -> eprintfn $"Agent '{title}': {ex.Message}"; ("", sid)

    let resume sid feedback title =
        let (out, _) = run feedback title (Some sid)
        out

module Verifiers =
    let private dir = Path.Combine(__SOURCE_DIRECTORY__, "verifiers")

    let listAll () =
        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.md") |> Array.map Path.GetFileNameWithoutExtension |> Array.sort |> Array.toList
        else []

    let private getPrompt name =
        let p = Path.Combine(dir, name + ".md")
        if File.Exists p then File.ReadAllText p else ""

    let private preamble baseCommit = String.concat "\n" [
        "You are a VERIFIER. Review code, do NOT change it."
        "This is iterative work — judge THIS SPRINT's delta only, not the whole project."
        $"EXACT SCOPE: only changes between {baseCommit} and HEAD. Nothing else."
        $"  Diff: git diff {baseCommit}..HEAD"
        $"  Log: git log --oneline {baseCommit}..HEAD"
        "Do NOT review code that existed before this sprint. Do NOT comment on pre-existing issues."
        "Output VERIFY_PASSED or VERIFY_FAILED on its own line at the end."
        "If FAILED, write specific actionable fix instructions for the implementor." ]

    let private parseVerdict (output: string) sid name =
        let p = output.Contains "VERIFY_PASSED"
        let f = output.Contains "VERIFY_FAILED"
        if p && not f then (true, output)
        elif f && not p then (false, output)
        else
            let c = Agent.resume sid "Ambiguous. Output exactly VERIFY_PASSED or VERIFY_FAILED, nothing else." $"Disambiguate-{name}"
            (c.Contains "VERIFY_PASSED" && not (c.Contains "VERIFY_FAILED"), output)

    let private title (name: string) = if name.Contains "EXPERT-REVIEW" then "review-expert" else $"Verify-{name}"

    let runVerifier (name: string) (baseCommit: string) : bool * string * string =
        let prompt = if name.Contains "EXPERT-REVIEW" then getPrompt name else (preamble baseCommit) + "\n\n" + getPrompt name
        let (out, sid) = Agent.run prompt (title name) None
        let (passed, fullOut) = parseVerdict out sid name
        (passed, fullOut, sid)

    let resumeVerifier sid name =
        let out = Agent.resume sid "Implementor fixed the issues. Re-review. Output VERIFY_PASSED or VERIFY_FAILED." $"Re-{title name}"
        let (passed, fullOut) = parseVerdict out sid name
        (passed, fullOut)

module ConvergenceLoop =
    let private key () = projectKey (targetDir())
    let private trunc (s: string) n = if s.Length <= n then s else s.[..n/2] + "..." + s.[(s.Length-n/2)..]

    let private ensureInit () =
        let config = require()
        let db = currentDbPath (key())
        if not (File.Exists db) then let c = initSchema db in initSprint c 0 "" 0 0; c.Close()
        config

    let private buildBriefing config sprintNum bucket dbBriefing failures totalTests alts prevFailure =
        let planPath = Path.Combine(targetDir(), "porting-plan.md")
        let plan =
            if File.Exists planPath then
                let s = File.ReadAllText planPath
                if s.Length > 12000 then s.[..12000] else s
            else ""
        let failLines = failures |> List.truncate 25 |> List.map (fun (f,e) -> $"  {f}: {e}") |> String.concat "\n"
        let altLines = if alts = [] then "" else alts |> List.map (fun b -> $"  - {b}") |> String.concat "\n" |> sprintf "\nAlternative buckets (pick if stuck):\n%s"
        let prevBlock = match prevFailure with Some ctx -> $"\n<previous_failure>\n{trunc ctx 3000}\n</previous_failure>" | None -> ""
        String.concat "\n" [
            $"Sprint {sprintNum}. Target bucket: {bucket}. Source: {config.SourceDir}."
            "Incremental port — improve test pass rate piece by piece. Not a one-shot."
            "Your focus: unpushed commits. Build, test, commit. Push happens only on full success."
            "Read .github/copilot-instructions.md and adr/INDEX.md."
            $"\n<tests total=\"{totalTests}\">\n{dbBriefing}\n</tests>"
            $"\n<failing>\n{failLines}\n</failing>"
            altLines; prevBlock
            $"\nFix failures in '{bucket}'. Build, test, commit."
        ]

    let step maxRetries prevFailure : bool * string =
        let config = ensureInit ()
        let db = currentDbPath (key())
        let conn = initSchema db
        let sNum = currentSprintNum conn
        let next = sNum + 1
        let (pp, pt) = passRate conn
        let ranked = bucketsRanked conn
        match ranked with
        | [] -> conn.Close(); printfn "All pass!"; (true, "ALL_PASS")
        | (bucket,layer,failing,bt) :: rest ->
            let alts = rest |> List.truncate 4 |> List.map (fun (b,l,f,_) -> $"{b} ({l},{f})")
            let fails = failingInBucket conn bucket 30
            let brief = briefing conn
            conn.Close()
            printfn $"S{next} | {pp}/{pt} | {bucket} ({failing} failing)"

            let prompt = buildBriefing config next bucket brief fails pt alts prevFailure
            let bead = Beads.create $"S{next}: {bucket}" $"{failing} failures"
            Beads.claim bead; Beads.note bead $"Pre:{pp}/{pt}"
            let sc = initSchema db in initSprint sc next bucket pp pt; sc.Close()

            // Record base commit BEFORE implementor runs — this defines the sprint's diff scope
            let baseCommit =
                try (cli { Exec "git"; Arguments [|"rev-parse"; "HEAD"|] } |> Command.execute).Text |> Option.defaultValue "HEAD" |> fun s -> s.Trim()
                with _ -> "HEAD"

            let (_, sid) = Agent.run prompt $"Impl-S{next}" None
            let mutable retries = 0
            let mutable passed = false
            let mutable lastFail = ""

            while retries < maxRetries && not passed do
                let results = Verifiers.listAll() |> List.map (fun v -> async {
                    let (vp,vo,vsid) = Verifiers.runVerifier v baseCommit
                    let verdict = if vp then "OK" else "FAIL"
                    Beads.note bead $"{v}:{verdict}"
                    return (v,vp,vo,vsid) }) |> Async.Parallel |> Async.RunSynchronously |> Array.toList
                let failed = results |> List.filter (fun (_,vp,_,_) -> not vp)
                if failed.IsEmpty then passed <- true
                else
                    let fb = failed |> List.map (fun (v,_,vo,_) -> $"=== {v} ===\n{trunc vo 2000}") |> String.concat "\n\n"
                    lastFail <- fb
                    Agent.resume sid fb $"Fix-S{next}" |> ignore
                    let rechecks = failed |> List.map (fun (v,_,_,vsid) -> async { let (r,_) = Verifiers.resumeVerifier vsid v in return (v,r) }) |> Async.Parallel |> Async.RunSynchronously
                    if rechecks |> Array.forall snd then passed <- true
                    retries <- retries + 1

            let fc = initSchema db in let (fp,ft) = passRate fc in let d = fp-pp in finalizeSprint fc fp ft; fc.Close()
            archiveAndReset (key()) next
            let msg = $"{fp}/{ft} d={d}"
            Beads.note bead msg
            if passed && d > 0 then
                Beads.close bead msg; printfn $"  OK: {msg}"
                // Push on success
                let pushResult = try (cli { Exec "git"; Arguments [|"push"|] } |> Command.execute).ExitCode with _ -> 1
                if pushResult <> 0 then printfn "  ⚠ git push failed — commits are local only"
                else printfn "  Pushed."
                // Knowledge capture with explicit diff scope
                let capturePrompt = String.concat "\n" [
                    $"Sprint {next} succeeded."
                    $"EXACT SCOPE: git diff {baseCommit}..HEAD"
                    $"Log: git log --oneline {baseCommit}..HEAD"
                    "Review only these commits. Capture non-trivial learnings if any. Say 'No learnings.' if none." ]
                Agent.run capturePrompt $"Knowledge-S{next}" None |> ignore
                (true, msg)
            else
                Beads.note bead $"Fail:{trunc lastFail 500}"; printfn $"  Fail: {msg}"; (false, lastFail)

    let run maxRetries =
        let config = ensureInit ()
        printfn $"=== {config.ProjectName}: {config.SourceLang} -> {config.TargetLang} ==="
        let mutable go = true
        let mutable prev: string option = None
        Console.CancelKeyPress.Add(fun a -> a.Cancel <- true; go <- false; printfn "\nStopping...")
        while go do
            let (ok, s) = step maxRetries prev
            if s = "ALL_PASS" then go <- false
            elif ok then prev <- None
            else prev <- Some s

    let status () =
        match load() with
        | Some c ->
            printfn $"{c.ProjectName}: {c.SourceLang} -> {c.TargetLang}"
            let db = currentDbPath (key())
            if File.Exists db then let cn = initSchema db in printfn $"{briefing cn}"; cn.Close()
        | None -> printfn "No project.json."

let retries rest = rest |> List.tryFind (fun (s:string) -> s.StartsWith "--retries=") |> Option.map (fun s -> int(s.Split('=').[1])) |> Option.defaultValue 3

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| "run" :: r -> ConvergenceLoop.run (retries r)
| "step" :: r -> ConvergenceLoop.step (retries r) None |> ignore
| ["status"] -> ConvergenceLoop.status ()
| _ ->
    printfn "ralph-port  run [--retries=N] | step [--retries=N] | status"
    printfn "Run from target project dir (needs project.json)."