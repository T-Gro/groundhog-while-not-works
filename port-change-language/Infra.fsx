#!/usr/bin/env dotnet fsi
/// Infrastructure: Agent, Git, Harvest, Verify, Triage, Briefing, Streak, Bootstrap, Status
#r "nuget: Fli"

open System
open System.IO
open Fli

// ─── Domain types ───────────────────────────────────────────────

type Situation = Bootstrap | AllPass | FalseAllPass of int * int | Sprint

// ─── Agent (copilot CLI) ────────────────────────────────────────

module Agent =
    let Model = "claude-opus-4.7-1m-internal"
    let mutable private lastOut = ""
    let lastOutput () = lastOut

    let run (prompt: string) (title: string) =
        let sid = Guid.NewGuid().ToString()
        try
            let r = cli { Exec "copilot"
                          Arguments [| "-p"; prompt; "--name"; sid; "--allow-all"; "--no-ask-user"
                                       "-s"; "--no-color"; "--plain-diff"; "--model"; Model; "--stream"; "off" |] }
                    |> Command.execute
            lastOut <- r.Text |> Option.defaultValue ""
            if lastOut = "" then r.Error |> Option.iter (fun e -> if e <> "" then eprintfn $"  Agent '{title}': {e.[..min 400 (e.Length-1)]}")
        with ex -> eprintfn $"  Agent '{title}': {ex.Message}"; lastOut <- ""

    let resume sid feedback title =
        try let r = cli { Exec "copilot"
                          Arguments [| "-p"; feedback; "--resume"; sid; "--allow-all"; "--no-ask-user"
                                       "-s"; "--no-color"; "--plain-diff"; "--model"; Model; "--stream"; "off" |] }
                    |> Command.execute
            r.Text |> Option.defaultValue ""
        with _ -> ""

// ─── Git ────────────────────────────────────────────────────────

module Git =
    let head ()            = try (cli { Exec "git"; Arguments [|"rev-parse";"HEAD"|] } |> Command.execute).Text |> Option.defaultValue "HEAD" |> fun s -> s.Trim() with _ -> "HEAD"
    let push ()            = try (cli { Exec "git"; Arguments [|"push"|] } |> Command.execute).ExitCode = 0 |> fun ok -> if ok then printfn "  Pushed." else printfn "  ⚠ push failed"; ok with _ -> false
    let hasNewCommits bc   = head () <> bc

// ─── Harvest ────────────────────────────────────────────────────

module Harvest =
    let run sprintNum =
        let script = Path.Combine(Environment.CurrentDirectory, "_tools", "harvest_tests.py")
        let dbPath = Path.GetFullPath(Db.dbFile ())
        if File.Exists script then
            try let r = cli { Exec "python"; WorkingDirectory (Environment.CurrentDirectory)
                              Arguments [| script; dbPath; string sprintNum |] } |> Command.execute
                r.Error |> Option.iter (fun e -> for l in e.Split '\n' do let t = l.Trim() in if t <> "" then printfn "  %s" t)
            with ex -> eprintfn $"  harvest: {ex.Message}"
        else
            printfn "  (no _tools/harvest_tests.py — agent will bootstrap)"

// ─── Triage ─────────────────────────────────────────────────────

module Triage =
    let classify total (buckets: (string*int*int) list) =
        match buckets with
        | [] when total = 0 -> Bootstrap
        | [] ->
            let hh = try Db.historicalHigh () with _ -> total
            if total < hh * 80 / 100 then FalseAllPass(total, hh) else AllPass
        | _ -> Sprint

    let pickBucket (buckets: (string*int*int) list) streak =
        let skip = if streak >= 3 then min (streak - 2) (List.length buckets - 1) else 0
        buckets |> List.skip skip |> List.head |> fun (b,_,_) -> b

// ─── Briefing ───────────────────────────────────────────────────

module Briefing =
    let private trunc (s: string) n = if s.Length <= n then s else s.[..n/2] + "..." + s.[(s.Length-n/2)..]

    let build sourceDir sprintNum dbBrief allBuckets streak targetBucket =
        // Load project-specific template if it exists (created by bootstrap sprint)
        let tmplPath = Path.Combine(Environment.CurrentDirectory, ".github", "instructions", "sprint-briefing.md")
        let tmpl = if File.Exists tmplPath then File.ReadAllText tmplPath else ""

        // One-shot human nudge
        let nudgePath = Path.Combine(Environment.CurrentDirectory, "nudge.md")
        let nudge =
            if File.Exists nudgePath then
                let c = File.ReadAllText nudgePath in File.Delete nudgePath
                printfn $"  📌 Nudge: {c.[..min 80 (c.Length-1)]}..."
                $"\n<human_nudge>\n{c}\n</human_nudge>"
            else ""

        let stall = if streak >= 3 then $"\n<stall>ZERO commits ×{streak}. Try '{targetBucket}'.</stall>" else ""

        String.concat "\n" [
            $"Sprint {sprintNum}. Source reference: {sourceDir}"
            ""
            "<sprint_scope>"
            "THIS SPRINT = 2 MONTHS OF DEVELOPER TIME."
            "You have 1M tokens. Read ENTIRE source files. Port EVERYTHING."
            "MEDIOCRE: 2 commits. GOOD: 10+, 20+ test flips. GREAT: 50+."
            "</sprint_scope>"
            ""
            "<rules>"
            "EVERY COMMIT MUST FLIP ≥1 TEST. Format: 'area: desc (N/M → N+K/M)'"
            "BANNED: refactoring without test flips · docs instead of porting."
            "</rules>"
            ""
            if tmpl <> "" then $"<project>\n{tmpl}\n</project>\n"
            $"<test_status>\n{dbBrief}\n</test_status>"
            $"\n<failing_buckets>\n{allBuckets}\n</failing_buckets>"
            "\n<persistence>Changed <200 lines? You barely started.</persistence>"
            stall; nudge ]

