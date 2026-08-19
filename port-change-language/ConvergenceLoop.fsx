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

/// Run an external process with a hard wall-clock TIMEOUT and process-TREE kill.
/// The loop is one sequential thread; without this a hung child — a stuck `copilot`
/// agent, or a freshly-ported binary that infinite-loops during the parity run —
/// blocks forever and the loop sits DEAD (it lost a whole 62h weekend this way). The
/// outer run-loop only catches EXCEPTIONS, so on timeout we kill the whole tree and
/// RAISE, turning a silent hang into a recoverable error. Returns (stdout, stderr, exit).
let execWithTimeout (exe: string) (args: string list) (workDir: string) (timeoutMin: int) : string * string * int =
    let psi = System.Diagnostics.ProcessStartInfo(exe)
    args |> List.iter psi.ArgumentList.Add
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    if workDir <> "" then psi.WorkingDirectory <- workDir
    use p = new System.Diagnostics.Process(StartInfo = psi)
    let sb = System.Text.StringBuilder()
    let eb = System.Text.StringBuilder()
    p.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then sb.AppendLine e.Data |> ignore)
    p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then eb.AppendLine e.Data |> ignore)
    p.Start() |> ignore
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()
    if p.WaitForExit(timeoutMin * 60 * 1000) then
        p.WaitForExit()                                   // flush async readers
        (sb.ToString(), eb.ToString(), p.ExitCode)
    else
        (try p.Kill(true) with _ -> ())                   // kill copilot/python + node/MCP children
        failwithf "TIMEOUT after %dmin (killed process tree): %s" timeoutMin exe

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
    let note id text = run ["comments"; "add"; id; text] |> ignore

    /// Record a verifier result as a labeled note.
    let verifierResult id (verifier: string) (passed: bool) (attempt: int) =
        let verdict = if passed then "PASS" else "FAIL"
        note id $"VERIFIER:{verifier}:{verdict}:attempt{attempt}"

    let closeSuccess id reason = run ["close"; id; $"--reason={reason}"] |> ignore
    let closeFailed id reason = run ["close"; id; $"--reason=FAILED: {reason}"; "--add-label"; "failed"] |> ignore
    let remember text = run ["remember"; text] |> ignore

module Agent =

    let Model = "claude-opus-4.8"
    let Effort = "xhigh"  // reasoning effort: none|low|medium|high|xhigh|max

    let run (prompt: string) (title: string) (resumeId: string option) : string * string =
        // Copilot CLI distinguishes new sessions (--name) from resumes (--resume).
        // Passing an unknown GUID to --resume now errors out (exit 1, "No session matched").
        // For brand-new sessions we use --name; for actual resumes we use --resume.
        let isResume = resumeId.IsSome
        let sid = resumeId |> Option.defaultWith (fun () -> Guid.NewGuid().ToString())
        let sessionFlag = if isResume then "--resume" else "--name"
        // A prompt passed via -p becomes a COMMAND-LINE ARGUMENT; a full briefing (all
        // failing buckets + PORT mandate + approved design) can exceed the OS command-line
        // limit (~32KB on Windows -> "The filename or extension is too long", and the whole
        // sprint fails to launch). For long prompts, write the prompt to a temp FILE and give
        // copilot a short pointer that forces reading the file first. Short prompts (resume
        // feedback) pass through directly.
        let promptFile =
            if prompt.Length > 12000 then
                let f = Path.Combine(Path.GetTempPath(), $"ralph-prompt-{sid}.md")
                File.WriteAllText(f, prompt)
                Some f
            else None
        let effectivePrompt =
            match promptFile with
            | Some f ->
                String.concat "\n" [
                    "Your COMPLETE instructions for this task are in the file below."
                    "FIRST ACTION, before anything else: open and READ THE ENTIRE FILE with your file-reading"
                    "tool — it is authoritative and complete. Then do EXACTLY what it says, in full."
                    "Reminder: this is a PORT — the source is authoritative; read and quote it, never invent."
                    ""
                    f ]
            | None -> prompt
        try
            try
                // Hard timeout: a hung `copilot` (network stall, model hang, a subagent
                // waiting despite --no-ask-user) must NOT block the sequential loop forever.
                // 4h is well above the ~2.5h a legit heavy impl takes, so it only trips on a
                // true hang; on timeout the tree is killed and this raises -> caught below.
                let (stdout, err, _exit) =
                    execWithTimeout "copilot"
                        [ "-p"; effectivePrompt; sessionFlag; sid; "--allow-all"; "--no-ask-user"; "-s"; "--no-color"; "--plain-diff"; "--model"; Model; "--effort"; Effort; "--stream"; "off" ]
                        "" 240
                if stdout = "" then
                    if err <> "" then
                        eprintfn $"Agent '{title}' empty stdout, stderr: {err.[..min 400 (err.Length-1)]}"
                (stdout, sid)
            with ex -> eprintfn $"Agent '{title}': {ex.Message}"; ("", sid)
        finally
            match promptFile with Some f -> (try File.Delete f with _ -> ()) | None -> ()

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
        "THIS IS A PORT: every diagnostic-producing change must REPRODUCE the cited source logic, not"
        "invent / gate / suppress / guess. Open the `// Ported from:` anchors and check them yourself."
        "TEETH: 'advisory / non-blocking' is NOT allowed for a hard-fail criterion. If any hard-fail"
        "condition in your checklist is met, you MUST output VERIFY_FAILED — never pass it with a note."
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
        let prompt = (preamble baseCommit) + "\n\n" + getPrompt name
        let (out, sid) = Agent.run prompt (title name) None
        let (passed, fullOut) = parseVerdict out sid name
        (passed, fullOut, sid)

    let resumeVerifier sid name =
        let out = Agent.resume sid "Implementor fixed the issues. Re-review. Output VERIFY_PASSED or VERIFY_FAILED." $"Re-{title name}"
        let (passed, fullOut) = parseVerdict out sid name
        (passed, fullOut)

