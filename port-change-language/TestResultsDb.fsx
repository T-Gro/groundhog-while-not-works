#!/usr/bin/env dotnet fsi

/// TestResultsDb — SQLite database for test pass/fail tracking across sprints.
///
/// Each sprint writes its results here. Previous sprint DBs are NOT deleted —
/// they're kept for trend detection, but only the latest is "live."
///
/// Schema designed for:
///   - Fast pass rate queries (overall and per-bucket)
///   - Finding buckets of related failures for implementors to work on
///   - Detecting regressions (compare sprint N vs N-1)
///   - Hierarchy: bucket → test → failure detail

#r "nuget: Microsoft.Data.Sqlite"

open System
open System.IO
open Microsoft.Data.Sqlite

module TestResultsDb =

    /// Runtime data lives in .ralph-port/<project-key>/ inside the harness dir.
    /// Keyed by target folder name so parallel ports don't collide.
    /// This folder is gitignored — ephemeral runtime state, not source.
    let private runtimeDir (projectKey: string) =
        let dir = Path.Combine(__SOURCE_DIRECTORY__, ".ralph-port", projectKey)
        Directory.CreateDirectory dir |> ignore
        dir

    /// Derive project key from target directory path (just the folder name).
    let projectKey (targetDir: string) = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

    let currentDbPath (key: string) = Path.Combine(runtimeDir key, "current_results.db")
    let sprintDbPath (key: string) (sprintNum: int) = Path.Combine(runtimeDir key, $"sprint_{sprintNum:D4}.db")

    /// Public path helper for ad-hoc runtime artifacts (logs, streak counters, etc.).
    let runtimeFile (key: string) (relativePath: string) = Path.Combine(runtimeDir key, relativePath)

    /// File-backed counter of consecutive sprints where the implementor produced no commits.
    /// Re-entrant across orchestrator restarts; lets us rotate buckets when stuck.
    let noCommitStreakPath (key: string) = runtimeFile key "no_commit_streak.txt"
    let getNoCommitStreak (key: string) =
        let p = noCommitStreakPath key
        if File.Exists p then
            match Int32.TryParse(File.ReadAllText(p).Trim()) with
            | true, n -> n
            | _ -> 0
        else 0
    let setNoCommitStreak (key: string) (n: int) = File.WriteAllText(noCommitStreakPath key, string n)
    let incrementNoCommitStreak (key: string) =
        let n = getNoCommitStreak key + 1
        setNoCommitStreak key n
        n
    let resetNoCommitStreak (key: string) = setNoCommitStreak key 0

    /// Per-sprint implementor stdout log. Lets a human (or follow-up agent) see WHY
    /// the implementor decided not to commit. Truncated to last ~200 KB to stay sane.
    let implLogPath (key: string) (sprintNum: int) =
        let dir = runtimeFile key "sprint_logs"
        Directory.CreateDirectory dir |> ignore
        Path.Combine(dir, $"sprint_{sprintNum:D4}.log")
    let writeImplLog (key: string) (sprintNum: int) (text: string) =
        let p = implLogPath key sprintNum
        let trimmed = if text.Length > 200_000 then text.Substring(text.Length - 200_000) else text
        File.WriteAllText(p, trimmed)
        p

    /// Initialize the schema in a new or existing DB.
    let initSchema (dbPath: string) =
        Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
        let conn = new SqliteConnection($"Data Source={dbPath}")
        conn.Open()
        let cmd = conn.CreateCommand()
        cmd.CommandText <- """
            -- Sprint identity: exactly one row, always present.
            CREATE TABLE IF NOT EXISTS sprint (
                sprint_num INTEGER NOT NULL,
                started_at TEXT NOT NULL DEFAULT (datetime('now')),
                finished_at TEXT,
                target_bucket TEXT,
                pre_passing INTEGER,
                pre_total INTEGER,
                post_passing INTEGER,
                post_total INTEGER
            );

            CREATE TABLE IF NOT EXISTS buckets (
                id TEXT PRIMARY KEY,
                description TEXT,
                layer TEXT,
                total_tests INTEGER DEFAULT 0,
                passing INTEGER DEFAULT 0,
                failing INTEGER DEFAULT 0,
                crashing INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS tests (
                id TEXT PRIMARY KEY,
                bucket_id TEXT NOT NULL,
                sprint_num INTEGER NOT NULL,  -- which sprint recorded this result
                status TEXT NOT NULL CHECK(status IN ('pass','fail','crash','timeout','skip')),
                error_message TEXT,
                error_category TEXT,
                duration_ms INTEGER,
                FOREIGN KEY (bucket_id) REFERENCES buckets(id)
            );

            CREATE TABLE IF NOT EXISTS regressions (
                test_id TEXT NOT NULL,
                previous_status TEXT NOT NULL,
                current_status TEXT NOT NULL,
                sprint_num INTEGER NOT NULL,
                PRIMARY KEY (test_id, sprint_num)
            );

            CREATE INDEX IF NOT EXISTS idx_tests_bucket ON tests(bucket_id);
            CREATE INDEX IF NOT EXISTS idx_tests_status ON tests(status);
            CREATE INDEX IF NOT EXISTS idx_tests_sprint ON tests(sprint_num);
        """
        cmd.ExecuteNonQuery() |> ignore
        conn

    /// Initialize the sprint row (call once at start of a new sprint).
    let initSprint (conn: SqliteConnection) (sprintNum: int) (targetBucket: string) (prePassing: int) (preTotal: int) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "DELETE FROM sprint; INSERT INTO sprint (sprint_num, target_bucket, pre_passing, pre_total) VALUES ($n, $b, $pp, $pt)"
        cmd.Parameters.AddWithValue("$n", sprintNum) |> ignore
        cmd.Parameters.AddWithValue("$b", targetBucket) |> ignore
        cmd.Parameters.AddWithValue("$pp", prePassing) |> ignore
        cmd.Parameters.AddWithValue("$pt", preTotal) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Finalize the sprint row with post-sprint metrics.
    let finalizeSprint (conn: SqliteConnection) (postPassing: int) (postTotal: int) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "UPDATE sprint SET finished_at = datetime('now'), post_passing = $pp, post_total = $pt"
        cmd.Parameters.AddWithValue("$pp", postPassing) |> ignore
        cmd.Parameters.AddWithValue("$pt", postTotal) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Read the current sprint number.
    let currentSprintNum (conn: SqliteConnection) : int =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT sprint_num FROM sprint LIMIT 1"
        try cmd.ExecuteScalar() :?> int64 |> int with _ -> 0

    /// Upsert a bucket.
    let upsertBucket (conn: SqliteConnection) (id: string) (desc: string) (layer: string) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            INSERT INTO buckets (id, description, layer) VALUES ($id, $desc, $layer)
            ON CONFLICT(id) DO UPDATE SET description=$desc, layer=$layer
        """
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.Parameters.AddWithValue("$desc", desc) |> ignore
        cmd.Parameters.AddWithValue("$layer", layer) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Record a test result (tagged with sprint number).
    let recordTest (conn: SqliteConnection) (sprintNum: int) (id: string) (bucketId: string) (status: string) (errorMsg: string option) (errorCat: string option) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            INSERT OR REPLACE INTO tests (id, bucket_id, sprint_num, status, error_message, error_category)
            VALUES ($id, $bucket, $sn, $status, $err, $cat)
        """
        cmd.Parameters.AddWithValue("$id", id) |> ignore
        cmd.Parameters.AddWithValue("$bucket", bucketId) |> ignore
        cmd.Parameters.AddWithValue("$sn", sprintNum) |> ignore
        cmd.Parameters.AddWithValue("$status", status) |> ignore
        cmd.Parameters.AddWithValue("$err", errorMsg |> Option.map box |> Option.defaultValue (box DBNull.Value)) |> ignore
        cmd.Parameters.AddWithValue("$cat", errorCat |> Option.map box |> Option.defaultValue (box DBNull.Value)) |> ignore
        cmd.ExecuteNonQuery() |> ignore

    /// Recalculate bucket aggregates from test data.
    let refreshBucketStats (conn: SqliteConnection) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            UPDATE buckets SET
                total_tests = (SELECT COUNT(*) FROM tests WHERE bucket_id = buckets.id),
                passing = (SELECT COUNT(*) FROM tests WHERE bucket_id = buckets.id AND status = 'pass'),
                failing = (SELECT COUNT(*) FROM tests WHERE bucket_id = buckets.id AND status IN ('fail','skip')),
                crashing = (SELECT COUNT(*) FROM tests WHERE bucket_id = buckets.id AND status IN ('crash','timeout'))
        """
        cmd.ExecuteNonQuery() |> ignore

    /// Get overall pass rate.
    let passRate (conn: SqliteConnection) : int * int =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM tests WHERE status='pass'"
        let passing = cmd.ExecuteScalar() :?> int64 |> int
        cmd.CommandText <- "SELECT COUNT(*) FROM tests"
        let total = cmd.ExecuteScalar() :?> int64 |> int
        (passing, total)

    /// Real-world diagnostic parity aggregates read from the BUCKETS table (not
    /// test rows). Returns (matching, missing, superfluous). parity-<proj> buckets
    /// carry total_tests=reference count, passing=matching; parity-<proj>-fp buckets
    /// carry the superfluous (false-positive) count as total_tests. This is the
    /// signal the credit gate must use — passRate (test rows) is blind to parity.
    let parityTotals (conn: SqliteConnection) : int * int * int =
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT " +
            "COALESCE(SUM(CASE WHEN id LIKE 'parity-%' AND id NOT LIKE '%-fp' THEN passing END),0), " +
            "COALESCE(SUM(CASE WHEN id LIKE 'parity-%' AND id NOT LIKE '%-fp' THEN total_tests - passing END),0), " +
            "COALESCE(SUM(CASE WHEN id LIKE 'parity-%-fp' THEN total_tests END),0) " +
            "FROM buckets"
        use r = cmd.ExecuteReader()
        if r.Read() then (int (r.GetInt64 0), int (r.GetInt64 1), int (r.GetInt64 2)) else (0, 0, 0)

    /// Count of parity project buckets present (health check — must be 6, else the
    /// parity harvest failed and the objective silently vanished).
    let parityProjectCount (conn: SqliteConnection) : int =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT COUNT(*) FROM buckets WHERE id LIKE 'parity-%' AND id NOT LIKE '%-fp'"
        cmd.ExecuteScalar() :?> int64 |> int

    /// Get buckets sorted by most non-passing (highest-impact first).
    let bucketsRanked (conn: SqliteConnection) : (string * string * int * int) list =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, layer, (total_tests - passing), total_tests FROM buckets WHERE total_tests > passing ORDER BY (total_tests - passing) DESC"
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do
            yield (reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)) ]

    /// Get failing tests for a specific bucket (for implementor briefing).
    let failingInBucket (conn: SqliteConnection) (bucketId: string) (limit: int) : (string * string) list =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, COALESCE(error_message,'') FROM tests WHERE bucket_id=$b AND status != 'pass' ORDER BY error_category, id LIMIT $l"
        cmd.Parameters.AddWithValue("$b", bucketId) |> ignore
        cmd.Parameters.AddWithValue("$l", limit) |> ignore
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do yield (reader.GetString(0), reader.GetString(1)) ]

    /// Detect regressions vs a previous DB.
    let detectRegressions (currentConn: SqliteConnection) (previousDbPath: string) (sprintNum: int) : (string * string) list =
        if not (File.Exists previousDbPath) then []
        else
            // Attach previous DB
            use cmd = currentConn.CreateCommand()
            cmd.CommandText <- $"ATTACH DATABASE '{previousDbPath}' AS prev"
            cmd.ExecuteNonQuery() |> ignore

            cmd.CommandText <- """
                SELECT t.id, t.status FROM tests t
                INNER JOIN prev.tests pt ON t.id = pt.id
                WHERE pt.status = 'pass' AND t.status != 'pass'
            """
            use reader = cmd.ExecuteReader()
            let regs = [ while reader.Read() do yield (reader.GetString(0), reader.GetString(1)) ]

            // Record regressions
            for (testId, newStatus) in regs do
                use ins = currentConn.CreateCommand()
                ins.CommandText <- "INSERT OR IGNORE INTO regressions (test_id, previous_status, current_status, sprint_num) VALUES ($t, 'pass', $s, $n)"
                ins.Parameters.AddWithValue("$t", testId) |> ignore
                ins.Parameters.AddWithValue("$s", newStatus) |> ignore
                ins.Parameters.AddWithValue("$n", sprintNum) |> ignore
                ins.ExecuteNonQuery() |> ignore

            cmd.CommandText <- "DETACH DATABASE prev"
            cmd.ExecuteNonQuery() |> ignore
            regs

    /// Generate a compact briefing string for implementor context.
    let briefing (conn: SqliteConnection) : string =
        let sprintNum = currentSprintNum conn
        let (passing, total) = passRate conn
        let pct = if total > 0 then float passing / float total * 100.0 else 0.0
        let ranked = bucketsRanked conn |> List.truncate 10
        let bucketLines = ranked |> List.map (fun (id, layer, failing, tot) -> $"  {layer} {id}: {failing}/{tot} failing")
        // Show 5 sample failing tests + their first-line error from the TOP bucket so
        // the implementor has a concrete handle, not just counts. Without this the
        // loop stalled for 43+ consecutive sprints with no commits (observed 2026-05-12).
        let sampleBlock =
            match ranked with
            | (topId, _, _, _) :: _ ->
                let samples = failingInBucket conn topId 5
                if List.isEmpty samples then ""
                else
                    let firstLine (s: string) =
                        let t = s.Trim()
                        let nl = t.IndexOf('\n')
                        if nl < 0 then t else t.Substring(0, nl)
                    let lines =
                        samples |> List.map (fun (testId, errMsg) ->
                            let trimmedTest = if testId.Length > 60 then testId.Substring(0, 57) + "..." else testId
                            let trimmedErr =
                                let f = firstLine errMsg
                                if f.Length > 200 then f.Substring(0, 197) + "..." else f
                            $"    {trimmedTest}\n      {trimmedErr}")
                    String.concat "\n" [
                        ""
                        $"Top bucket '{topId}' sample failures (first 5):"
                        yield! lines
                    ]
            | _ -> ""
        String.concat "\n" [
            $"Sprint: {sprintNum} | Pass rate: {passing}/{total} ({pct:F1}%%)"
            "Top failure buckets:"
            yield! bucketLines
            sampleBlock
        ]

    /// Multi-dimensional dashboard: shows pass/fail per LAYER, not per bucket.
    let dashboard (conn: SqliteConnection) : string =
        let sprintNum = currentSprintNum conn
        let (passing, total) = passRate conn
        let pct = if total > 0 then float passing / float total * 100.0 else 0.0
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT layer, SUM(passing), SUM(failing), SUM(crashing), SUM(total_tests), COUNT(*) FROM buckets GROUP BY layer ORDER BY layer"
        use reader = cmd.ExecuteReader()
        let layerLines = [
            while reader.Read() do
                let layer = reader.GetString(0)
                let p = reader.GetInt32(1)
                let f = reader.GetInt32(2)
                let c = reader.GetInt32(3)
                let t = reader.GetInt32(4)
                let nBuckets = reader.GetInt32(5)
                let lpct = if t > 0 then float p / float t * 100.0 else 0.0
                let crashNote = if c > 0 then $" ({c} crash)" else ""
                yield $"  {layer,-14} {p,5}/{t,-5} {lpct,5:F1}%%{crashNote}  ({nBuckets} buckets)" ]
        // Top 5 worst buckets for quick glance
        let worst = bucketsRanked conn |> List.truncate 5
        let worstLines = worst |> List.map (fun (id, _, failing, tot) -> $"  {id,-25} {failing}/{tot} failing")
        String.concat "\n" [
            $"Sprint: {sprintNum} | Overall: {passing}/{total} ({pct:F1}%%)"
            ""
            yield! layerLines
            ""
            "Top 5 failure buckets:"
            yield! worstLines
        ]

    /// Archive current DB as sprint N.
    let archiveAndReset (key: string) (sprintNum: int) =
        let current = currentDbPath key
        if File.Exists current then
            let archive = sprintDbPath key sprintNum
            File.Copy(current, archive, true)

    /// Read pass rates from all archived sprint DBs.
    /// Returns (sprintNum, overall_passing, overall_total, layer_stats) where layer_stats = (layer, passing, total) list.
    let trendData (key: string) : (int * int * int * (string * int * int) list) list =
        let dir = runtimeDir key
        if not (Directory.Exists dir) then []
        else
            Directory.GetFiles(dir, "sprint_*.db")
            |> Array.sort
            |> Array.choose (fun f ->
                try
                    let name = Path.GetFileNameWithoutExtension f
                    let num = int (name.Replace("sprint_", ""))
                    let conn = initSchema f
                    let (p, t) = passRate conn
                    // Read per-layer aggregates
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <- "SELECT layer, SUM(passing), SUM(total_tests) FROM buckets GROUP BY layer ORDER BY layer"
                    use reader = cmd.ExecuteReader()
                    let layers = [
                        while reader.Read() do
                            let layer = reader.GetString(0)
                            let lp = reader.GetInt32(1)
                            let lt = reader.GetInt32(2)
                            yield (layer, lp, lt) ]
                    conn.Close()
                    Some (num, p, t, layers)
                with _ -> None)
            |> Array.toList

    /// Render an ASCII progress chart with per-layer columns.
    let renderChart (data: (int * int * int * (string * int * int) list) list) (width: int) : string =
        if data.IsEmpty then "(no sprint data yet)"
        else
            // Collect all layer names across all sprints
            let allLayers = data |> List.collect (fun (_, _, _, ls) -> ls |> List.map (fun (l,_,_) -> l)) |> List.distinct |> List.sort
            let header =
                let layerCols = allLayers |> List.map (fun l -> sprintf "%-14s" l) |> String.concat " "
                sprintf "       %-*s  %%     %s" width "" layerCols
            let lines = data |> List.map (fun (s, p, t, layers) ->
                let pct = if t > 0 then float p / float t * 100.0 else 0.0
                let bars = int (pct / 100.0 * float width)
                let bar = String.replicate bars "█" + String.replicate (width - bars) "░"
                let layerCols = allLayers |> List.map (fun l ->
                    match layers |> List.tryFind (fun (ll,_,_) -> ll = l) with
                    | Some (_, lp, lt) -> sprintf "%4d/%-4d     " lp lt
                    | None -> sprintf "  -/  -       ") |> String.concat " "
                sprintf "S%3d %s %5.1f%% %s" s bar pct layerCols)
            String.concat "\n" (header :: lines)
        // Don't delete current — it becomes the baseline for next sprint
