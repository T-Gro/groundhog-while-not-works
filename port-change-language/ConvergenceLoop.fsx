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

    let mutable private warned = false
    let mutable private epicId = ""

    let run (args: string list) : string =
        try
            let path = Environment.GetEnvironmentVariable("PATH")
            let doltDir = @"Q:\.tools\dolt\dolt-windows-amd64\bin"
            if not (path.Contains(doltDir)) then
                Environment.SetEnvironmentVariable("PATH", $"{doltDir};{path}")
            Environment.SetEnvironmentVariable("BEADS_DIR", Path.Combine(__SOURCE_DIRECTORY__, ".beads"))
            let result = cli { Exec (bd()); Arguments (args |> Array.ofList) } |> Command.execute
            result.Text |> Option.defaultValue ""
        with ex ->
            if not warned then eprintfn $"⚠ beads unavailable: {ex.Message}"; warned <- true
            ""

    /// Ensure campaign epic exists. Returns its ID.
    let ensureEpic (projectName: string) =
        if epicId = "" then
            // Search for existing epic
            let existing = run ["query"; "type=epic AND status!=closed"; "--json"]
            if existing.Contains projectName then
                // Extract ID from JSON — rough but works
                let m = System.Text.RegularExpressions.Regex.Match(existing, "\"id\":\\s*\"([^\"]+)\"")
                if m.Success then epicId <- m.Groups.[1].Value
            if epicId = "" then
                epicId <- (run ["create"; $"--title={projectName}"; "--type=epic"; "--description=Porting campaign"]).Trim()
        epicId

    /// Create a sprint task under the campaign epic.
    let createSprint (sprintNum: int) (bucket: string) (preMetrics: string) =
        let parent = epicId
        let args = [
            "create"
            $"--title=S{sprintNum}: {bucket}"
            $"--description={preMetrics}"
            "--type=task"
            $"--labels=sprint:{sprintNum},bucket:{bucket}"
            if parent <> "" then $"--parent={parent}"
        ]
        (run args).Trim()

    let claim id = run ["update"; id; "--claim"] |> ignore
    let note id text = run ["note"; id; text] |> ignore

    /// Record a verifier result as a labeled note.
    let verifierResult id (verifier: string) (passed: bool) (attempt: int) =
        let verdict = if passed then "PASS" else "FAIL"
        note id $"VERIFIER:{verifier}:{verdict}:attempt{attempt}"

    let closeSuccess id reason = run ["close"; id; $"--reason={reason}"] |> ignore
    let closeFailed id reason = run ["close"; id; $"--reason=FAILED: {reason}"; "--add-label"; "failed"] |> ignore
    let remember text = run ["remember"; text] |> ignore

module Agent =
    let private model = "claude-opus-4.6"

    let run (prompt: string) (title: string) (resumeId: string option) : string * string =
        let sid = resumeId |> Option.defaultWith (fun () -> Guid.NewGuid().ToString())
        try
            let result =
                cli { Exec "copilot"; Arguments [| "-p"; prompt; "--model"; model; "--resume"; sid; "--allow-all"; "--no-ask-user"; "--autopilot"; "-s"; "--no-color"; "--plain-diff"; "--stream"; "off" |] }
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
        "Read: adr/INDEX.md, porting-plan.md"
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

