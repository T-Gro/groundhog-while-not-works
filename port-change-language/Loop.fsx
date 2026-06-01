#!/usr/bin/env dotnet fsi
/// ╔══════════════════════════════════════════════════════════════╗
/// ║  ralph-port — Autonomous porting convergence loop           ║
/// ║                                                              ║
/// ║  while failing_tests > 0:                                    ║
/// ║      harvest()           # measure                           ║
/// ║      triage()            # classify situation                ║
/// ║      implement(briefing) # run agent with context            ║
/// ║      verify(diff)        # quality gates                     ║
/// ║      record(delta)       # persist + push                    ║
/// ║                                                              ║
/// ║  Generic for any porting task. Project-specific config       ║
/// ║  lives in the TARGET repo, not here.                         ║
/// ╚══════════════════════════════════════════════════════════════╝

#load "ProjectConfig.fsx"
#load "SprintDb.fsx"
#r "nuget: Fli"

open System
open System.IO
open Fli
open ProjectConfig
open SprintDb

// ═══════════════════════════════════════════════════════════════
//  INFRASTRUCTURE — thin wrappers over external tools
// ═══════════════════════════════════════════════════════════════

module Agent =
    let Model = "claude-opus-4.7-1m-internal"

    let run prompt title (resumeId: string option) =
        let sid = resumeId |> Option.defaultWith Guid.NewGuid().ToString
        let flag = if resumeId.IsSome then "--resume" else "--name"
        try
            let r = cli { Exec "copilot"
                          Arguments [| "-p"; prompt; flag; sid; "--allow-all"; "--no-ask-user"
                                       "-s"; "--no-color"; "--plain-diff"; "--model"; Model; "--stream"; "off" |] }
                    |> Command.execute
            let out = r.Text |> Option.defaultValue ""
            if out = "" then r.Error |> Option.iter (fun e -> if e <> "" then eprintfn $"  Agent '{title}': {e.[..min 400 (e.Length-1)]}")
            (out, sid)
        with ex -> eprintfn $"  Agent '{title}': {ex.Message}"; ("", sid)

    let resume sid feedback title = run feedback title (Some sid) |> fst

module Git =
    let head ()             = try (cli { Exec "git"; Arguments [|"rev-parse";"HEAD"|] } |> Command.execute).Text |> Option.defaultValue "HEAD" |> fun s -> s.Trim() with _ -> "HEAD"
    let push ()             = try (cli { Exec "git"; Arguments [|"push"|] } |> Command.execute).ExitCode = 0 with _ -> false
    let hasNewCommits bc    = head () <> bc

module Beads =
    let private bd () = let k = @"Q:\.tools\beads\bd.exe" in if File.Exists k then k else "bd"
    let mutable private warned = false
    let mutable private epic = ""

    let run args =
        try
            let p = Environment.GetEnvironmentVariable "PATH"
            let d = @"Q:\.tools\dolt\dolt-windows-amd64\bin"
            if not (p.Contains d) then Environment.SetEnvironmentVariable("PATH", $"{d};{p}")
            Environment.SetEnvironmentVariable("BEADS_DIR", Path.Combine(__SOURCE_DIRECTORY__, ".beads"))
            (cli { Exec (bd()); Arguments (args |> Array.ofList) } |> Command.execute).Text |> Option.defaultValue ""
        with ex -> if not warned then eprintfn $"  beads: {ex.Message}"; warned <- true; ""

    let ensureEpic name =
        if epic = "" then
            let e = run ["query"; "type=epic AND status!=closed"; "--json"]
            if e.Contains name then
                let m = Text.RegularExpressions.Regex.Match(e, "\"id\":\\s*\"([^\"]+)\"")
                if m.Success then epic <- m.Groups.[1].Value
            if epic = "" then epic <- (run ["create"; $"--title={name}"; "--type=epic"]).Trim()
        epic

    let sprint n bucket pre = (run ["create"; $"--title=S{n}: {bucket}"; $"--description={pre}"; "--type=task"; $"--labels=sprint:{n},bucket:{bucket}"; if epic <> "" then $"--parent={epic}"]).Trim()
    let claim id            = run ["update"; id; "--claim"] |> ignore
    let note id text        = run ["comments"; "add"; id; text] |> ignore
    let close id ok reason  = run ["close"; id; $"--reason={(if ok then "" else "FAILED: ")}{reason}"; if not ok then "--add-label"; if not ok then "failed"] |> ignore
    let remember text       = run ["remember"; text] |> ignore

