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

    let private dbDir () = Path.Combine(__SOURCE_DIRECTORY__, "testdata")

    /// Path to the current (latest) sprint DB.
    let currentDbPath () = Path.Combine(dbDir(), "current_results.db")

    /// Archive path for a specific sprint.
    let sprintDbPath (sprintNum: int) = Path.Combine(dbDir(), $"sprint_{sprintNum:D4}.db")

    /// Initialize the schema in a new or existing DB.
    let initSchema (dbPath: string) =
        Directory.CreateDirectory(Path.GetDirectoryName dbPath) |> ignore
        use conn = new SqliteConnection($"Data Source={dbPath}")
        conn.Open()
        use cmd = conn.CreateCommand()
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
                failing = (SELECT COUNT(*) FROM tests WHERE bucket_id = buckets.id AND status = 'fail'),
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

    /// Get buckets sorted by most failing (highest-impact first).
    let bucketsRanked (conn: SqliteConnection) : (string * string * int * int) list =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, layer, failing, total_tests FROM buckets WHERE failing > 0 ORDER BY failing DESC"
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
        String.concat "\n" [
            $"Sprint: {sprintNum} | Pass rate: {passing}/{total} ({pct:F1}%)"
            "Top failure buckets:"
            yield! bucketLines
        ]

    /// Archive current DB as sprint N and start fresh.
    let archiveAndReset (sprintNum: int) =
        let current = currentDbPath()
        if File.Exists current then
            let archive = sprintDbPath sprintNum
            File.Copy(current, archive, true)
        // Don't delete current — it becomes the baseline for next sprint