module PortStatus =
    /// Canonical source-index DB path. The 908-row port_status table lives under
    /// _tools/ (the repo root copy is an empty stray file) — pointing at the root
    /// made sync/coverage a silent no-op.
    let sourceIndexPath () = Path.Combine(targetDir(), "_tools", "pyright-source-index.db")

    /// Scan Go files for "// Ported from:" comments and update port_status in the source index DB.
    /// Runs after each successful sprint — orchestrator-owned, not agent-dependent.
    let sync (sprintNum: int) =
        let dbPath = sourceIndexPath ()
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
                "import sqlite3, json, sys, os"
                "db, jf, sprint = sys.argv[1], sys.argv[2], int(sys.argv[3])"
                "conn = sqlite3.connect(db)"
                "c = conn.cursor()"
                "entries = json.load(open(jf))"
                ""
                "# Load function-level ranges, keyed by BASENAME so bare-name markers"
                "# (e.g. 'typeEvaluator.ts') canonicalize to the prefixed index path"
                "# ('analyzer/typeEvaluator.ts'). Store the real ts_file for the UPDATE."
                "func_ranges = {}"
                "for row in c.execute(\"SELECT ps.ts_file, ps.ts_lines, ps.concept FROM port_status ps WHERE ps.ts_lines IS NOT NULL AND ps.ts_lines != '' AND ps.ts_file LIKE '%.ts' AND ps.concept NOT LIKE 'phantom:%' AND ps.ts_file IN (SELECT path FROM files)\"):"
                "    f, lr, concept = row"
                "    if '-' in lr:"
                "        parts = lr.replace('\\u2013', '-').split('-')"
                "        try: func_ranges.setdefault(os.path.basename(f), []).append((int(parts[0]), int(parts[1]), concept, f))"
                "        except: pass"
                "# Refuse to guess when a basename is owned by >1 real file (cross-dir collision)."
                "ambig = set(b for b,v in func_ranges.items() if len(set(x[3] for x in v))>1)"
                ""
                "updated = 0"
                "phantom = 0"
                "for e in entries:"
                "    ts, lines, go = e['ts'], e['lines'], e['go']"
                "    matched_concept = None"
                "    real_file = None"
                "    bn = os.path.basename(ts)"
                "    if lines and '-' in lines and bn not in ambig:"
                "        parts = lines.replace('\\u2013', '-').split('-')"
                "        try:"
                "            start = int(parts[0])"
                "            for (fs, fe, fc, rf) in func_ranges.get(bn, []):"
                "                if fs <= start <= fe:"
                "                    matched_concept = fc; real_file = rf"
                "                    break"
                "        except: pass"
                "    if matched_concept:"
                "        # Marker maps to a real TS function range -> honest coverage."
                "        # 'partial' = ported (a marker present); promotion to 'complete'"
                "        # is earned by the faithfulness verifier, not by typing a comment."
                "        c.execute('UPDATE port_status SET go_file=?, status=\"partial\", sprint=?, updated_at=datetime(\"now\") WHERE ts_file=? AND concept=? AND status!=\"complete\"', (go, sprint, real_file, matched_concept))"
                "        updated += 1"
                "    else:"
                "        # PHANTOM marker: cites a TS file/range with no known function"
                "        # range. Do NOT write it (it would not advance coverage and the"
                "        # schema CHECK forbids a 'phantom' status) — just count for audit."
                "        phantom += 1"
                "conn.commit()"
                "stats = dict(c.execute('SELECT status, COUNT(*) FROM port_status GROUP BY status').fetchall())"
                "print('port_status: %d markers matched, %d PHANTOM (unmatched TS range) | ' % (updated, phantom) + ' '.join('%s=%d'%(s,n) for s,n in stats.items()))"
                "conn.close()" ]
            let pyFile = Path.GetTempFileName() + ".py"
            File.WriteAllText(pyFile, pyScript)
            let sn = string sprintNum
            let result = cli { Exec "python"; Arguments [| pyFile; dbPath; tmpJson; sn |] } |> Command.execute
            result.Text |> Option.iter (fun t -> printfn "  📊 %s" (t.Trim()))
            File.Delete tmpJson
            File.Delete pyFile
        with ex -> eprintfn "  ⚠ port_status sync failed: %s" ex.Message

    /// Check if any "// Ported from:" or "// TODO(port):" comments were removed in this sprint's diff.
    /// Returns list of removed coverage markers. Empty = no regression.
    let checkRegressions (baseCommit: string) : string list =
        try
            let result = cli { Exec "git"; Arguments [| "diff"; baseCommit + "..HEAD"; "--"; "internal/" |] } |> Command.execute
            let diff = result.Text |> Option.defaultValue ""
            let lines = diff.Split('\n')
            let removed = [
                for line in lines do
                    let trimmed = line.TrimStart()
                    if trimmed.StartsWith("-") && not (trimmed.StartsWith("---")) then
                        let content = trimmed.[1..]
                        if content.Contains("// Ported from:") || content.Contains("// TODO(port):") then
                            // Check if same marker was re-added (moved, not deleted)
                            let marker = content.Trim()
                            let wasReadded = lines |> Array.exists (fun l ->
                                let t = l.TrimStart()
                                t.StartsWith("+") && not (t.StartsWith("+++")) && t.[1..].Trim() = marker)
                            if not wasReadded then yield content.Trim() ]
            removed
        with _ -> []

    /// Deterministic, NOISE-FREE progress signal: net count of `// Ported from:`
    /// markers ADDED in this sprint's diff whose cited TS file exists in the source
    /// index (a marker to an unknown file does not count — anti-inflation). New
    /// faithful ports add markers, so structural / keystone work accretes on HEAD
    /// even when it flips no diagnostic yet. This is what dissolves the revert-spin:
    /// coverage is immune to the ±250 parity noise.
    let coverageGain (baseCommit: string) : int =
        try
            let dbPath = sourceIndexPath ()
            if not (File.Exists dbPath) then 0 else
            let py = String.concat "\n" [
                "import sqlite3,subprocess,re,sys,os"
                "db,base=sys.argv[1],sys.argv[2]"
                "# basename -> list of (start,end) real concept ranges (phantom excluded)."
                "ranges={}"
                "for f,lr in sqlite3.connect(db).execute(\"SELECT ts_file,ts_lines FROM port_status WHERE ts_lines IS NOT NULL AND ts_lines!='' AND ts_file LIKE '%.ts' AND concept NOT LIKE 'phantom:%' AND ts_file IN (SELECT path FROM files)\"):"
                "    if '-' in lr:"
                "        p=lr.replace('\\u2013','-').split('-')"
                "        try: ranges.setdefault(os.path.basename(f),[]).append((int(p[0]),int(p[1])))"
                "        except: pass"
                "diff=subprocess.run(['git','diff',base+'..HEAD','--','internal/'],capture_output=True,text=True).stdout"
                "pat=re.compile(r'//\\s*Ported from:\\s*(\\S+?):(\\d+)[-\\u2013]\\d+',re.I)"
                "def valid(m):"
                "    bn=os.path.basename(m.group(1)); start=int(m.group(2))"
                "    return any(fs<=start<=fe for (fs,fe) in ranges.get(bn,[]))"
                "def cnt(sign):"
                "    n=0"
                "    for l in diff.split('\\n'):"
                "        if l.startswith(sign) and not l.startswith(sign*3):"
                "            m=pat.search(l)"
                "            if m and valid(m): n+=1"
                "    return n"
                "print(cnt('+')-cnt('-'))" ]
            let pyFile = Path.GetTempFileName() + ".py"
            File.WriteAllText(pyFile, py)
            let r = cli { Exec "python"; Arguments [| pyFile; dbPath; baseCommit |] } |> Command.execute
            (try File.Delete pyFile with _ -> ())
            match System.Int32.TryParse((r.Text |> Option.defaultValue "0").Trim()) with
            | true, n -> n
            | _ -> 0
        with _ -> 0

    /// Faithfulness promoter (partial -> complete). Orchestrator-owned, DETERMINISTIC
    /// (no agent): a 'partial' concept (a marker exists) is promoted to 'complete' only
    /// when the Go function carrying its marker is not a stub — no panic()/TODO(port)/
    /// unimplemented sentinel AND its body is proportional to the TS range (>= 25%, min
    /// 5 lines). This makes 'complete' an honest, non-forgeable terminus signal and stops
    /// marker-on-stub farming from ever reaching "done".
    let promote (sprintNum: int) =
        try
            let dbPath = sourceIndexPath ()
            if not (File.Exists dbPath) then () else
            let py = String.concat "\n" [
                "import sqlite3,sys,os,re"
                "db,sprint,root=sys.argv[1],int(sys.argv[2]),sys.argv[3]"
                "conn=sqlite3.connect(db); c=conn.cursor()"
                "rows=c.execute(\"SELECT ts_file,ts_lines,concept,go_file FROM port_status WHERE status='partial' AND go_file IS NOT NULL AND go_file!='' AND ts_lines LIKE '%-%' AND concept NOT LIKE 'phantom:%' AND ts_file IN (SELECT path FROM files) LIMIT 60\").fetchall()"
                "STUB=re.compile(r'panic\\(|TODO\\(port\\)|not[ _]?implemented|unimplemented',re.I)"
                "promoted=0"
                "for ts_file,ts_lines,concept,go_file in rows:"
                "    gp=os.path.join(root,go_file)"
                "    if not os.path.exists(gp): continue"
                "    try: lines=open(gp,encoding='utf-8').read().split('\\n')"
                "    except: continue"
                "    bn=os.path.basename(ts_file)"
                "    try:"
                "        a,b=ts_lines.replace('\\u2013','-').split('-'); tsn=int(b)-int(a)+1"
                "    except: tsn=0"
                "    ok=False"
                "    for i,l in enumerate(lines):"
                "        if 'Ported from' in l and bn in l:"
                "            j=i"
                "            while j>=0 and not lines[j].lstrip().startswith('func '): j-=1"
                "            if j<0: continue"
                "            depth=0; started=False; body=[]"
                "            for k in range(j,len(lines)):"
                "                body.append(lines[k]); depth+=lines[k].count('{')-lines[k].count('}')"
                "                if '{' in lines[k]: started=True"
                "                if started and depth<=0: break"
                "            fn='\\n'.join(body); gon=len([x for x in body if x.strip()])"
                "            if not STUB.search(fn) and gon>=max(5,int(tsn*0.25)): ok=True; break"
                "    if ok:"
                "        c.execute(\"UPDATE port_status SET status='complete', sprint=?, updated_at=datetime('now') WHERE ts_file=? AND concept=?\",(sprint,ts_file,concept)); promoted+=1"
                "conn.commit(); print('promoted %d partial->complete'%promoted); conn.close()" ]
            let pyFile = Path.GetTempFileName() + ".py"
            File.WriteAllText(pyFile, py)
            let r = cli { Exec "python"; Arguments [| pyFile; dbPath; string sprintNum; targetDir () |] } |> Command.execute
            (try File.Delete pyFile with _ -> ())
            r.Text |> Option.iter (fun t -> if t.Trim() <> "" then printfn "  🎓 %s" (t.Trim()))
        with ex -> eprintfn "  ⚠ promote failed: %s" ex.Message

    /// Honest convergence terminus. Returns a status string. DONE requires every real
    /// indexed concept 'complete' AND every non-test source file present in the index
    /// (the index-completeness gate: a truncated index can NEVER falsely fire "done" —
    /// it forces the loop to extend the index instead).
    let convergenceStatus () : string =
        try
            let dbPath = sourceIndexPath ()
            if not (File.Exists dbPath) then "no-index" else
            let py = String.concat "\n" [
                "import sqlite3,sys,os"
                "db,src=sys.argv[1],sys.argv[2]"
                "conn=sqlite3.connect(db); c=conn.cursor()"
                "remaining=c.execute(\"SELECT COUNT(*) FROM port_status WHERE status!='complete' AND concept NOT LIKE 'phantom:%' AND ts_file IN (SELECT path FROM files)\").fetchone()[0]"
                "indexed=set(r[0] for r in c.execute('SELECT DISTINCT ts_file FROM port_status'))"
                "missing=0"
                "srcdir=os.path.join(src,'packages','pyright-internal','src')"
                "if os.path.isdir(srcdir):"
                "    for dp,_,fs in os.walk(srcdir):"
                "        for f in fs:"
                "            if f.endswith('.ts') and not f.endswith('.test.ts') and 'fourslash' not in dp:"
                "                rel=os.path.relpath(os.path.join(dp,f),srcdir).replace(os.sep,'/')"
                "                if rel not in indexed and os.path.basename(rel) not in set(os.path.basename(x) for x in indexed): missing+=1"
                "conn.close()"
                "print('%d %d'%(remaining,missing))" ]
            let pyFile = Path.GetTempFileName() + ".py"
            File.WriteAllText(pyFile, py)
            let srcDir = ProjectConfig.ProjectConfig.load() |> Option.map (fun c -> c.SourceDir) |> Option.defaultValue ""
            let r = cli { Exec "python"; Arguments [| pyFile; dbPath; srcDir |] } |> Command.execute
            (try File.Delete pyFile with _ -> ())
            let parts = (r.Text |> Option.defaultValue "1 1").Trim().Split(' ')
            match parts with
            | [| rem; mis |] ->
                let remaining = (match System.Int32.TryParse rem with | true, n -> n | _ -> 1)
                let missing = (match System.Int32.TryParse mis with | true, n -> n | _ -> 1)
                if remaining = 0 && missing = 0 then "FULL_PRODUCT_DONE"
                elif remaining = 0 then $"INDEXED_SCOPE_DONE_ONLY (index missing {missing} source files)"
                else $"in-progress ({remaining} concepts unverified, {missing} files unindexed)"
            | _ -> "unknown"
        with _ -> "unknown"

    /// Generate a brief summary of porting progress from port_status.
    let summary () =
        let dbPath = sourceIndexPath ()
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

    /// The branch the loop pushes to, captured at run start (when HEAD is attached).
    let mutable private activeBranch = ""

    /// Push robustly even if an implementor/verifier agent left HEAD DETACHED (agents
    /// have full git access and sometimes `git checkout <sha>`, which makes plain
    /// `git push` fail with "not currently on a branch" — silently stranding all
    /// committed work locally). When detached, fast-forward the active branch to HEAD
    /// and reattach, then push. Returns the push exit code.
    let private safePush () : int =
        try
            let attached = (cli { Exec "git"; Arguments [| "symbolic-ref"; "-q"; "HEAD" |] } |> Command.execute).ExitCode = 0
            if not attached && activeBranch <> "" then
                // FAST-FORWARD ONLY. Move the branch to the detached HEAD *only* if HEAD is
                // a descendant of the branch (the agent's commits are ahead). NEVER move the
                // branch backward/sideways onto a detached side-commit — doing so orphaned
                // large landed sprints (django +70/+67/+25 lost while a +4 side-commit
                // overwrote the branch). If HEAD is behind/diverged, reattach WITHOUT moving
                // the branch, preserving the branch's committed work.
                let branchIsAncestorOfHead =
                    (cli { Exec "git"; Arguments [| "merge-base"; "--is-ancestor"; activeBranch; "HEAD" |] } |> Command.execute).ExitCode = 0
                if branchIsAncestorOfHead then
                    cli { Exec "git"; Arguments [| "branch"; "-f"; activeBranch; "HEAD" |] } |> Command.execute |> ignore
                    printfn $"  🔧 fast-forwarded {activeBranch} to detached HEAD (captured its commits)"
                else
                    printfn $"  🔧 detached HEAD is behind/diverged from {activeBranch} — reattaching WITHOUT moving the branch (preserving landed work)"
                cli { Exec "git"; Arguments [| "checkout"; activeBranch |] } |> Command.execute |> ignore
            (cli { Exec "git"; Arguments [| "push" |] } |> Command.execute).ExitCode
        with _ -> 1

    /// Harvest test results by running the project's harvest script directly.
    /// Falls back to agent if script doesn't exist.
    let private harvestTests (config: ProjectConfig) (sprintNum: int) =
        let db = Path.GetFullPath(currentDbPath (key()))
        let harvestScript = Path.Combine(targetDir(), "_tools", "harvest_tests.py")

        if File.Exists harvestScript then
            printfn "  🧪 Harvesting via script..."
            try
                let (_out, err, _exit) =
                    execWithTimeout "python" [ harvestScript; db; string sprintNum ] (targetDir()) 75
                // Print stderr (where the script logs)
                for line in err.Split('\n') do
                    let t = line.Trim()
                    if t.Length > 0 then printfn "  %s" t
            with ex -> eprintfn $"  ⚠ Harvest script failed: {ex.Message}"
        else
            printfn "  🧪 Harvesting via agent (no _tools/harvest_tests.py)..."
            let harvestPrompt = $"HARVEST TASK: Run ALL tests and record results in SQLite DB at {db}. Sprint {sprintNum}. Read .github/instructions/harvest.instructions.md for commands and schema."
            Agent.run harvestPrompt "Harvest" None |> ignore

        try
            let conn = initSchema db
            let (p, t) = passRate conn
            if t > 0 then printfn $"  📊 {p}/{t} passing"
            else eprintfn "  ⚠ 0 test results in DB"
            conn.Close()
        with _ -> eprintfn "  ⚠ Could not read harvest results"

    let private protectedFilesTouched (baseCommit: string) =
        let changedFiles =
            try
                // Compare the base commit to the complete working tree, not merely HEAD.
                // This catches protected edits that an agent staged or left uncommitted.
                (cli { Exec "git"; Arguments [| "diff"; "--name-only"; baseCommit |] } |> Command.execute).Text
                |> Option.defaultValue ""
            with _ -> ""
        changedFiles.Split('\n')
        |> Array.map (fun s -> s.Trim().Replace('\\', '/'))
        |> Array.filter (fun f -> f <> "" && (
                f.StartsWith("testdata/baselines/reference/") ||
                f.EndsWith("pyright-source-index.db") ||
                f.EndsWith("_tools/harvest_tests.py") ||
                f = "project.json"))
        |> Array.toList

    let private repairProtectedFiles baseCommit sid bead sprintNum maxAttempts =
        let mutable touched = protectedFilesTouched baseCommit
        let mutable attempt = 0
        while not touched.IsEmpty && attempt < maxAttempts do
            let files = String.concat "\n" (touched |> List.map (fun f -> $"  - {f}"))
            let touchedCsv = String.concat "," touched
            Beads.note bead $"PHASE:protected-fix attempt={attempt+1} files={touchedCsv}"
            let prompt = String.concat "\n" [
                "PROTECTED ORACLE VIOLATION. Do not abandon the implementation and do not change the oracle."
                "Restore every protected file below byte-for-byte to the sprint base commit:"
                files
                ""
                "Then correct the TARGET IMPLEMENTATION so it passes the existing oracle."
                "Reference baselines come from the authoritative source and must never be edited to make new code pass."
                "If your implementation emits a diagnostic absent from the existing baseline, your port is not faithful yet:"
                "read the source path again and fix the target behavior rather than changing expected output."
                "Run the affected build/tests and COMMIT the repair. Preserve valid parity gains."
                $"Sprint base commit: {baseCommit}" ]
            Agent.resume sid prompt $"Protect-S{sprintNum}" |> ignore
            attempt <- attempt + 1
            touched <- protectedFilesTouched baseCommit
        touched

    let private ensureInit () =
        let config = require()
        let db = currentDbPath (key())
        if not (File.Exists db) then let c = initSchema db in initSprint c 0 "" 0 0; c.Close()
        Beads.ensureEpic config.ProjectName |> ignore
        config

    let private nudgeBlock () =
        let nudgePath = Path.Combine(targetDir(), "nudge.md")
        if File.Exists nudgePath then
            let content = File.ReadAllText nudgePath
            File.Delete nudgePath // consume it — one-shot
            printfn $"  📌 Nudge picked up: {content.[..min 80 (content.Length-1)]}..."
            $"\n<human_nudge>\n{content}\n</human_nudge>"
        else ""

    let private buildBriefing config sprintNum dbBriefing allBuckets prevFailure =
        let prevBlock = match prevFailure with Some ctx -> $"\n<previous_failure>\n{trunc ctx 3000}\n</previous_failure>" | None -> ""
        let srcDir = config.SourceDir
        // Load project-specific briefing template if it exists; otherwise use generic porting briefing.
        // The template can use {{sprint}}, {{source_lang}}, {{target_lang}}, {{source_dir}} placeholders.
        let templatePath = Path.Combine(targetDir(), ".github", "instructions", "sprint-briefing.md")
        let projectSpecific =
            if File.Exists templatePath then
                let raw = File.ReadAllText templatePath
                raw.Replace("{{sprint}}", string sprintNum)
                   .Replace("{{source_lang}}", config.SourceLang)
                   .Replace("{{target_lang}}", config.TargetLang)
                   .Replace("{{source_dir}}", srcDir)
                   .Replace("{{project}}", config.ProjectName)
            else ""
        String.concat "\n" [
            $"Sprint {sprintNum}. Port {config.SourceLang} logic to {config.TargetLang}."
            ""
            "<sprint_scope>"
            "THIS SPRINT = 2 MONTHS OF DEVELOPER TIME. Plan accordingly."
            ""
            "AI agents MASSIVELY UNDERESTIMATE what they can do in a single session."
            "What feels like 'a lot of work' is actually a reasonable single-session task."
            "You have a 1-million token context window. You can read 50 full source files,"
            "port thousands of lines, and make dozens of commits in one sprint."
            ""
            "A MEDIOCRE sprint: reads a few files, ports 1-2 functions, makes 2 commits."
            "A GOOD sprint: ports an ENTIRE source file (1000-3000 LOC), makes 10+ commits, flips 20+ tests."
            "A GREAT sprint: ports MULTIPLE source files, flips 50+ tests, moves parity by percentage points."
            ""
            "Be the GREAT sprint."
            "</sprint_scope>"
            ""
            "<parity_rules>"
            "EVERY COMMIT MUST MAKE AT LEAST ONE TEST FLIP FROM FAIL TO PASS."
            "Measure before. Port. Measure after. Delta must be positive."
            "Commit message format: 'area: description (NNN/MMM → NNN+K/MMM)'"
            ""
            "BANNED (these waste cycles without parity gains):"
            "  - Refactoring that doesn't fix a failing test"
            "  - Review-fix rounds on comments/formatting"
            "  - Documentation instead of actual porting"
            "  - Infrastructure that doesn't produce test wins in THIS sprint"
            ""
            "REQUIRED WORKFLOW:"
            "  1. Run the project's test suite and note the current pass count"
            "  2. Pick a failing bucket. Read the ENTIRE source file for that area."
            "     Not a function. The ENTIRE FILE. You have 1M tokens."
            "  3. Port EVERY unported function in that file. Build + test after each one."
            "  4. Each commit must increase the pass count. Keep porting until it does."
            "  5. When the file is done, pick the NEXT failing area and repeat."
            "  6. STOP ONLY when you have genuinely run out of productive porting work to do."
            "</parity_rules>"
            ""
            // Project-specific instructions from template file
            if projectSpecific <> "" then $"<project_instructions>\n{projectSpecific}\n</project_instructions>"
            ""
            $"Source reference: {srcDir}"
            "You MUST commit your changes. Do NOT push."
            ""
            $"<test_status>\n{dbBriefing}\n</test_status>"
            $"\n<failing_buckets>\n{allBuckets}\n</failing_buckets>"
            ""
            "<persistence>"
            "You are NOT 'almost done' after 2 commits. You are NOT 'making good progress'."
            "You are done when the failing bucket count has materially dropped and you've"
            "ported every function you can from the source. If you've only changed 200 lines,"
            "you have barely started. Keep going."
            "</persistence>"
            nudgeBlock ()
            prevBlock
        ]

    /// Design-review-BEFORE-implementation: a PROPOSER drafts a port design (no code),
    /// an adversarial CRITIC checks it is a FAITHFUL port (not an invention/gate/patch)
    /// against the cited source anchors, and the proposer revises until approved (bounded).
    /// Returns the approved (or best) design text to hand to the implementor. Cheap
    /// (usually 2 agent calls) and catches unfaithful ports before any code is written.
    let private designReview config sprintNum (baseBrief: string) bead : string * bool =
        let maxRounds = 2
        let proposePrompt = String.concat "\n" [
            $"You are the PROPOSER for sprint {sprintNum} of a {config.SourceLang} -> {config.TargetLang} PORT."
            "Do NOT write code and do NOT change files. Produce a concise DESIGN for the slice you will port."
            $"THIS IS A PORT: the complete source is at {config.SourceDir}. You TRANSLATE it, you do not invent."
            "FIRST actually OPEN and READ the source anchors (and the target files you would change) — do not"
            "design from memory. Quote the specific source lines you will reproduce."
            "Your design MUST state:"
            "  1. The exact source symbol(s)/logic to port, with file:line anchors you HAVE READ (quote 2-3 key lines)."
            "  2. The target file(s)/functions to change, and how the source logic maps onto them."
            "  3. Why this is FAITHFUL to the source (a port, not an invention/gate/suppression/patch)."
            "  4. The EXPECTED NET matching delta PER affected project (matches gained minus new false positives)."
            "     If your honest estimate is ~0 net new matching diagnostics, this design is PARITY-NEUTRAL:"
            "     DO NOT propose it — pick the biggest-leverage keystone slice instead (parity-strategy.instructions.md)."
            "  5. Regression risks and how the build+test gate will catch them."
            "Prefer the biggest coherent structural slice you can land, not a one-liner. Wrap the final plan in <DESIGN> ... </DESIGN>."
            ""
            baseBrief ]
        let (design0, proposerSid) = Agent.run proposePrompt $"Propose-S{sprintNum}" None
        Beads.note bead "PHASE:design proposed"
        let mutable design = design0
        let mutable approved = false
        let mutable round = 0
        while not approved && round < maxRounds do
            let criticPrompt = String.concat "\n" [
                $"You are the CRITIC — an adversarial DESIGN reviewer for a {config.SourceLang} -> {config.TargetLang} PORT."
                "You review a DESIGN, not code. You WRITE NO CODE and change NO files."
                "OPEN the cited source file:line anchors yourself and verify the design is a FAITHFUL, high-leverage port:"
                "  - Does it REPRODUCE the source logic, or invent / gate / suppress / patch around it? (invention => REJECT)"
                "  - Are the anchors real, correct, and actually READ (are the quoted lines genuine)? (fabricated anchors => REJECT)"
                "  - Is the expected NET matching delta clearly POSITIVE? A parity-neutral / coverage-only slice => REJECT."
                "  - Is this the biggest-leverage slice available, or nibbling while a KEYSTONE is open? (nibbling => REJECT)"
                "  - Will it regress existing matching diagnostics? Is the slice coherent and shippable?"
                "Give 2-5 specific, actionable fixes if you reject."
                "YOUR LAST LINE MUST BE EXACTLY ONE TOKEN: DESIGN_APPROVED  or  DESIGN_REJECTED  (nothing else on that line)."
                "Approve ONLY a faithful, source-grounded, net-parity-positive port. When in doubt, REJECT."
                ""
                "=== DESIGN UNDER REVIEW ==="
                design ]
            let (criticOut, _) = Agent.run criticPrompt $"Critic-S{sprintNum}" None
            if criticOut.Contains "DESIGN_APPROVED" && not (criticOut.Contains "DESIGN_REJECTED") then
                approved <- true
                Beads.note bead $"PHASE:design approved round={round+1}"
            else
                Beads.note bead $"PHASE:design rejected round={round+1}"
                let revisePrompt = String.concat "\n" [
                    "The CRITIC rejected your design. Revise it to address EVERY point. Still NO code, NO file changes."
                    "Re-OPEN and re-READ the source anchors. If the slice is parity-neutral or a nibble, switch to the"
                    "biggest-leverage KEYSTONE slice instead. Wrap the revised plan in <DESIGN> ... </DESIGN>."
                    ""
                    "=== CRITIC FEEDBACK ==="
                    trunc criticOut 3000 ]
                design <- Agent.resume proposerSid revisePrompt $"Revise-S{sprintNum}"
                round <- round + 1
        if not approved then
            Beads.note bead "PHASE:design UNAPPROVED — escalating to keystone campaign"
            printfn $"  ⚠ Design not approved after {maxRounds} rounds; escalating this sprint to a KEYSTONE CAMPAIGN."
        else
            printfn "  ✔ Design approved."
        (design, approved)

    let step maxRetries prevFailure : bool * string =
        let config = ensureInit ()
        let db = currentDbPath (key())
        let conn = initSchema db
        let sNum = currentSprintNum conn
        conn.Close()

        // ALWAYS harvest before deciding what to do — stale data causes false "All pass!"
        printfn "  📊 Harvesting current test results..."
        harvestTests config (max sNum 0)

        let conn2 = initSchema db
        let next = sNum + 1
        let (pp, pt) = passRate conn2
        let (pMatch0, _pMiss0, pSup0) = parityTotals conn2
        let pby0 = parityByProject conn2   // per-project snapshot BEFORE the implementor (for the ratchet)
        let ranked = bucketsRanked conn2
        match ranked with
        | [] when pt = 0 ->
            // No test data — bootstrap: agent discovers how to test, creates harvest script
            conn2.Close()
            printfn $"S{next} | No test data — bootstrap sprint"
            let harvestContract = String.concat "\n" [
                "The orchestrator needs a HarvestCommand in project.json that runs all tests and outputs results."
                "Output format: one line per test, tab-separated:"
                "  STATUS\\tBUCKET\\tTEST_ID[\\tERROR_MSG]"
                "  STATUS = pass|fail|crash|timeout|skip"
                "  BUCKET = logical grouping (e.g. unit-parser, baseline, integration)"
                "  TEST_ID = unique test name"
                "  ERROR_MSG = optional, first line of failure message"
                ""
                "Your job:"
                "1. Figure out how this project runs tests (read Makefile, CI config, docs)"
                "2. Create a script (in _tools/ or similar) that runs ALL test layers and outputs TSV"
                "3. Add \"HarvestCommand\": \"<command>\" to project.json"
                "4. Run the command yourself to verify it works and produces correct output"
                "5. Commit everything" ]
            let prompt = String.concat "\n" [
                $"Sprint {next}: BOOTSTRAP. Project: {config.ProjectName}."
                $"Source language: {config.SourceLang}. Target language: {config.TargetLang}."
                $"Source reference: {config.SourceDir}."
                ""
                "Read all available docs: README, Makefile, CI config, copilot-instructions, porting-plan."
                "Set up the test harness so tests can be discovered, run, and measured."
                ""
                harvestContract
                ""
                "You MUST commit your changes. Do NOT push." ]
            let bead = Beads.createSprint next "bootstrap" "Empty DB"
            Beads.claim bead
            let sc = initSchema db in initSprint sc next "bootstrap" 0 0; sc.Close()
            let (_, _) = Agent.run prompt $"Impl-S{next}" None
            let pushResult = safePush ()
            if pushResult = 0 then printfn "  Pushed." else printfn "  ⚠ push failed"
            Beads.closeSuccess bead "Bootstrap done"
            printfn "  Bootstrap done. Continuing to first improvement sprint..."
            (true, "BOOTSTRAP") // continue — don't stop
        | [] when pt > 0 ->
            // All buckets pass — BUT verify this isn't a false positive from a build failure.
            // A build failure can silently drop test count (e.g., merge conflict markers in Go code
            // cause test packages to fail to compile → harvest sees only a subset → 0 failing buckets).
            // Guard: if total tests dropped >20% from the historical high, it's a build failure, not success.
            let histHigh =
                try
                    let hConn = initSchema db
                    let cmd = hConn.CreateCommand()
                    cmd.CommandText <- "SELECT MAX(total_tests) FROM sprint"
                    let result = cmd.ExecuteScalar()
                    hConn.Close()
                    match result with :? int64 as v -> int v | :? int as v -> v | _ -> pt
                with _ -> pt
            if pt < histHigh * 80 / 100 then
                conn2.Close()
                printfn $"  🚨 FALSE ALL-PASS: {pt} tests vs historical high {histHigh} — build likely broken!"
                printfn "  Attempting build fix sprint..."
                let fixPrompt = String.concat "\n" [
                    $"Sprint {next}: BUILD REPAIR. The test count dropped from {histHigh} to {pt}."
                    "This means the Go code has a build failure causing test packages to not compile."
                    "Run: go build ./... and fix ALL compile errors."
                    "Common causes: merge conflict markers (<<<<<<< ======= >>>>>>>), missing imports, syntax errors."
                    "Search for: <<<<<<< in all .go files: grep -r '<<<<<<' internal/"
                    "Fix every build error. Then run: go test -timeout 60s ./internal/..."
                    "Commit the fix." ]
                let (_, _) = Agent.run fixPrompt $"BuildFix-S{next}" None
                (false, "BUILD_REPAIR")
            else
                conn2.Close(); printfn "All pass!"; (true, "ALL_PASS")
        | _ ->
            // Normal sprint: there are failing buckets to fix.
            let allBuckets =
                ranked |> List.truncate 50 |> List.map (fun (b, l, f, t) -> $"  {b} ({l}): {f}/{t} failing")
                |> String.concat "\n"
                |> fun s -> if ranked.Length > 50 then s + $"\n  … (+{ranked.Length - 50} more failing buckets)" else s
            let brief = briefing conn2
            conn2.Close()
            printfn $"S{next} | {pp}/{pt} | {ranked.Length} failing buckets"

            // Focus selection. Most sprints attack the biggest real-world parity bucket,
            // but leaf-by-leaf nibbling never cracks the KEYSTONES (foundational ports that
            // each unblock hundreds-to-thousands of diagnostics), so we DEDICATE sprints to
            // them: every 3rd sprint, AND whenever the implementor has stalled (a stall is
            // almost always a keystone wall). Every 5th (non-keystone) sprint works a
            // DIFFERENT project so one big noisy project cannot starve the others.
            // NOTE: the old coverage-<file> lane is gone — porting a no-diagnostic file
            // (e.g. a localizer) burned whole sprints for zero parity (coverage != parity).
            let streak = getNoCommitStreak (key())
            let projOf (b: string) =
                if b.StartsWith("parity-") then b.Substring(7).Replace("-fp", "") else ""
            let headBucket = ranked |> List.head |> fun (b, _, _, _) -> b
            let keystoneCampaign = (next % 3 = 0) || streak >= 2
            let topBucket =
                if keystoneCampaign then "keystone-campaign"
                elif next % 5 = 0 then
                    let headProj = projOf headBucket
                    match ranked |> List.tryFind (fun (b, _, _, _) -> b.StartsWith("parity-") && projOf b <> "" && projOf b <> headProj) with
                    | Some (b, _, _, _) -> b
                    | None -> headBucket
                else headBucket

            // THE PORT MANDATE — repeated at every hand-off. This is a PORT: the whole
            // source exists and must be TRANSLATED, never invented / gated / guessed.
            let portMandate = String.concat "\n" [
                ""
                "<PORT_MANDATE>"
                $"THIS IS A PORT, NOT A REWRITE. The COMPLETE source lives at {config.SourceDir}."
                "Every behaviour you need already exists there, battle-tested for years. TRANSLATE it —"
                "never invent, gate, suppress, or guess. If you cannot point at the source lines behind a"
                "change, you are INVENTING: stop and go read the source."
                "BEFORE writing ANY target code, OPEN and READ the exact source file:line ranges you will"
                "port, and quote the key lines you reproduce. Read the ENTIRE relevant source file, not one"
                "function — you have ~1M tokens; use them. Every ported block carries a real"
                "  // Ported from: <file>:<lines>  that a reviewer can open in the source."
                "</PORT_MANDATE>" ]

            // Keystone campaign directive: attack a foundational port, not a leaf.
            let keystoneDirective = String.concat "\n" [
                ""
                "<keystone_campaign>"
                "THIS SPRINT IS A KEYSTONE CAMPAIGN — do NOT nibble a leaf bucket."
                "DETERMINISM FIRST: if a project's parity numbers are NONDETERMINISTIC (they flap run-to-run,"
                "or your careful set-diff of a fix disagrees with the harvest's re-measured counts), then the"
                "single HIGHEST-LEVERAGE keystone is making the measurement REPRODUCIBLE — port the source's"
                "per-file cache/state reset faithfully. Noisy measurement makes EVERY other gain uncreditable"
                "(a faithful fix looks like noise and gets reverted), so fix it before anything else. This is"
                "not parity-neutral busywork — it unblocks crediting the whole biggest bucket."
                "Otherwise: open .github/instructions/parity-strategy.instructions.md and pick the HIGHEST-"
                "LEVERAGE open keystone (work top-down; each unblocks hundreds-to-thousands of diagnostics)."
                "Open its SOURCE anchor AND its target anchor side by side and READ THE ENTIRE source region."
                "Port the smallest COHERENT STRUCTURAL slice that compiles, keeps every test green, and moves"
                "the keystone — spanning MULTIPLE files if that is what the source does. Expect a BIG diff; do"
                "not fear width, the build+test HARD GATE guards you and reverts only real regressions. A"
                "landed keystone slice is worth 10-100x any leaf fix. Do NOT downgrade it into a one-function"
                "patch and do NOT substitute a gate/suppression for the real ported logic."
                "Cite // Ported from: on the ported logic so faithful structural work is credited even before"
                "it flips a (noisy) count."
                "</keystone_campaign>" ]

            let focusNotice =
                if keystoneCampaign then keystoneDirective
                else $"\n<focus_bucket>\nTHIS SPRINT focus on the bucket: {topBucket}. Work the biggest lever inside it; PORT the responsible source logic (read the source, cite file:line). Do not nibble — if the bucket is keystone-blocked, port the keystone slice from the source.\n</focus_bucket>"

            let baseBrief = (buildBriefing config next brief allBuckets prevFailure) + focusNotice
            let bead = Beads.createSprint next topBucket $"Pre:{pp}/{pt}"
            Beads.claim bead
            let sc = initSchema db in initSprint sc next topBucket pp pt; sc.Close()

            // Design-review BEFORE implementation: a PROPOSER drafts a FAITHFUL port design,
            // an adversarial CRITIC verifies it against the cited source and MUST emit a verdict
            // token. A design that cannot earn DESIGN_APPROVED in the bounded rounds is NOT
            // implemented as-is — the sprint escalates to a KEYSTONE CAMPAIGN (a rejected leaf
            // is exactly the "nibbling while a keystone is open" the critics keep flagging).
            Beads.note bead "PHASE:design"
            let (approvedDesign, designApproved) = designReview config next baseBrief bead
            let runKeystone = keystoneCampaign || not designApproved
            let prompt =
                baseBrief + portMandate
                + (if runKeystone && not keystoneCampaign then
                       "\n\n<design_rejected>\nThe proposed leaf design did NOT earn DESIGN_APPROVED — do NOT implement it as-is."
                       + " Instead run the KEYSTONE CAMPAIGN below: attack the foundational port it was nibbling around.\n"
                       + keystoneDirective + "\n</design_rejected>"
                   else "")
                + (if designApproved then
                       "\n\n<approved_design>\nA proposer/critic review APPROVED this port design. IMPLEMENT IT faithfully from the source."
                       + " Deviate only if the source code proves it wrong (and say why).\n" + approvedDesign + "\n</approved_design>"
                   else
                       "\n\n<best_design_notes>\nUnapproved proposer notes — use ONLY what you can verify is faithful to the source:\n" + trunc approvedDesign 2000 + "\n</best_design_notes>")

            // Reattach HEAD to the active branch BEFORE capturing baseCommit, so the sprint
            // works ON the branch tip: the implementor's commits then advance the branch
            // linearly and safePush fast-forwards them. (A prior sprint's agent may have left
            // HEAD detached; without this, baseCommit is a detached commit and the branch can
            // diverge, which is how large landed sprints got orphaned.)
            if activeBranch <> "" then
                let attached = (cli { Exec "git"; Arguments [| "symbolic-ref"; "-q"; "HEAD" |] } |> Command.execute).ExitCode = 0
                if not attached then
                    (try cli { Exec "git"; Arguments [| "checkout"; activeBranch |] } |> Command.execute |> ignore with _ -> ())
                    printfn $"  🔧 reattached HEAD to {activeBranch} before sprint (was detached)"

            // Record base commit BEFORE implementor runs — this defines the sprint's diff scope
            let baseCommit =
                try (cli { Exec "git"; Arguments [|"rev-parse"; "HEAD"|] } |> Command.execute).Text |> Option.defaultValue "HEAD" |> fun s -> s.Trim()
                with _ -> "HEAD"

            Beads.note bead $"PHASE:impl baseCommit={baseCommit.[..7]} streak={streak} keystone={runKeystone} focus={topBucket}"
            let (implOut, sid) = Agent.run prompt $"Impl-S{next}" None

            // Capture implementor stdout — diagnostic gold for debugging no-commit sprints.
            let logPath =
                try writeImplLog (key()) next implOut
                with ex -> eprintfn $"  ⚠ failed to write impl log: {ex.Message}"; ""
            if logPath <> "" then printfn $"  📝 impl log: {logPath} ({implOut.Length} chars)"

            let headAfterImpl =
                try (cli { Exec "git"; Arguments [|"rev-parse"; "HEAD"|] } |> Command.execute).Text |> Option.defaultValue "" |> fun s -> s.Trim()
                with _ -> ""
            if headAfterImpl = baseCommit then
                let newStreak = incrementNoCommitStreak (key())
                Beads.note bead $"PHASE:impl:NO_COMMITS streak={newStreak}"
                printfn $"  ⚠ Implementor made no commits (streak={newStreak})"
                // Surface the tail of the impl log so the human can see WHY immediately
                if implOut.Length > 0 then
                    let tail = if implOut.Length > 800 then implOut.Substring(implOut.Length - 800) else implOut
                    printfn $"  ── impl tail ──\n{tail}\n  ───────────────"
            else
                resetNoCommitStreak (key())
                Beads.note bead $"PHASE:impl:done commits={headAfterImpl.[..7]}"

            // Protected files are immutable oracles. Push back immediately and let the
            // implementor repair its code before spending a full harvest/verifier cycle.
            // Previously this was detected only at the final gate, so valuable candidates
            // were discarded wholesale without one correction turn.
            let mutable protectedTouched = repairProtectedFiles baseCommit sid bead next maxRetries

            // Harvest test results AFTER every implementor revision — orchestrator-owned
            // measurement. Verifier-requested fixes must be re-harvested before credit.
            Beads.note bead "PHASE:harvest"
            harvestTests config next

            let mutable retries = 0
            let mutable passed = false
            let mutable lastFail = ""

            while retries <= maxRetries && not passed do
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
                    if retries < maxRetries then
                        Agent.resume sid fb $"Fix-S{next}" |> ignore
                        protectedTouched <- repairProtectedFiles baseCommit sid bead next maxRetries
                        Beads.note bead $"PHASE:reharvest attempt={retries+1}"
                        harvestTests config next
                    retries <- retries + 1

            let fc = initSchema db
            let (fp, ft) = passRate fc
            let d = fp - pp
            let (pMatch1, _pMiss1, pSup1) = parityTotals fc
            let pby1 = parityByProject fc   // per-project snapshot AFTER the implementor
            let pProj1 = parityProjectCount fc
            finalizeSprint fc fp ft
            fc.Close()
            archiveAndReset (key()) next

            // Parity deltas from BUCKET STATS. passRate/d counts test ROWS; parity
            // buckets carry no pass rows, so parity progress is invisible to d — the
            // credit gate MUST read parityTotals. (This was the credit-assignment
            // inversion: real porting showed d<=0 and was discarded.)
            // Fail-closed: if fewer than 6 parity project buckets were harvested, the
            // checker build/run failed and the objective silently vanished (F2).
            let parityHarvestBroken = pMatch0 > 0 && pProj1 < 6
            let matchGain = pMatch1 - pMatch0        // global, for the report line only
            // PER-PROJECT credit (agnostic — no project names). Each project's measurement
            // noise scales with its reference size (offset-cache nondeterminism flaps roughly
            // in proportion to file volume), so we judge each project against ITS OWN band.
            // A real win = net matches (matches gained minus new false positives) beyond that
            // band in ANY project — so a genuine +50 in a small quiet project is credited
            // instead of being drowned by a large noisy project's global flap, while a real
            // per-project matching loss or false-positive flood still hard-reverts.
            let band refc = max 20 (refc / 150)
            let m0 = pby0 |> List.map (fun (p,m,_,_) -> p, m) |> Map.ofList
            let s0 = pby0 |> List.map (fun (p,_,s,_) -> p, s) |> Map.ofList
            let perProj =
                pby1 |> List.map (fun (p, m1, s1, refc) ->
                    let mg = m1 - (Map.tryFind p m0 |> Option.defaultValue m1)
                    let sd = s1 - (Map.tryFind p s0 |> Option.defaultValue s1)
                    let net = mg - (max 0 sd)          // pay one match per new false positive
                    (p, mg, sd, net, band refc))
            let realGain = perProj |> List.sumBy (fun (_,_,_,net,b) -> if net > b then net else 0)
            let realMatchLoss = (not parityHarvestBroken) && (perProj |> List.exists (fun (_,mg,_,_,b) -> -mg > b))
            let fpFlood = (not parityHarvestBroken) && (perProj |> List.exists (fun (_,_,sd,net,b) -> sd > b && net < 0))
            let precisionWin = (not parityHarvestBroken) && pSup1 < pSup0
            let gainStr = (if matchGain >= 0 then "+" else "") + string matchGain
            let msg = $"parity match {pMatch0}->{pMatch1} ({gainStr}) fp {pSup0}->{pSup1} | tests {fp}/{ft} d={d}"
            Beads.note bead msg

            // Recheck after all verifier-requested revisions. This includes committed,
            // staged, and unstaged differences from the sprint base.
            protectedTouched <- protectedFilesTouched baseCommit

            // Marker-removal gate (F1 fix): removing a `// Ported from:` marker is only a
            // regression if NOT accompanied by a precision win — deleting over-broad ported
            // logic to kill false positives is legitimate porting work.
            let sourceIndexDb = PortStatus.sourceIndexPath ()
            let hasPortStatus = File.Exists sourceIndexDb
            let markerRegression =
                if hasPortStatus && not precisionWin then
                    let regressions = PortStatus.checkRegressions baseCommit
                    if regressions.IsEmpty then false
                    else
                        let regMsg = regressions |> List.map (fun r -> $"  REMOVED: {r}") |> String.concat "\n"
                        Beads.note bead $"COVERAGE_REGRESSION: {regressions.Length} markers removed"
                        let fixPrompt = String.concat "\n" [
                            "COVERAGE REGRESSION: these '// Ported from:' / '// TODO(port):' markers were removed WITHOUT a false-positive reduction:"
                            regMsg
                            "Restore them, or (if you refactored) move them to the new location with updated line ranges." ]
                        Agent.resume sid fixPrompt $"RestoreCoverage-S{next}" |> ignore
                        not (PortStatus.checkRegressions baseCommit).IsEmpty
                else false

            // Deterministic coverage progress (noise-free): new faithful ports add
            // validated `// Ported from:` markers. Credit this so structural/keystone
            // work accretes on HEAD even before it flips a diagnostic (kills the spin).
            let covGain = if hasPortStatus then PortStatus.coverageGain baseCommit else 0

            let hardRegression =
                parityHarvestBroken || (not passed) || not protectedTouched.IsEmpty || d < 0
                || realMatchLoss || fpFlood || markerRegression
            // Coverage credit: a faithful structural port that adds validated `// Ported
            // from:` markers is durable progress even before it flips a diagnostic — this is
            // what lets keystone / de-noising / infrastructure work ACCRETE. We must NOT gate
            // this on a global superfluous-count delta: the biggest project's parity is
            // NONDETERMINISTIC (its count flaps hundreds run-to-run), so a global-FP bound
            // silently reverts perfectly faithful, measured-zero-regression ports (a real
            // django reportUnbound fix was thrown away because the harvest re-rolled +3 FP
            // noise). The FP guard is the PER-PROJECT `fpFlood` (a project's FPs exceeding its
            // OWN noise band without compensating matches) — that catches real floods while
            // ignoring count noise. So: faithful markers + no real per-project loss/flood.
            let coverageCredit = covGain > 0 && not realMatchLoss && not fpFlood
            let durableProgress =
                (not parityHarvestBroken)
                && (coverageCredit || (d > 0) || (realGain > 0) || precisionWin)

            let revert (reason: string) =
                (try cli { Exec "git"; Arguments [| "reset"; "--hard"; baseCommit |] } |> Command.execute |> ignore with _ -> ())
                (try cli { Exec "git"; Arguments [| "clean"; "-fd" |] } |> Command.execute |> ignore with _ -> ())
                Beads.note bead $"REVERT: {reason}"
                Beads.closeFailed bead reason
                printfn $"  ⟲ Reverted: {reason}"

            if hardRegression then
                let pf = String.concat "," protectedTouched
                let reason =
                    if parityHarvestBroken then "checker build/run failed (parity harvest empty) — fix the build"
                    elif not passed then "verifiers failed"
                    elif not protectedTouched.IsEmpty then $"edited protected files: {pf}"
                    elif d < 0 then $"unit/baseline regression d={d}"
                    elif realMatchLoss then $"per-project parity matching lost beyond its noise band (global {pMatch0}->{pMatch1})"
                    elif fpFlood then $"per-project false-positive flood not paid for by matches (global fp {pSup0}->{pSup1})"
                    else "coverage markers removed"
                revert reason
                (false, reason)
            elif not durableProgress then
                revert "no durable progress (no real per-project net matching gain, no precision win, no coverage credit)"
                (false, "no durable progress")
            else
                Beads.closeSuccess bead msg; printfn $"  OK: {msg}"
                if hasPortStatus then
                    PortStatus.sync next        // markers -> 'partial'
                    PortStatus.promote next     // faithfulness-verified 'partial' -> 'complete'
                    let conv = PortStatus.convergenceStatus ()
                    printfn $"  📈 convergence: {conv}"
                    Beads.note bead $"convergence: {conv}"
                    if conv = "FULL_PRODUCT_DONE" then
                        printfn "  🏁 FULL PRODUCT PORTED — every source concept faithfully complete. Dropping to verify-only watch."
                // Knowledge capture — runs BEFORE push so its changes get included
                let capturePrompt = String.concat "\n" [
                    $"Sprint {next} landed durable progress ({msg})."
                    $"EXACT SCOPE: git diff {baseCommit}..HEAD"
                    "Capture only non-trivial, reusable learnings (say 'No learnings.' if none). Commit any files you create." ]
                Agent.run capturePrompt $"Knowledge-S{next}" None |> ignore
                let pushResult = safePush ()
                if pushResult <> 0 then printfn "  ⚠ git push failed" else printfn "  Pushed."
                (true, msg)

    let run maxRetries =
        let config = ensureInit ()
        printfn $"=== {config.ProjectName}: {config.SourceLang} -> {config.TargetLang} ==="
        // Capture the branch to push to (agents may later detach HEAD; safePush reattaches).
        activeBranch <-
            try (cli { Exec "git"; Arguments [| "symbolic-ref"; "-q"; "--short"; "HEAD" |] } |> Command.execute).Text
                |> Option.defaultValue "" |> fun s -> s.Trim()
            with _ -> ""
        if activeBranch <> "" then printfn $"  push target branch: {activeBranch}"
        let mutable go = true
        let mutable prev: string option = None
        let mutable consecutiveErrors = 0
        let mutable consecutiveFails = 0
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
                        "Perform the domain-expert review directly. Do NOT invoke or delegate to a subagent."
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
                    // Refactoring sprint. Capture base + pre-metrics BEFORE the agent runs.
                    // (Ghost-refactor bug: base was captured AFTER the agent, so verifiers saw
                    // an empty diff and the result was pushed unconditionally.)
                    let readMetrics () =
                        let cc = initSchema (currentDbPath (key()))
                        let (p, _) = passRate cc
                        let (m, _, s) = parityTotals cc
                        cc.Close()
                        (p, m, s)
                    let baseCommit =
                        try (cli { Exec "git"; Arguments [| "rev-parse"; "HEAD" |] } |> Command.execute).Text |> Option.defaultValue "HEAD" |> fun s -> s.Trim()
                        with _ -> "HEAD"
                    printfn "  📊 pre-refactor harvest..."
                    harvestTests config 0
                    let (rfp0, rMatch0, rSup0) = readMetrics ()
                    let refactorPrompt = String.concat "\n" [
                        "REFACTORING SPRINT. No new features. Test count / pass rate / parity MUST NOT decrease."
                        "Do NOT edit reference baselines, the source index DB, the harvest script, or project.json."
                        "An expert review found these improvement opportunities:"
                        reviewFeedback
                        "Pick the highest-impact improvements. Refactor, commit." ]
                    printfn "── Refactoring sprint ──"
                    let (_, refactorSid) = Agent.run refactorPrompt "Refactor" None
                    // Run verifiers against the pre-refactor base (real diff, not empty).
                    let results = Verifiers.listAll() |> List.map (fun v -> async {
                        let (vp,vo,vsid) = Verifiers.runVerifier v baseCommit
                        return (v,vp,vo,vsid) }) |> Async.Parallel |> Async.RunSynchronously |> Array.toList
                    let failed = results |> List.filter (fun (_,vp,_,_) -> not vp)
                    if not failed.IsEmpty then
                        let fb = failed |> List.map (fun (v,_,vo,_) -> $"=== {v} ===\n{trunc vo 2000}") |> String.concat "\n\n"
                        Agent.resume refactorSid fb "Fix-Refactor" |> ignore
                    let verifiersFailed =
                        results
                        |> List.map (fun (v,vp,_,vsid) -> if vp then true else (let (r,_) = Verifiers.resumeVerifier vsid v in r))
                        |> List.forall id |> not
                    // Re-measure and gate: revert unless verifiers pass AND nothing regressed.
                    printfn "  📊 post-refactor harvest..."
                    harvestTests config 0
                    let (rfp1, rMatch1, rSup1) = readMetrics ()
                    let regressed =
                        verifiersFailed || rfp1 < rfp0 || (rMatch0 - rMatch1) > 250 || (rSup1 - rSup0) > 250
                    if regressed then
                        (try cli { Exec "git"; Arguments [| "reset"; "--hard"; baseCommit |] } |> Command.execute |> ignore with _ -> ())
                        (try cli { Exec "git"; Arguments [| "clean"; "-fd" |] } |> Command.execute |> ignore with _ -> ())
                        printfn "  ⟲ Refactor reverted (verifier failure or regression)."
                    else
                        safePush () |> ignore
                        printfn "── Refactoring done (pushed) ──"
                else
                    let (ok, s) = step maxRetries prev
                    consecutiveErrors <- 0
                    if s = "ALL_PASS" then
                        // Do NOT terminate (Goal 8: must not stop). Every harvested test +
                        // all real-world parity passing is a milestone, not the end — the
                        // whole product is still not ported. Idle briefly, then continue;
                        // the next harvest will surface cold-coverage / precision work.
                        printfn "  ✅ All tracked buckets pass — continuing (cold coverage / precision / corpus). Idle 10 min."
                        prev <- None
                        consecutiveFails <- 0
                        System.Threading.Thread.Sleep(10 * 60 * 1000)
                    elif ok then
                        prev <- None
                        consecutiveFails <- 0
                    else
                        prev <- Some s
                        consecutiveFails <- consecutiveFails + 1
                        if consecutiveFails >= 3 then
                            printfn $"  ⏳ {consecutiveFails} consecutive failed sprints — cooling down 5 min"
                            System.Threading.Thread.Sleep(5 * 60 * 1000)
                        if consecutiveFails >= 10 then
                            printfn "  ⏳ 10 consecutive failures — extended cooldown 30 min"
                            System.Threading.Thread.Sleep(25 * 60 * 1000) // extra 25 on top of the 5
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
            if File.Exists db then let cn = initSchema db in printfn $"{dashboard cn}"; cn.Close()
            let trend = trendData k
            if trend.Length > 0 then
                printfn ""
                printfn $"{renderChart trend 30}"
        | None -> printfn "No project.json."

    /// Watch mode — refreshes status every N seconds.
    let watch interval =
        let cts = new System.Threading.CancellationTokenSource()
        Console.CancelKeyPress.Add(fun a -> a.Cancel <- true; cts.Cancel())
        try
            while not cts.Token.IsCancellationRequested do
                Console.Clear()
                status ()
                printfn $"\n(refreshing every {interval}s — Ctrl+C to stop)"
                try cts.Token.WaitHandle.WaitOne(interval * 1000) |> ignore with :? OperationCanceledException -> ()
        with :? OperationCanceledException -> ()
        printfn "\nStopped."

    /// Show phase timing breakdown from beads comments.
    let timing () =
        // Query all sprint tasks
        let tasksJson = Beads.run ["query"; "type=task"; "--json"]
        if tasksJson = "" then printfn "No sprint data." else
        try
            let doc = System.Text.Json.JsonDocument.Parse(tasksJson)
            let tasks = doc.RootElement.EnumerateArray() |> Seq.toList
                        |> List.sortBy (fun t -> t.GetProperty("created_at").GetString())
            printfn "=== PHASE TIMING ==="
            for task in tasks do
                let id = task.GetProperty("id").GetString()
                let title = task.GetProperty("title").GetString()
                let commentsJson = Beads.run ["comments"; id; "--json"]
                if commentsJson <> "" && commentsJson <> "[]" then
                    try
                        let cdoc = System.Text.Json.JsonDocument.Parse(commentsJson)
                        let comments = cdoc.RootElement.EnumerateArray() |> Seq.toList
                                       |> List.choose (fun c ->
                                           try
                                               let text = c.GetProperty("text").GetString()
                                               let ts = DateTime.Parse(c.GetProperty("created_at").GetString())
                                               Some (ts, text)
                                           with _ -> None)
                                       |> List.sortBy fst
                        if comments.Length > 0 then
                            printfn ""
                            printfn $"  {title} ({id})"
                            let mutable prev = comments.Head |> fst
                            for (ts, text) in comments do
                                let delta = (ts - prev).TotalMinutes
                                let deltaStr = if delta < 1.0 then "" else sprintf "+%.0fm" delta
                                let tsStr = ts.ToString("HH:mm:ss")
                                printfn $"    {tsStr} {deltaStr,6}  {text}"
                                prev <- ts
                            let total = ((comments |> List.last |> fst) - (comments.Head |> fst)).TotalMinutes
                            printfn $"    ───── total: %.0f{total}m"
                    with _ -> ()
            printfn ""
        with ex -> eprintfn $"Parse error: {ex.Message}"

let retries rest = rest |> List.tryFind (fun (s:string) -> s.StartsWith "--retries=") |> Option.map (fun s -> int(s.Split('=').[1])) |> Option.defaultValue 3

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| "run" :: r -> ConvergenceLoop.run (retries r)
| "step" :: r -> ConvergenceLoop.step (retries r) None |> ignore
| ["status"] -> ConvergenceLoop.status ()
| ["timing"] -> ConvergenceLoop.timing ()
| "watch" :: r ->
    let interval = r |> List.tryHead |> Option.map int |> Option.defaultValue 30
    ConvergenceLoop.watch interval
| _ ->
    printfn "ralph-port"
    printfn "  run   [--retries=N]  Autonomous loop. Ctrl+C safe."
    printfn "  step  [--retries=N]  One sprint."
    printfn "  status               Current state + progress chart."
    printfn "  timing               Phase timing breakdown from beads."
    printfn "  watch [seconds]      Live dashboard (default: 30s refresh)."