module Verifiers =
    let private dir   = Path.Combine(__SOURCE_DIRECTORY__, "verifiers")
    let listAll ()    = if Directory.Exists dir then Directory.GetFiles(dir, "*.md") |> Array.map Path.GetFileNameWithoutExtension |> Array.sort |> Array.toList else []
    let private read n = Path.Combine(dir, n + ".md") |> fun p -> if File.Exists p then File.ReadAllText p else ""

    let private verdict (o: string) sid n =
        let p, f = o.Contains "VERIFY_PASSED", o.Contains "VERIFY_FAILED"
        match p, f with
        | true, false  -> (true, o)
        | false, true  -> (false, o)
        | _ -> let c = Agent.resume sid "Output VERIFY_PASSED or VERIFY_FAILED." $"V-{n}"
               (c.Contains "VERIFY_PASSED" && not (c.Contains "VERIFY_FAILED"), o)

    let runOne name baseCommit =
        let preamble = $"VERIFIER. Scope: git diff {baseCommit}..HEAD. Output VERIFY_PASSED or VERIFY_FAILED."
        let prompt = if name.Contains "EXPERT-REVIEW" then read name else preamble + "\n\n" + read name
        let title = if name.Contains "EXPERT-REVIEW" then "review-expert" else $"Verify-{name}"
        let (out, sid) = Agent.run prompt title None
        let (passed, full) = verdict out sid name
        (passed, full, sid)

    let rerun sid name =
        let out = Agent.resume sid "Re-review. Output VERIFY_PASSED or VERIFY_FAILED." $"Re-{name}"
        verdict out sid name

// ═══════════════════════════════════════════════════════════════
//  DOMAIN — Sprint phases as composable functions
// ═══════════════════════════════════════════════════════════════

