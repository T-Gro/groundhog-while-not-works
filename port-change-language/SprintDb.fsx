#!/usr/bin/env dotnet fsi
/// Sprint-level test tracking in SQLite.
/// Schema: sprint (identity) → buckets (groups) → tests (individual results).
/// Accumulates across sprints for trend detection + bucket ranking.
#r "nuget: Microsoft.Data.Sqlite"

open System
open System.IO
open Microsoft.Data.Sqlite

module SprintDb =

    // ── Paths ────────────────────────────────────────────────────
    let private runtimeDir key =
        let d = Path.Combine(__SOURCE_DIRECTORY__, ".ralph-port", key)
        Directory.CreateDirectory d |> ignore; d

    let projectKey dir = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    let dbPath     key = Path.Combine(runtimeDir key, "current_results.db")
    let logDir     key = let d = Path.Combine(runtimeDir key, "sprint_logs") in Directory.CreateDirectory d |> ignore; d

    // ── Streak counter (file-backed, survives restarts) ──────────
    let private streakFile key = Path.Combine(runtimeDir key, "no_commit_streak.txt")
    let getStreak      key = let p = streakFile key in if File.Exists p then match Int32.TryParse(File.ReadAllText(p).Trim()) with true, n -> n | _ -> 0 else 0
    let setStreak  key n   = File.WriteAllText(streakFile key, string n)
    let bumpStreak key     = let n = getStreak key + 1 in setStreak key n; n
    let resetStreak key    = setStreak key 0

    // ── Implementor log (last 200KB, for debugging no-commit sprints) ──
    let writeLog key sprint text =
        let p = Path.Combine(logDir key, $"sprint_{sprint:D4}.log")
        File.WriteAllText(p, if String.length text > 200_000 then text.[text.Length-200_000..] else text); p

    // ── Schema ───────────────────────────────────────────────────
    let init dbPath =
        Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
        let conn = new SqliteConnection($"Data Source={dbPath}")
        conn.Open()
        let cmd = conn.CreateCommand()
        cmd.CommandText <- """
            CREATE TABLE IF NOT EXISTS sprint (
                sprint_num    INTEGER NOT NULL,
                started_at    TEXT NOT NULL DEFAULT (datetime('now')),
                finished_at   TEXT,
                target_bucket TEXT,
                passing       INTEGER DEFAULT 0,
                total_tests   INTEGER DEFAULT 0,
                error_count   INTEGER,
                regression_count INTEGER
            );
            CREATE TABLE IF NOT EXISTS buckets (
                id          TEXT PRIMARY KEY,
                description TEXT,
                layer       TEXT,
                total_tests INTEGER DEFAULT 0,
                passing     INTEGER DEFAULT 0,
                failing     INTEGER DEFAULT 0,
                crashing    INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS tests (
                id             TEXT PRIMARY KEY,
                bucket_id      TEXT,
                status         TEXT NOT NULL,
                error_message  TEXT,
                error_category TEXT
            );"""
        cmd.ExecuteNonQuery() |> ignore; conn

    let initSprint conn num bucket pp pt =
        let cmd = conn.CreateCommand()
        cmd.CommandText <- $"INSERT INTO sprint (sprint_num, target_bucket, passing, total_tests) VALUES ({num}, '{bucket}', {pp}, {pt})"
        try cmd.ExecuteNonQuery() |> ignore with _ -> ()

    let finalize conn pp pt =
        let cmd = conn.CreateCommand()
        cmd.CommandText <- $"UPDATE sprint SET finished_at = datetime('now'), passing = {pp}, total_tests = {pt} WHERE finished_at IS NULL"
        cmd.ExecuteNonQuery() |> ignore

    // ── Queries ──────────────────────────────────────────────────
    let sprintNum conn =
        let cmd = (conn: SqliteConnection).CreateCommand()
        cmd.CommandText <- "SELECT COALESCE(MAX(sprint_num), 0) FROM sprint"
        cmd.ExecuteScalar() |> function :? int64 as v -> int v | :? int as v -> v | _ -> 0

    let passRate conn =
        let cmd = (conn: SqliteConnection).CreateCommand()
        cmd.CommandText <- "SELECT COALESCE(SUM(passing),0), COALESCE(SUM(total_tests),0) FROM buckets"
        use r = cmd.ExecuteReader()
        if r.Read() then (r.GetInt32 0, r.GetInt32 1) else (0, 0)

    /// Buckets ranked by most failing, for sprint targeting.
    let rankedBuckets conn =
        let cmd = (conn: SqliteConnection).CreateCommand()
        cmd.CommandText <- "SELECT id, layer, total_tests - passing, total_tests FROM buckets WHERE total_tests - passing > 0 ORDER BY 3 DESC"
        use r = cmd.ExecuteReader()
        [ while r.Read() do yield (r.GetString 0, r.GetString 1, r.GetInt32 2, r.GetInt32 3) ]

    /// Top-bucket sample failures for the implementor briefing.
    let briefing conn =
        let ranked = rankedBuckets conn
        let (pp, pt) = passRate conn
        let lines = [ $"Overall: {pp}/{pt} passing ({if pt > 0 then 100*pp/pt else 0}%%)"
                      "" ]
        let bucketLines =
            ranked |> List.truncate 10
            |> List.map (fun (b, _, f, t) -> $"  {b}: {f}/{t} failing")
        let sampleLines =
            match ranked with
            | (topBucket, _, _, _) :: _ ->
                let cmd = conn.CreateCommand()
                cmd.CommandText <- $"SELECT id, error_message FROM tests WHERE bucket_id = '{topBucket}' AND status = 'fail' LIMIT 5"
                use r = cmd.ExecuteReader()
                [ yield $"\nTop bucket '{topBucket}' samples:"
                  while r.Read() do
                      let id = r.GetString 0
                      let msg = if r.IsDBNull 1 then "" else r.GetString 1
                      let firstLine = if msg.Length > 0 then msg.Split('\n').[0].[..min 120 (msg.Split('\n').[0].Length-1)] else ""
                      yield $"  {id}: {firstLine}" ]
            | [] -> []
        String.concat "\n" (lines @ bucketLines @ sampleLines)

    // ── Trend data for sparkline chart ───────────────────────────
    let trend key =
        let db = dbPath key
        if not (File.Exists db) then [||] else
        try
            let conn = init db
            let cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT sprint_num, passing, total_tests FROM sprint ORDER BY sprint_num"
            use r = cmd.ExecuteReader()
            [| while r.Read() do yield (r.GetInt32 0, r.GetInt32 1, r.GetInt32 2) |]
            |> fun a -> conn.Close(); a
        with _ -> [||]

    let renderChart (data: (int*int*int)[]) width =
        if data.Length = 0 then "" else
        let pcts = data |> Array.map (fun (_,p,t) -> if t > 0 then float p / float t * 100.0 else 0.0)
        let lo, hi = Array.min pcts, max (Array.max pcts) 100.0
        let scale v = int ((v - lo) / (hi - lo) * float width) |> max 0 |> min width
        let lines = [
            for i in 0 .. data.Length - 1 do
                let (sn, p, t) = data.[i]
                let pct = pcts.[i]
                let bar = String.replicate (scale pct) "█" + String.replicate (width - scale pct) "░"
                $"S{sn,3} {bar}  {pct,5:F1}%%  {p}/{t}" ]
        String.concat "\n" lines

    let dashboard conn =
        let (pp, pt) = passRate conn
        let ranked = rankedBuckets conn
        let sn = sprintNum conn
        String.concat "\n" [
            $"Sprint: {sn} | Overall: {pp}/{pt} ({if pt > 0 then 100*pp/pt else 0}%%)"
            ""
            if ranked.Length > 0 then "Top 5 failure buckets:"
            for (b, _, f, t) in ranked |> List.truncate 5 do $"  {b}: {f}/{t} failing" ]

    /// Archive current DB and reset for next sprint.
    let archiveAndReset key sprintNum =
        let src = dbPath key
        let dst = Path.Combine(runtimeDir key, $"sprint_{sprintNum:D4}.db")
        try File.Copy(src, dst, true) with _ -> ()
