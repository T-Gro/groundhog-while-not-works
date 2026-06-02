#!/usr/bin/env dotnet fsi
/// Sprint SQLite database — test tracking, bucket ranking, trend data.
#r "nuget: Microsoft.Data.Sqlite"

open System
open System.IO
open Microsoft.Data.Sqlite

module Db =
    let runtimeDir () =
        let d = Path.Combine(__SOURCE_DIRECTORY__, ".ralph-port", Path.GetFileName(Environment.CurrentDirectory.TrimEnd(Path.DirectorySeparatorChar)))
        Directory.CreateDirectory d |> ignore; d

    let dbFile () = Path.Combine(runtimeDir (), "current_results.db")

    let open' () =
        let p = dbFile ()
        Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
        let conn = new SqliteConnection($"Data Source={p}")
        conn.Open()
        let cmd = conn.CreateCommand()
        cmd.CommandText <- """
            CREATE TABLE IF NOT EXISTS sprint (sprint_num INTEGER, started_at TEXT DEFAULT (datetime('now')), finished_at TEXT, target_bucket TEXT, passing INTEGER DEFAULT 0, total_tests INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS buckets (id TEXT PRIMARY KEY, layer TEXT, total_tests INTEGER DEFAULT 0, passing INTEGER DEFAULT 0, failing INTEGER DEFAULT 0, crashing INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS tests (id TEXT PRIMARY KEY, bucket_id TEXT, status TEXT, error_message TEXT, error_category TEXT);"""
        cmd.ExecuteNonQuery() |> ignore; conn

    let sprintNum (conn: SqliteConnection) =
        let cmd = conn.CreateCommand() in cmd.CommandText <- "SELECT COALESCE(MAX(sprint_num),0) FROM sprint"
        cmd.ExecuteScalar() |> function :? int64 as v -> int v | :? int as v -> v | _ -> 0

    let passRate (conn: SqliteConnection) =
        let cmd = conn.CreateCommand() in cmd.CommandText <- "SELECT COALESCE(SUM(passing),0), COALESCE(SUM(total_tests),0) FROM buckets"
        use r = cmd.ExecuteReader() in if r.Read() then (r.GetInt32 0, r.GetInt32 1) else (0, 0)

    let failingBuckets (conn: SqliteConnection) =
        let cmd = conn.CreateCommand() in cmd.CommandText <- "SELECT id, total_tests - passing, total_tests FROM buckets WHERE total_tests - passing > 0 ORDER BY 2 DESC"
        use r = cmd.ExecuteReader() in [ while r.Read() do yield (r.GetString 0, r.GetInt32 1, r.GetInt32 2) ]

    let historicalHigh () =
        let conn = open' () in let cmd = conn.CreateCommand() in cmd.CommandText <- "SELECT COALESCE(MAX(total_tests),0) FROM sprint"
        let v = cmd.ExecuteScalar() |> function :? int64 as v -> int v | :? int as v -> v | _ -> 0
        conn.Close(); v

    let briefing (conn: SqliteConnection) =
        let (pp, pt) = passRate conn
        let buckets = failingBuckets conn
        let lines = [$"Overall: {pp}/{pt} ({if pt > 0 then 100*pp/pt else 0}%%)"]
        let blines = buckets |> List.truncate 10 |> List.map (fun (b,f,t) -> $"  {b}: {f}/{t} failing")
        conn.Close()
        String.concat "\n" (lines @ blines)

    let writeLog sprintNum text =
        let dir = Path.Combine(runtimeDir (), "sprint_logs") in Directory.CreateDirectory dir |> ignore
        let p = Path.Combine(dir, $"sprint_{sprintNum:D4}.log")
        File.WriteAllText(p, if text.Length > 200_000 then text.[text.Length-200_000..] else text)