module Sprint =
    let trunc (s: string) n = if s.Length <= n then s else s.[..n/2] + "..." + s.[(s.Length-n/2)..]

    /// Harvest: measure current test state via project's harvest script.
    let harvest (cfg: Config) sprintNum =
        let db = Path.GetFullPath(SprintDb.dbPath (SprintDb.projectKey (Config.targetDir ())))
        let script = Path.Combine(Config.targetDir (), "_tools", "harvest_tests.py")
        if File.Exists script then
            try let r = cli { Exec "python"; WorkingDirectory (Config.targetDir ()); Arguments [|script; db; string sprintNum|] } |> Command.execute
                r.Error |> Option.iter (fun e -> for l in e.Split '\n' do let t = l.Trim() in if t <> "" then printfn "  %s" t)
            with ex -> eprintfn $"  harvest: {ex.Message}"
        else Agent.run $"HARVEST: Run ALL tests, record in {db}. Sprint {sprintNum}." "Harvest" None |> ignore

    /// Triage: classify the situation from test results.
    let triage total buckets dbPath =
        match buckets with
        | [] when total = 0 -> "bootstrap"
        | [] ->
            let hh = try let c = SprintDb.init dbPath in let cmd = c.CreateCommand() in cmd.CommandText <- "SELECT MAX(total_tests) FROM sprint"
                         let r = cmd.ExecuteScalar() in c.Close(); match r with :? int64 as v -> int v | :? int as v -> v | _ -> total
                     with _ -> total
            if total < hh * 80 / 100 then "false-all-pass" else "all-pass"
        | _ -> "sprint"

    /// Briefing: build the implementor prompt from generic framing + project template.
    let briefing (cfg: Config) sprintNum dbBrief allBuckets (prevFail: string option) =
        let prev = match prevFail with Some c -> $"\n<previous_failure>\n{trunc c 3000}\n</previous_failure>" | None -> ""
        let tmpl = Path.Combine(Config.targetDir (), ".github", "instructions", "sprint-briefing.md")
        let proj = if File.Exists tmpl then
                       let raw = File.ReadAllText tmpl
                       raw.Replace("{{sprint}}", string sprintNum).Replace("{{source_lang}}", cfg.SourceLang)
                          .Replace("{{target_lang}}", cfg.TargetLang).Replace("{{source_dir}}", cfg.SourceDir)
                          .Replace("{{project}}", cfg.ProjectName)
                   else ""
        let nudge = let p = Path.Combine(Config.targetDir (), "nudge.md")
                    if File.Exists p then let c = File.ReadAllText p in File.Delete p; printfn $"  📌 Nudge: {c.[..min 80 (c.Length-1)]}..."; $"\n<human_nudge>\n{c}\n</human_nudge>" else ""
        String.concat "\n" [
            $"Sprint {sprintNum}. Port {cfg.SourceLang} → {cfg.TargetLang}."
            "\n<sprint_scope>"
            "THIS SPRINT = 2 MONTHS OF DEVELOPER TIME."
            "AI agents MASSIVELY UNDERESTIMATE what they can do. You have 1M tokens."
            "MEDIOCRE: 2 commits. GOOD: 10+, 20+ test flips. GREAT: 50+ flips."
            "</sprint_scope>"
            "\n<parity_rules>"
            "EVERY COMMIT MUST FLIP ≥1 TEST. Format: 'area: desc (N/M → N+K/M)'"
            "BANNED: refactoring without test flips · docs instead of porting."
            "</parity_rules>"
            if proj <> "" then $"\n<project>\n{proj}\n</project>"
            $"\nSource: {cfg.SourceDir}. You MUST commit. Do NOT push."
            $"\n<test_status>\n{dbBrief}\n</test_status>"
            $"\n<failing_buckets>\n{allBuckets}\n</failing_buckets>"
            "\n<persistence>Changed <200 lines? You barely started. Keep going.</persistence>"
            nudge; prev ]

    /// Implement: run the agent and capture output.
    let implement prompt sprintNum pkey =
        let bc = Git.head ()
        let (out, sid) = Agent.run prompt $"Impl-S{sprintNum}" None
        try SprintDb.writeLog pkey sprintNum out |> fun p -> printfn $"  📝 {p} ({out.Length} chars)" with _ -> ()
        (bc, sid, out, Git.hasNewCommits bc)

    /// Verify: run all verifiers, fix failures, recheck.
    let verify baseCommit sid bead maxRetries =
        let mutable retries, passed, fail = 0, Verifiers.listAll().IsEmpty, ""
        while retries < maxRetries && not passed do
            Beads.note bead $"verify:{retries+1}"
            let rs = Verifiers.listAll() |> List.map (fun v -> async {
                          let (p,o,s) = Verifiers.runOne v baseCommit in return (v,p,o,s) })
                     |> Async.Parallel |> Async.RunSynchronously |> Array.toList
            let bad = rs |> List.filter (fun (_,p,_,_) -> not p)
            if bad.IsEmpty then passed <- true
            else
                fail <- bad |> List.map (fun (v,_,o,_) -> $"=== {v} ===\n{trunc o 2000}") |> String.concat "\n\n"
                Agent.resume sid fail "Fix" |> ignore
                if (bad |> List.map (fun (v,_,_,s) -> async { return Verifiers.rerun s v |> fst })
                    |> Async.Parallel |> Async.RunSynchronously |> Array.forall id) then passed <- true
                retries <- retries + 1
        (passed, fail)

    /// Port traceability: check for removed "// Ported from:" markers (opt-in).
    let checkTraceability baseCommit sid =
        let indexDb = Path.Combine(Config.targetDir (), "pyright-source-index.db")
        if not (File.Exists indexDb) then true
        else
            try let diff = (cli { Exec "git"; Arguments [|"diff"; baseCommit+"..HEAD";"--";"internal/"|] } |> Command.execute).Text |> Option.defaultValue ""
                let lines = diff.Split '\n'
                let removed = [ for l in lines do let t = l.TrimStart()
                                if t.StartsWith "-" && not (t.StartsWith "---") then
                                    let c = t.[1..] in if c.Contains "// Ported from:" || c.Contains "// TODO(port):" then
                                        let mk = c.Trim()
                                        if not (lines |> Array.exists (fun x -> let xt = x.TrimStart() in xt.StartsWith "+" && not (xt.StartsWith "+++") && xt.[1..].Trim() = mk)) then yield mk ]
                if removed.IsEmpty then true
                else printfn $"  🚨 {removed.Length} traceability markers removed"
                     Agent.resume sid ($"Restore:\n{removed |> List.map (fun r -> $"  {r}") |> String.concat "\n"}") "Restore" |> ignore
                     false // conservative — let next sprint recheck
            with _ -> true

// ═══════════════════════════════════════════════════════════════
//  APPLICATION — The convergence loop
// ═══════════════════════════════════════════════════════════════