// ─── Bootstrap ──────────────────────────────────────────────────

module Bootstrap =
    let prompt sourceDir sprintNum =
        String.concat "\n" [
            $"Sprint {sprintNum}: BOOTSTRAP."
            $"You are in a target repository that is being ported FROM {sourceDir}."
            ""
            "Your job: set up the porting infrastructure. Do ALL of the following:"
            ""
            "1. READ the source repo to understand what languages, build system, and tests it uses."
            "2. READ the target repo to understand what exists so far."
            "3. CREATE _tools/harvest_tests.py — a script that:"
            "   - Runs ALL test suites in the target repo"
            "   - Writes results to a SQLite DB (path = sys.argv[1], sprint = sys.argv[2])"
            "   - Schema: buckets(id, layer, total_tests, passing, failing) + tests(id, bucket_id, status, error_message)"
            "4. CREATE .github/instructions/sprint-briefing.md — a template with:"
            "   - Project-specific guidance for the porting agent"
            "   - What files to read, how to build/test, what the priorities are"
            "   - Use {{sprint}}, {{source_dir}} placeholders"
            "5. RUN the harvest script to verify it works."
            "6. COMMIT everything."
            ""
            "You MUST commit. Do NOT push." ]

// ─── Verify ─────────────────────────────────────────────────────

module Verify =
    let runAll baseCommit =
        let verifiers = Verifiers.listAll ()
        if verifiers.IsEmpty then (true, "")
        else
            let results =
                verifiers |> List.map (fun v -> async {
                    let (p, o, _) = Verifiers.runOne v baseCommit
                    return (v, p, o) })
                |> Async.Parallel |> Async.RunSynchronously |> Array.toList
            let failed = results |> List.filter (fun (_, p, _) -> not p)
            if failed.IsEmpty then (true, "")
            else (false, failed |> List.map (fun (v,_,o) -> $"=== {v} ===\n{o.[..min 2000 (o.Length-1)]}") |> String.concat "\n")

module Verifiers =
    let private dir = Path.Combine(__SOURCE_DIRECTORY__, "verifiers")
    let listAll () = if Directory.Exists dir then Directory.GetFiles(dir, "*.md") |> Array.map Path.GetFileNameWithoutExtension |> Array.sort |> Array.toList else []
    let private read n = let p = Path.Combine(dir, n + ".md") in if File.Exists p then File.ReadAllText p else ""

    let runOne name baseCommit =
        let preamble = $"VERIFIER. Scope: git diff {baseCommit}..HEAD\nOutput VERIFY_PASSED or VERIFY_FAILED."
        let prompt = preamble + "\n\n" + read name
        Agent.run prompt $"Verify-{name}"
        let out = Agent.lastOutput ()
        let passed = out.Contains "VERIFY_PASSED" && not (out.Contains "VERIFY_FAILED")
        (passed, out, "")

// ─── Streak (file-backed, survives restarts) ────────────────────

module Streak =
    let private file () = Path.Combine(Db.runtimeDir (), "no_commit_streak.txt")
    let get ()    = let p = file () in if File.Exists p then match Int32.TryParse(File.ReadAllText(p).Trim()) with true, n -> n | _ -> 0 else 0
    let set n     = File.WriteAllText(file (), string n)
    let bump ()   = let n = get () + 1 in set n; n
    let reset ()  = set 0

// ─── Status ─────────────────────────────────────────────────────

module Status =
    let show () =
        let db = Db.open' ()
        let pp, pt = Db.passRate db
        let buckets = Db.failingBuckets db
        let sn = Db.sprintNum db
        db.Close ()
        printfn $"Sprint {sn} | {pp}/{pt} passing ({if pt > 0 then 100*pp/pt else 0}%%)"
        if buckets.Length > 0 then
            printfn "\nTop failing:"
            for (b, f, t) in buckets |> List.truncate 10 do printfn $"  {b}: {f}/{t}"

    let watch interval =
        let cts = new Threading.CancellationTokenSource()
        Console.CancelKeyPress.Add(fun a -> a.Cancel <- true; cts.Cancel())
        try while not cts.IsCancellationRequested do Console.Clear(); show (); printfn $"\n(every {interval}s)"
                                                     try cts.Token.WaitHandle.WaitOne(interval*1000) |> ignore with :? OperationCanceledException -> ()
        with :? OperationCanceledException -> (); printfn "\nStopped."
