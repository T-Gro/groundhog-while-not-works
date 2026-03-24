#r "nuget: Spectre.Console"
#r "nuget: FSharp.SystemTextJson"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

/// Tracks and visualises progress across a large-scale porting campaign.
/// Produces both a Spectre.Console dashboard panel and a JSON report
/// suitable for charting test-passing improvement over time.

module PortingProgress =

    // ── Types ────────────────────────────────────────────────────────────────

    type ModuleStatus =
        | NotStarted
        | InProgress of iteration: int
        | Ported        // Code translated
        | TestsPassing  // Go tests passing
        | Reviewed      // Passed review verifier
        | Complete      // All verifiers green

    type ModuleProgress = {
        Name: string
        Status: ModuleStatus
        SourceTokens: int
        SourceFiles: int
        GoTestsPassing: int
        GoTestsTotal: int
        CoveragePercent: float option
        LastUpdated: DateTime
    }

    type Snapshot = {
        Timestamp: DateTime
        TotalModules: int
        ModulesComplete: int
        ModulesInProgress: int
        TotalTests: int
        TestsPassing: int
        AvgCoverage: float option
    }

    type PortingState = {
        ProjectName: string
        SourceDir: string
        GoModulePath: string
        Modules: ModuleProgress list
        History: Snapshot list    // Time-series for charting
        StartTime: DateTime
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────

    let createSnapshot (state: PortingState) : Snapshot =
        let complete = state.Modules |> List.filter (fun m -> m.Status = Complete) |> List.length
        let inProgress = state.Modules |> List.filter (fun m -> match m.Status with InProgress _ | Ported | TestsPassing | Reviewed -> true | _ -> false) |> List.length
        let totalTests = state.Modules |> List.sumBy (fun m -> m.GoTestsTotal)
        let passing = state.Modules |> List.sumBy (fun m -> m.GoTestsPassing)
        let coverages = state.Modules |> List.choose (fun m -> m.CoveragePercent)
        let avgCov = if coverages.IsEmpty then None else Some (coverages |> List.average)
        { Timestamp = DateTime.UtcNow; TotalModules = state.Modules.Length
          ModulesComplete = complete; ModulesInProgress = inProgress
          TotalTests = totalTests; TestsPassing = passing; AvgCoverage = avgCov }

    // ── Persistence ──────────────────────────────────────────────────────────

    let private reportDir = ".tools/ralph/porting"

    let private jsonOptions =
        let opts = JsonSerializerOptions(WriteIndented = true)
        opts.Converters.Add(JsonFSharpConverter())
        opts

    let saveState (state: PortingState) =
        Directory.CreateDirectory reportDir |> ignore
        let json = JsonSerializer.Serialize(state, jsonOptions)
        File.WriteAllText(Path.Combine(reportDir, "state.json"), json)

    let loadState () : PortingState option =
        let path = Path.Combine(reportDir, "state.json")
        if File.Exists path then
            try Some (JsonSerializer.Deserialize<PortingState>(File.ReadAllText path, jsonOptions))
            with _ -> None
        else None

    // ── Markdown report ──────────────────────────────────────────────────────

    let private statusEmoji = function
        | NotStarted     -> "⬜"
        | InProgress _   -> "🔄"
        | Ported         -> "📦"
        | TestsPassing   -> "✅"
        | Reviewed       -> "👀"
        | Complete       -> "🏁"

    let private statusLabel = function
        | NotStarted     -> "Not started"
        | InProgress i   -> $"In progress (iter {i})"
        | Ported         -> "Ported"
        | TestsPassing   -> "Tests passing"
        | Reviewed       -> "Reviewed"
        | Complete       -> "Complete"

    /// Simple ASCII bar chart of test-passing percentage over time.
    let private renderAsciiChart (history: Snapshot list) : string =
        if history.IsEmpty then "(no data yet)"
        else
            let width = 50
            history
            |> List.map (fun s ->
                let pct = if s.TotalTests > 0 then float s.TestsPassing / float s.TotalTests else 0.0
                let bars = int (pct * float width)
                let ts = s.Timestamp.ToString("MM-dd HH:mm")
                sprintf "%s |%s%s| %3.0f%%" ts (String.replicate bars "█") (String.replicate (width - bars) "░") (pct * 100.0))
            |> String.concat "\n"

    /// Generate a markdown progress report.
    let renderMarkdownReport (state: PortingState) : string =
        let snap = createSnapshot state
        let elapsed = DateTime.UtcNow - state.StartTime
        let pct = if snap.TotalModules > 0 then float snap.ModulesComplete / float snap.TotalModules * 100.0 else 0.0
        let testPct = if snap.TotalTests > 0 then float snap.TestsPassing / float snap.TotalTests * 100.0 else 0.0
        let covStr = snap.AvgCoverage |> Option.map (sprintf "%.1f%%") |> Option.defaultValue "n/a"

        let moduleRows =
            state.Modules
            |> List.map (fun m ->
                let cov = m.CoveragePercent |> Option.map (sprintf "%.0f%%") |> Option.defaultValue "-"
                $"| {statusEmoji m.Status} | {m.Name} | {statusLabel m.Status} | {m.GoTestsPassing}/{m.GoTestsTotal} | {cov} | {m.SourceTokens} |")
            |> String.concat "\n"

        let historyRows =
            state.History
            |> List.map (fun s ->
                let ts = s.Timestamp.ToString("yyyy-MM-dd HH:mm")
                let covVal = s.AvgCoverage |> Option.map (sprintf "%.1f%%") |> Option.defaultValue "-"
                $"| {ts} | {s.ModulesComplete}/{s.TotalModules} | {s.TestsPassing}/{s.TotalTests} | {covVal} |")
            |> String.concat "\n"

        $"""# Porting Progress: {state.ProjectName}

> **Source:** `{state.SourceDir}` → `{state.GoModulePath}`
> **Elapsed:** {elapsed.Days}d {elapsed.Hours}h {elapsed.Minutes}m
> **Modules:** {snap.ModulesComplete}/{snap.TotalModules} complete ({pct:F1}%%)
> **Tests:** {snap.TestsPassing}/{snap.TotalTests} passing ({testPct:F1}%%)
> **Coverage:** {covStr}

## Module Status

| | Module | Status | Tests | Cov | Tokens |
|---|--------|--------|-------|-----|--------|
{moduleRows}

## History

| Time | Modules | Tests | Coverage |
|------|---------|-------|----------|
{historyRows}

## Test-Passing Trend

```
{renderAsciiChart state.History}
```
"""

    /// Write the markdown report to disk.
    let writeReport (state: PortingState) =
        Directory.CreateDirectory reportDir |> ignore
        let md = renderMarkdownReport state
        File.WriteAllText(Path.Combine(reportDir, "PROGRESS.md"), md)

    // ── Spectre.Console panel ────────────────────────────────────────────────

    open Spectre.Console
    open Spectre.Console.Rendering

    /// Build a Spectre.Console renderable showing porting progress.
    let buildPortingPanel (state: PortingState) : IRenderable =
        let snap = createSnapshot state
        let pct = if snap.TotalModules > 0 then float snap.ModulesComplete / float snap.TotalModules * 100.0 else 0.0
        let testPct = if snap.TotalTests > 0 then float snap.TestsPassing / float snap.TotalTests * 100.0 else 0.0

        let table = Table()
        table.Border <- TableBorder.Rounded
        table.Title <- TableTitle($"Porting: {state.ProjectName}")
        table.AddColumn("") |> ignore
        table.AddColumn("Module") |> ignore
        table.AddColumn("Status") |> ignore
        table.AddColumn("Tests") |> ignore
        table.AddColumn("Cov") |> ignore

        for m in state.Modules do
            let icon = statusEmoji m.Status
            let statusStr =
                match m.Status with
                | NotStarted -> "[dim]Not started[/]"
                | InProgress i -> $"[yellow]Iter {i}[/]"
                | Ported -> "[blue]Ported[/]"
                | TestsPassing -> "[green]Tests ✓[/]"
                | Reviewed -> "[cyan]Reviewed[/]"
                | Complete -> "[green bold]Complete[/]"
            let testStr = $"{m.GoTestsPassing}/{m.GoTestsTotal}"
            let covStr = m.CoveragePercent |> Option.map (sprintf "%.0f%%") |> Option.defaultValue "-"
            table.AddRow(Markup(icon), Markup(Markup.Escape m.Name), Markup(statusStr), Markup(testStr), Markup(covStr)) |> ignore

        let header = Markup($"[bold]Modules:[/] {snap.ModulesComplete}/{snap.TotalModules} ({pct:F0}%%)  [bold]Tests:[/] {snap.TestsPassing}/{snap.TotalTests} ({testPct:F0}%%)")
        let rows = Rows([| header :> IRenderable; table :> IRenderable |])
        Panel(rows, Header = PanelHeader("Porting Progress"), Border = BoxBorder.Double) :> IRenderable