module Loop =
    let private key () = SprintDb.projectKey (Config.targetDir ())

    let private init () =
        let cfg = Config.require ()
        let db = SprintDb.dbPath (key ())
        if not (File.Exists db) then SprintDb.init db |> fun c -> SprintDb.initSprint c 0 "" 0 0; c.Close()
        Beads.ensureEpic cfg.ProjectName |> ignore; cfg

    /// ── One sprint ──────────────────────────────────────────────
    let step maxRetries prevFail =
        let cfg = init ()
        let db = SprintDb.dbPath (key ())
        let conn = SprintDb.init db
        let sn = SprintDb.sprintNum conn
        conn.Close()

        // Harvest
        printfn "  📊 Harvesting..."
        Sprint.harvest cfg (max sn 0)

        let c2 = SprintDb.init db
        let next = sn + 1
        let (pp, pt) = SprintDb.passRate c2
        let ranked = SprintDb.rankedBuckets c2

        match Sprint.triage pt ranked db with
        | "bootstrap" ->
            c2.Close(); printfn $"S{next} | Bootstrap"
            let bead = Beads.sprint next "bootstrap" "Empty" in Beads.claim bead
            SprintDb.init db |> fun c -> SprintDb.initSprint c next "bootstrap" 0 0; c.Close()
            Agent.run $"BOOTSTRAP. {cfg.ProjectName}: {cfg.SourceLang}→{cfg.TargetLang}. Source: {cfg.SourceDir}. Set up tests. Commit." $"S{next}" None |> ignore
            Git.push () |> ignore; Beads.close bead true "Bootstrap"
            (true, "BOOTSTRAP")

        | "false-all-pass" ->
            c2.Close(); printfn "  🚨 FALSE ALL-PASS — build broken!"
            Agent.run "BUILD REPAIR: fix all compile errors. Search for <<<<<<< in source." $"Fix-S{next}" None |> ignore
            (false, "BUILD_REPAIR")

        | "all-pass" -> c2.Close(); printfn "✅ All pass!"; (true, "ALL_PASS")

        | _ (* sprint *) ->
            let bucketStr = ranked |> List.map (fun (b,l,f,t) -> $"  {b} ({l}): {f}/{t}") |> String.concat "\n"
            let brief = SprintDb.briefing c2 in c2.Close()
            printfn $"S{next} | {pp}/{pt} | {ranked.Length} failing"

            // Bucket rotation on stall
            let streak = SprintDb.getStreak (key ())
            let skip = if streak >= 3 then min (streak-2) (List.length ranked - 1) else 0
            let top = ranked |> List.skip skip |> List.head |> fun (b,_,_,_) -> b
            let stall = if streak >= 3 then $"\n<stall>ZERO commits ×{streak}. Try '{top}'.</stall>" else ""

            let prompt = Sprint.briefing cfg next brief bucketStr prevFail + stall
            let bead = Beads.sprint next top $"Pre:{pp}/{pt}" in Beads.claim bead
            SprintDb.init db |> fun c -> SprintDb.initSprint c next top pp pt; c.Close()

            // Implement
            Beads.note bead $"impl streak={streak}"
            let (bc, sid, implOut, commits) = Sprint.implement prompt next (key ())
            if commits then SprintDb.resetStreak (key ()); Beads.note bead $"impl:done {Git.head().[..7]}"
            else
                let s = SprintDb.bumpStreak (key ())
                Beads.note bead $"NO_COMMITS streak={s}"
                printfn $"  ⚠ No commits (streak={s})"
                if implOut.Length > 0 then printfn $"  ── tail ──\n{implOut.[max 0 (implOut.Length-800)..]}\n  ──────────"

            // Harvest after
            Beads.note bead "harvest"
            Sprint.harvest cfg next

            // Verify
            let (vOk, lastFail) = Sprint.verify bc sid bead maxRetries

            // Record
            let fc = SprintDb.init db in let (fp,ft) = SprintDb.passRate fc in let d = fp-pp
            SprintDb.finalize fc fp ft; fc.Close()
            SprintDb.archiveAndReset (key ()) next
            let msg = $"{fp}/{ft} d={d}"
            Beads.note bead msg

            // Traceability (opt-in)
            let tOk = Sprint.checkTraceability bc sid
            let ok = vOk && tOk && d > 0

            if ok then
                Beads.close bead true msg; printfn $"  ✅ {msg}"
                Agent.run $"Sprint {next} done. Capture learnings." $"Knowledge-S{next}" None |> ignore
                if Git.push () then printfn "  Pushed." else printfn "  ⚠ push failed"
                (true, msg)
            else Beads.close bead false msg; printfn $"  ❌ {msg}"; (false, lastFail)

    /// ── Main loop ───────────────────────────────────────────────
    let run maxRetries =
        let cfg = init ()
        printfn $"═══ {cfg.ProjectName}: {cfg.SourceLang} → {cfg.TargetLang} ═══"
        let mutable go, prev = true, None
        let mutable errs, fails, reviewN = 0, 0, 0
        Console.CancelKeyPress.Add(fun a -> a.Cancel <- true; go <- false; printfn "\n⏹ Stopping...")

        while go do
            try
                reviewN <- reviewN + 1
                if reviewN >= 7 then
                    reviewN <- 0
                    printfn "── Review ──"
                    let (rv, _) = Agent.run $"Review {Config.targetDir ()}. Source: {cfg.SourceDir}." "review" None
                    Beads.remember $"Review: {rv.[..min 500 (rv.Length-1)]}"
                    printfn "── Refactor ──"
                    let (_, rSid) = Agent.run $"REFACTOR. Tests must not decrease.\n{rv.[..min 4000 (rv.Length-1)]}" "Refactor" None
                    let bc = Git.head ()
                    Verifiers.listAll() |> List.map (fun v -> async { return Verifiers.runOne v bc })
                    |> Async.Parallel |> Async.RunSynchronously |> Array.toList
                    |> List.filter (fun (_,p,_,_) -> not p)
                    |> function
                       | [] -> ()
                       | bad -> Agent.resume rSid (bad |> List.map (fun (v,_,o,_) -> $"=== {v} ===\n{o.[..min 2000 (o.Length-1)]}") |> String.concat "\n") "Fix" |> ignore
                    Git.push () |> ignore; printfn "── Done ──"
                else
                    let (ok, s) = step maxRetries prev
                    errs <- 0
                    match s with
                    | "ALL_PASS" -> go <- false
                    | _ when ok -> prev <- None; fails <- 0
                    | _ ->
                        prev <- Some s; fails <- fails + 1
                        if fails >= 3 then printfn $"  ⏳ {fails} fails — 5 min"; Threading.Thread.Sleep(5*60*1000)
                        if fails >= 10 then printfn "  ⏳ 10 fails — 30 min"; Threading.Thread.Sleep(25*60*1000)
            with ex ->
                errs <- errs + 1; eprintfn $"  Error ({errs}): {ex.Message}"; Beads.remember $"Error: {ex.Message}"
                if errs >= 5 then eprintfn "  5 errors — 10 min"; Threading.Thread.Sleep(10*60*1000); errs <- 0

    let status () =
        match Config.load () with
        | Some c -> let k = key () in printfn $"{c.ProjectName}: {c.SourceLang} → {c.TargetLang}"
                    let db = SprintDb.dbPath k in if File.Exists db then SprintDb.init db |> fun cn -> printfn $"{SprintDb.dashboard cn}"; cn.Close()
                    let t = SprintDb.trend k in if t.Length > 0 then printfn ""; printfn $"{SprintDb.renderChart t 30}"
        | None -> printfn "No project.json."

    let watch interval =
        let cts = new Threading.CancellationTokenSource()
        Console.CancelKeyPress.Add(fun a -> a.Cancel <- true; cts.Cancel())
        try while not cts.IsCancellationRequested do Console.Clear(); status (); printfn $"\n(every {interval}s)"
                                                     try cts.Token.WaitHandle.WaitOne(interval*1000) |> ignore with :? OperationCanceledException -> ()
        with :? OperationCanceledException -> (); printfn "\nStopped."

// ═══════════════════════════════════════════════════════════════
//  CLI
// ═══════════════════════════════════════════════════════════════

let retries args = args |> List.tryFind (fun (s:string) -> s.StartsWith "--retries=") |> Option.map (fun s -> int(s.Split('=').[1])) |> Option.defaultValue 3

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| "run"    :: r -> Loop.run (retries r)
| "step"   :: r -> Loop.step (retries r) None |> ignore
| ["status"]    -> Loop.status ()
| "watch"  :: r -> Loop.watch (r |> List.tryHead |> Option.map int |> Option.defaultValue 30)
| _ ->
    printfn "ralph-port — autonomous porting convergence loop"
    printfn ""
    printfn "  run   [--retries=N]   Loop until all tests pass. Ctrl+C safe."
    printfn "  step  [--retries=N]   One sprint."
    printfn "  status                Current state + progress chart."
    printfn "  watch [seconds]       Live dashboard (default: 30s)."
