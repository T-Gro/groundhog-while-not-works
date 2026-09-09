#!/usr/bin/env dotnet fsi
// NuGet references — must be at the top-level entry point for FSI
#r "nuget: Microsoft.Data.Sqlite"
#r "nuget: Fli"

#load "Infra.fsx"

open System
open Infra
open Db

// ─── THE LOOP (this is the whole thing) ─────────────────────────

/// Returns true if the loop should continue, false to stop.
let step sourceDir =
    let db    = Db.open' ()
    let sn    = Db.sprintNum db
    let next  = sn + 1

    printfn "  📊 Harvesting..."
    Harvest.run next

    let pp, pt = Db.passRate db
    let buckets = Db.failingBuckets db
    db.Close ()

    match Triage.classify pt buckets with
    | Bootstrap ->
        printfn $"S{next} | Bootstrap"
        Agent.run (Bootstrap.prompt sourceDir next) $"S{next}"
        Git.push () |> ignore
        true

    | FalseAllPass (n, high) ->
        printfn $"  🚨 FALSE ALL-PASS: {n} tests vs high {high}"
        Agent.run "BUILD REPAIR: fix all compile errors. Search for <<<<<<< in source." $"Fix-S{next}"
        true

    | AllPass ->
        printfn "✅ All pass!"
        false

    | Sprint ->
        let brief   = Db.briefing (Db.open' ())
        let listing = buckets |> List.map (fun (b,f,t) -> $"  {b}: {f}/{t}") |> String.concat "\n"
        let streak  = Streak.get ()
        let bucket  = Triage.pickBucket buckets streak
        printfn $"S{next} | {pp}/{pt} | {List.length buckets} failing | streak={streak}"

        let prompt  = Briefing.build sourceDir next brief listing streak bucket
        let bc      = Git.head ()

        Agent.run prompt $"S{next}"
        let log     = Agent.lastOutput ()
        Db.writeLog next log

        if Git.hasNewCommits bc then Streak.reset ()
        else Streak.bump () |> fun s -> printfn $"  ⚠ No commits (streak={s})"

        Harvest.run next

        let vOk, _   = Verify.runAll bc
        let fp, ft   = Db.passRate (Db.open' ())
        let delta    = fp - pp

        printfn $"  {fp}/{ft} d={delta}"
        if vOk && delta > 0 then
            Agent.run $"Sprint {next} done. Capture learnings." $"Learn-S{next}"
            Git.push () |> ignore
            printfn $"  ✅ OK"
        else printfn $"  ❌ Fail"
        true

let run sourceDir =
    printfn $"═══ ralph-port: {sourceDir} ═══"
    let mutable go, fails = true, 0
    Console.CancelKeyPress.Add (fun a -> a.Cancel <- true; go <- false)
    while go do
        try
            if step sourceDir |> not then go <- false
            else fails <- 0
        with ex ->
            fails <- fails + 1
            eprintfn $"  Error ({fails}): {ex.Message}"
            if fails >= 5 then Threading.Thread.Sleep(10*60*1000); fails <- 0

// ─── CLI ────────────────────────────────────────────────────────

match fsi.CommandLineArgs |> Array.toList |> List.tail with
| "status" :: _        -> Status.show ()
| "watch"  :: rest     -> Status.watch (rest |> List.tryHead |> Option.map int |> Option.defaultValue 30)
| [sourceDir]          -> run sourceDir
| sourceDir :: _       -> run sourceDir
| _ ->
    printfn "ralph-port — autonomous porting convergence loop"
    printfn ""
    printfn "  dotnet fsi ralph-port.fsx <source-dir>   Run the loop"
    printfn "  dotnet fsi ralph-port.fsx status          Current state"
    printfn "  dotnet fsi ralph-port.fsx watch [sec]     Live dashboard"