module PortStatus =
    /// Scan Go files for "// Ported from:" comments and update port_status in the source index DB.
    /// Runs after each successful sprint — orchestrator-owned, not agent-dependent.
    let sync (sprintNum: int) =
        let dbPath = Path.Combine(targetDir(), "pyright-source-index.db")
        if not (File.Exists dbPath) then () else
        let internalDir = Path.Combine(targetDir(), "internal")
        if not (Directory.Exists internalDir) then () else
        let goFiles = Directory.GetFiles(internalDir, "*.go", SearchOption.AllDirectories)
                      |> Array.filter (fun f -> not (f.EndsWith("_test.go")))
        let pattern = System.Text.RegularExpressions.Regex(@"//\s*Ported from:\s*(\S+?)(?::(\d+[\-–]\d+))?\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
        let entries = [
            for goFile in goFiles do
                let content = File.ReadAllText goFile
                let ms = pattern.Matches content
                for m in ms do
                    let tsFile = m.Groups.[1].Value
                    let tsLines = if m.Groups.[2].Success then m.Groups.[2].Value else ""
                    let sep = string Path.DirectorySeparatorChar
                    let goRel = goFile.Replace(targetDir() + sep, "").Replace('\\', '/')
                    yield (tsFile, tsLines, goRel) ]
        if entries.IsEmpty then () else
        try
            // Write entries to a temp JSON file, then run Python to upsert
            let tmpJson = Path.GetTempFileName()
            let jsonData = System.Text.Json.JsonSerializer.Serialize(entries |> List.map (fun (t,l,g) -> {| ts=t; lines=l; go=g |}))
            File.WriteAllText(tmpJson, jsonData)
            let pyScript = String.concat "\n" [
                "import sqlite3, json, sys"
                "db, jf, sprint = sys.argv[1], sys.argv[2], int(sys.argv[3])"
                "conn = sqlite3.connect(db)"
                "c = conn.cursor()"
                "entries = json.load(open(jf))"
                ""
                "# Load function-level entries for range matching"
                "func_ranges = {}"
                "for row in c.execute('SELECT ts_file, ts_lines, concept FROM port_status WHERE ts_lines IS NOT NULL AND ts_lines != \"\"'):"
                "    f, lr, concept = row"
                "    if '-' in lr:"
                "        parts = lr.replace('\\u2013', '-').split('-')"
                "        try: func_ranges.setdefault(f, []).append((int(parts[0]), int(parts[1]), concept))"
                "        except: pass"
                ""
                "updated = 0"
                "for e in entries:"
                "    ts, lines, go = e['ts'], e['lines'], e['go']"
                "    matched_concept = None"
                "    if lines and '-' in lines:"
                "        parts = lines.replace('\\u2013', '-').split('-')"
                "        try:"
                "            start = int(parts[0])"
                "            for (fs, fe, fc) in func_ranges.get(ts, []):"
                "                if fs <= start <= fe:"
                "                    matched_concept = fc"
                "                    break"
                "        except: pass"
                "    if matched_concept:"
                "        c.execute('UPDATE port_status SET go_file=?, status=\"partial\", sprint=?, updated_at=datetime(\"now\") WHERE ts_file=? AND concept=?', (go, sprint, ts, matched_concept))"
                "    else:"
                "        c.execute('INSERT INTO port_status (ts_file, ts_lines, concept, go_file, status, sprint, updated_at) VALUES (?, ?, ?, ?, ?, ?, datetime(\"now\")) ON CONFLICT(ts_file, concept) DO UPDATE SET go_file=excluded.go_file, status=\"partial\", sprint=excluded.sprint, updated_at=datetime(\"now\")', (ts, lines, 'ported-function', go, 'partial', sprint))"
                "    updated += 1"
                "conn.commit()"
                "print('port_status: synced ' + str(updated) + ' entries')"
                "conn.close()" ]
            let pyFile = Path.GetTempFileName() + ".py"
            File.WriteAllText(pyFile, pyScript)
            let sn = string sprintNum
            let result = cli { Exec "python"; Arguments [| pyFile; dbPath; tmpJson; sn |] } |> Command.execute
            result.Text |> Option.iter (fun t -> printfn "  📊 %s" (t.Trim()))
            File.Delete tmpJson
            File.Delete pyFile
        with ex -> eprintfn "  ⚠ port_status sync failed: %s" ex.Message

    /// Generate a brief summary of porting progress from port_status.
    let summary () =
        let dbPath = Path.Combine(targetDir(), "pyright-source-index.db")
        if not (File.Exists dbPath) then "" else
        try
            let pyScript = "import sqlite3,sys\nconn=sqlite3.connect(sys.argv[1])\nrows=conn.execute('SELECT status,COUNT(*) FROM port_status GROUP BY status').fetchall()\nprint(' | '.join(f'{s}: {n}' for s,n in rows))\nconn.close()"
            let pyFile = Path.GetTempFileName() + ".py"
            File.WriteAllText(pyFile, pyScript)
            let result = cli { Exec "python"; Arguments [| pyFile; dbPath |] } |> Command.execute
            File.Delete pyFile
            result.Text |> Option.defaultValue "" |> fun s -> s.Trim()
        with _ -> ""

module ConvergenceLoop =
    let private key () = projectKey (targetDir())
    let private trunc (s: string) n = if s.Length <= n then s else s.[..n/2] + "..." + s.[(s.Length-n/2)..]

    let private ensureInit () =
        let config = require()
        let db = currentDbPath (key())
        if not (File.Exists db) then let c = initSchema db in initSprint c 0 "" 0 0; c.Close()
        Beads.ensureEpic config.ProjectName |> ignore
        config

    let private buildBriefing config sprintNum dbBriefing allBuckets prevFailure =
        let prevBlock = match prevFailure with Some ctx -> $"\n<previous_failure>\n{trunc ctx 3000}\n</previous_failure>" | None -> ""
        // Check for nudge.md — human can drop instructions for the next sprint
        let nudgePath = Path.Combine(targetDir(), "nudge.md")
        let nudgeBlock =
            if File.Exists nudgePath then
                let content = File.ReadAllText nudgePath
                File.Delete nudgePath // consume it — one-shot
                printfn $"  📌 Nudge picked up: {content.[..min 80 (content.Length-1)]}..."
                $"\n<human_nudge>\n{content}\n</human_nudge>"
            else ""
        let srcDir = config.SourceDir
        String.concat "\n" [
            $"Sprint {sprintNum}. Port TypeScript logic to Go."
            $"Read: porting-plan.md (especially 'How To Port'), adr/INDEX.md"
            "Check .github/instructions/port-debt.instructions.md for known gaps from previous sprints."
            "Check port_status table in pyright-source-index.db for what is already ported."
            "You MUST commit your changes. Do NOT push."
            $"\n<test_status>\n{dbBriefing}\n</test_status>"
            $"\n<failing_buckets>\n{allBuckets}\n</failing_buckets>"
            "\nPick a failing area. Query pyright-source-index.db to find the TS source that implements it."
            $"Read the TypeScript source at {srcDir}. Port that logic into internal/ packages."
            "Every diagnostic must come from real analysis (parse→bind→check). NEVER pattern-match Python source."
            "Do not reinvent — the TS code is battle-tested. Read it, port it, preserve all edge cases."
            "After porting: add '// Ported from: file:lines' comments to every ported function. Log any skipped edge cases in port-debt.instructions.md."
            "Commit your changes."
            nudgeBlock
            prevBlock
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
        | [] when pt = 0 ->
            // No test data — one-shot bootstrap, then stop for human verification
            conn.Close()
            printfn $"S{next} | No test data — bootstrap sprint"
            let prompt = String.concat "\n" [
                $"Sprint {next}. Source: {config.SourceDir}."
                "No test results exist yet. Read porting-plan.md Phase 0."
                "Set up the Go test harness. Test samples are in testdata/cases/."
                "You MUST commit your changes. Do NOT push."
                "Read: adr/INDEX.md, porting-plan.md" ]
            let bead = Beads.createSprint next "bootstrap" "Empty DB"
            Beads.claim bead
            let sc = initSchema db in initSprint sc next "bootstrap" 0 0; sc.Close()
            let (_, _) = Agent.run prompt $"Impl-S{next}" None
            let pushResult = try (cli { Exec "git"; Arguments [|"push"|] } |> Command.execute).ExitCode with _ -> 1
            if pushResult = 0 then printfn "  Pushed." else printfn "  ⚠ push failed"
            Beads.closeSuccess bead "Bootstrap done"
            printfn "  Bootstrap done. Verify test harness works, then restart ralph-port run."
            (true, "ALL_PASS") // stops the run loop
        | [] ->
            conn.Close(); printfn "All pass!"; (true, "ALL_PASS")
        | _ ->
            // Build a full bucket list for the agent to choose from
            let allBuckets =
                ranked |> List.map (fun (b, l, f, t) -> $"  {b} ({l}): {f}/{t} failing")
                |> String.concat "\n"
            let brief = briefing conn
            conn.Close()
            printfn $"S{next} | {pp}/{pt} | {ranked.Length} failing buckets"

            let prompt = buildBriefing config next brief allBuckets prevFailure
            let topBucket = ranked |> List.head |> fun (b,_,_,_) -> b
            let bead = Beads.createSprint next topBucket $"Pre:{pp}/{pt}"
            Beads.claim bead
            let sc = initSchema db in initSprint sc next topBucket pp pt; sc.Close()

            // Record base commit BEFORE implementor runs — this defines the sprint's diff scope
            let baseCommit =
                try (cli { Exec "git"; Arguments [|"rev-parse"; "HEAD"|] } |> Command.execute).Text |> Option.defaultValue "HEAD" |> fun s -> s.Trim()
                with _ -> "HEAD"

            Beads.note bead $"PHASE:impl baseCommit={baseCommit.[..7]}"
            let (_, sid) = Agent.run prompt $"Impl-S{next}" None

            let headAfterImpl =
                try (cli { Exec "git"; Arguments [|"rev-parse"; "HEAD"|] } |> Command.execute).Text |> Option.defaultValue "" |> fun s -> s.Trim()
                with _ -> ""
            if headAfterImpl = baseCommit then
                Beads.note bead "PHASE:impl:NO_COMMITS"
                printfn "  ⚠ Implementor made no commits"
            else
                Beads.note bead $"PHASE:impl:done commits={headAfterImpl.[..7]}"

            let mutable retries = 0
            let mutable passed = false
            let mutable lastFail = ""

            while retries < maxRetries && not passed do
                Beads.note bead $"PHASE:verify attempt={retries+1}"
                let results = Verifiers.listAll() |> List.map (fun v -> async {
                    let (vp,vo,vsid) = Verifiers.runVerifier v baseCommit
                    let verdict = if vp then "PASS" else "FAIL"
                    Beads.verifierResult bead v (verdict = "PASS") (retries+1)
                    return (v,vp,vo,vsid) }) |> Async.Parallel |> Async.RunSynchronously |> Array.toList
                let failed = results |> List.filter (fun (_,vp,_,_) -> not vp)
                if failed.IsEmpty then passed <- true
                else
                    let fb = failed |> List.map (fun (v,_,vo,_) -> $"=== {v} ===\n{trunc vo 2000}") |> String.concat "\n\n"
                    lastFail <- fb
                    let failedNames = failed |> List.map (fun (v,_,_,_) -> v) |> String.concat ","
                    Beads.note bead $"PHASE:fix attempt={retries+1} fixing={failedNames}"
                    Agent.resume sid fb $"Fix-S{next}" |> ignore
                    Beads.note bead $"PHASE:recheck attempt={retries+1}"
                    let rechecks = failed |> List.map (fun (v,_,_,vsid) -> async { let (r,_) = Verifiers.resumeVerifier vsid v in return (v,r) }) |> Async.Parallel |> Async.RunSynchronously
                    if rechecks |> Array.forall snd then passed <- true
                    retries <- retries + 1

            let fc = initSchema db in let (fp,ft) = passRate fc in let d = fp-pp in finalizeSprint fc fp ft; fc.Close()
            archiveAndReset (key()) next
            let msg = $"{fp}/{ft} d={d}"
            Beads.note bead msg
            if passed && d > 0 then
                Beads.closeSuccess bead msg; printfn $"  OK: {msg}"
                // Sync port_status from "// Ported from:" comments — orchestrator-owned, not agent-dependent
                PortStatus.sync next
                // Knowledge capture — runs BEFORE push so its changes get included
                let capturePrompt = String.concat "\n" [
                    $"Sprint {next} succeeded."
                    $"EXACT SCOPE: git diff {baseCommit}..HEAD"
                    $"Log: git log --oneline {baseCommit}..HEAD"
                    "Review only these commits. Capture non-trivial learnings if any."
                    "If you create/edit files, commit them. Say 'No learnings.' if none." ]
                Agent.run capturePrompt $"Knowledge-S{next}" None |> ignore
                // Push ALL commits (impl + knowledge capture) on success
                let pushResult = try (cli { Exec "git"; Arguments [|"push"|] } |> Command.execute).ExitCode with _ -> 1
                if pushResult <> 0 then printfn "  ⚠ git push failed"
                else printfn "  Pushed."
                (true, msg)
            else
                Beads.closeFailed bead msg; printfn $"  Fail: {msg}"; (false, lastFail)

    let run maxRetries =
        let config = ensureInit ()
        printfn $"=== {config.ProjectName}: {config.SourceLang} -> {config.TargetLang} ==="
        let mutable go = true
        let mutable prev: string option = None
        let mutable consecutiveErrors = 0
        let mutable sprintsSinceReview = 0
        let reviewInterval = 7
        Console.CancelKeyPress.Add(fun a -> a.Cancel <- true; go <- false; printfn "\nStopping...")
        while go do
            try
                // Every N sprints: full codebase review → refactoring sprint
                sprintsSinceReview <- sprintsSinceReview + 1
                if sprintsSinceReview >= reviewInterval then
                    sprintsSinceReview <- 0
                    printfn "── Periodic codebase review ──"
                    let reviewPrompt = String.concat "\n" [
                        "Invoke the review-expert agent as a subtask."
                        $"Goal: assess overall quality of the FULL codebase in {targetDir()}."
                        "You are NOT reviewing a diff. You are reviewing the entire implementation."
                        $"Source reference: {config.SourceDir}"
                        "Focus on:"
                        "- Simplify code, logical flow, and data flow"
                        "- Detect missing abstractions or reuse potential"
                        "- Module-level architectural concerns"
                        "- Cross-cutting refactoring opportunities"
                        "Output a prioritized list of actionable improvements." ]
                    let (reviewOutput, _) = Agent.run reviewPrompt "review-expert" None
                    let reviewFeedback = trunc reviewOutput 4000
                    Beads.remember $"Codebase review: {trunc reviewOutput 500}"
                    // Feed review as a refactoring sprint — must not regress tests
                    let refactorPrompt = String.concat "\n" [
                        "REFACTORING SPRINT. No new features. Test count and pass rate must not decrease."
                        "An expert review found these improvement opportunities:"
                        reviewFeedback
                        "Pick the highest-impact improvements. Refactor, commit." ]
                    printfn "── Refactoring sprint ──"
                    let (_, refactorSid) = Agent.run refactorPrompt "Refactor" None
                    // Run verifiers on the refactoring too
                    let baseCommit =
                        try (cli { Exec "git"; Arguments [|"rev-parse"; "HEAD"|] } |> Command.execute).Text |> Option.defaultValue "HEAD" |> fun s -> s.Trim()
                        with _ -> "HEAD"
                    let results = Verifiers.listAll() |> List.map (fun v -> async {
                        let (vp,vo,vsid) = Verifiers.runVerifier v baseCommit
                        return (v,vp,vo,vsid) }) |> Async.Parallel |> Async.RunSynchronously |> Array.toList
                    let failed = results |> List.filter (fun (_,vp,_,_) -> not vp)
                    if not failed.IsEmpty then
                        let fb = failed |> List.map (fun (v,_,vo,_) -> $"=== {v} ===\n{trunc vo 2000}") |> String.concat "\n\n"
                        Agent.resume refactorSid fb "Fix-Refactor" |> ignore
                    try cli { Exec "git"; Arguments [|"push"|] } |> Command.execute |> ignore with _ -> ()
                    printfn "── Refactoring done ──"
                else
                    let (ok, s) = step maxRetries prev
                    consecutiveErrors <- 0
                    if s = "ALL_PASS" then go <- false
                    elif ok then prev <- None
                    else prev <- Some s
            with ex ->
                consecutiveErrors <- consecutiveErrors + 1
                eprintfn $"Sprint error ({consecutiveErrors}): {ex.Message}"
                Beads.remember $"Sprint error: {ex.Message}"
                if consecutiveErrors >= 5 then
                    eprintfn "5 consecutive errors — sleeping 10 min before retry"
                    System.Threading.Thread.Sleep(10 * 60 * 1000)
                    consecutiveErrors <- 0

    let status () =
        match load() with
        | Some c ->
            let k = key()
            printfn $"{c.ProjectName}: {c.SourceLang} -> {c.TargetLang}"
            let db = currentDbPath k
            if File.Exists db then let cn = initSchema db in printfn $"{briefing cn}"; cn.Close()
            let trend = trendData k
            if trend.Length > 0 then
                printfn ""
                printfn $"{renderChart trend 30}"
        | None -> printfn "No project.json."

    /// Watch mode — refreshes status every N seconds.
    let watch interval =
        let mutable go = true
        Console.CancelKeyPress.Add(fun a -> a.Cancel <- true; go <- false)
        while go do
            Console.Clear()
            status ()
            printfn $"\n(refreshing every {interval}s — Ctrl+C to stop)"
            System.Threading.Thread.Sleep(interval * 1000)

let retries rest = rest |> List.tryFind (fun (s:string) -> s.StartsWith "--retries=") |> Option.map (fun s -> int(s.Split('=').[1])) |> Option.defaultValue 3

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| "run" :: r -> ConvergenceLoop.run (retries r)
| "step" :: r -> ConvergenceLoop.step (retries r) None |> ignore
| ["status"] -> ConvergenceLoop.status ()
| "watch" :: r ->
    let interval = r |> List.tryHead |> Option.map int |> Option.defaultValue 30
    ConvergenceLoop.watch interval
| _ ->
    printfn "ralph-port"
    printfn "  run   [--retries=N]  Autonomous loop. Ctrl+C safe."
    printfn "  step  [--retries=N]  One sprint."
    printfn "  status               Current state + progress chart."
    printfn "  watch [seconds]      Live dashboard (default: 30s refresh)."