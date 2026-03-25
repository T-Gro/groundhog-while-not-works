#!/usr/bin/env dotnet fsi

/// ContextBuilder — Builds compact, focused context for subagent prompts.
///
/// Language-agnostic. Reads project.json for source/target paths.
///
/// Each subagent should receive ONLY what it needs:
///   1. Interface contracts for adjacent layers (from hints/)
///   2. Source code to port (scoped to sprint)
///   3. Current failing test output
///   4. Pattern/translation guide (from hints/)
///
/// NOT: the entire codebase, full history, all type mappings.

#load "ProjectConfig.fsx"

open System
open System.IO
open ProjectConfig.ProjectConfig

module ContextBuilder =

    /// Read a hints file if it exists, return empty string otherwise.
    let private readHint (name: string) : string =
        let path = Path.Combine(__SOURCE_DIRECTORY__, "hints", name)
        if File.Exists path then File.ReadAllText path
        else ""

    /// Read layer boundary contracts for adjacent layers only.
    let getAdjacentContracts (config: ProjectConfig) (layerId: string) : string =
        let content = readHint "layer-boundaries.md"
        if String.IsNullOrWhiteSpace content then
            "(No layer-boundaries.md found in hints/. Run init or create manually.)"
        else
            $"<!-- Layer contracts (adjacent to {layerId}) -->\n{content}"

    /// Read type/structure translation patterns.
    let getTranslationPatterns () : string =
        let content = readHint "type-patterns.md"
        if String.IsNullOrWhiteSpace content then
            "(No type-patterns.md found in hints/. Generate during init or create manually.)"
        else content

    /// Read architecture overview.
    let getArchitectureOverview () : string =
        let content = readHint "architecture.md"
        if String.IsNullOrWhiteSpace content then
            "(No architecture.md found in hints/. Generate during init or create manually.)"
        else content

    /// Extract source files for a specific scope, respecting token budget.
    let extractSourceFiles (sourceDir: string) (filePaths: string list) (maxTokens: int) : string =
        let mutable totalChars = 0
        let mutable result = System.Text.StringBuilder()
        let charBudget = maxTokens * 4  // ~4 chars per token

        for relPath in filePaths do
            let fullPath = Path.Combine(sourceDir, relPath)
            if File.Exists fullPath then
                let content = File.ReadAllText fullPath
                if totalChars + content.Length <= charBudget then
                    result.AppendLine $"// ═══ {relPath} ({content.Length / 4} est. tokens) ═══" |> ignore
                    result.AppendLine content |> ignore
                    totalChars <- totalChars + content.Length
                else
                    result.AppendLine $"// ═══ {relPath} (TRUNCATED — budget exceeded) ═══" |> ignore
                    let remaining = charBudget - totalChars
                    if remaining > 200 then
                        result.AppendLine (content.Substring(0, min remaining content.Length)) |> ignore
                    totalChars <- charBudget

        result.ToString()

    /// Format failing test output compactly.
    let formatFailures (failures: (string * string) list) (maxCount: int) : string =
        let selected = failures |> List.truncate maxCount
        let lines =
            selected |> List.map (fun (sample, error) -> $"• {sample}: {error}")
        String.concat "\n" [
            $"### Failing tests ({failures.Length} total, showing {selected.Length}):"
            yield! lines
            if failures.Length > maxCount then
                $"... and {failures.Length - maxCount} more"
        ]

    /// Read the porting plan (if one was seeded during init).
    let getPlan () : string =
        let content = readHint "porting-plan.md"
        if String.IsNullOrWhiteSpace content then ""
        else $"## Porting Plan\n{content}"

    /// Build the complete prompt context for a convergence sprint.
    let buildSprintContext
        (config: ProjectConfig)
        (layerId: string)
        (featureName: string)
        (sourceFiles: string list)
        (failures: (string * string) list)
        (previousPassingPct: float)
        (targetDeltaPct: float)
        : string =

        let contracts = getAdjacentContracts config layerId
        let patterns = getTranslationPatterns ()
        let plan = getPlan ()
        let source = extractSourceFiles config.SourceDir sourceFiles 30_000
        let failureText = formatFailures failures 20

        // Include plan summary only if it fits budget — truncate to first 3K tokens
        let planSection =
            if String.IsNullOrWhiteSpace plan then ""
            else
                let maxPlanChars = 12_000  // ~3K tokens
                if plan.Length <= maxPlanChars then plan
                else plan.Substring(0, maxPlanChars) + "\n\n_(plan truncated for context budget)_"

        String.concat "\n\n" [
            $"# Convergence Sprint: Port {featureName} ({layerId})"
            $"**Source language**: {config.SourceLang}  |  **Target language**: {config.TargetLang}"
            $"**Previous passing**: {previousPassingPct:F1}%%  |  **Target delta**: +{targetDeltaPct:F1}%%"
            ""
            if not (String.IsNullOrWhiteSpace planSection) then planSection
            ""
            "## Architecture Context"
            contracts
            ""
            "## Source to Port"
            source
            ""
            "## Current Failures"
            failureText
            ""
            "## Translation Patterns"
            patterns
        ]

    /// Estimate token count.
    let estimateTokens (text: string) : int = max 1 (text.Length / 4)

    /// Print a context budget report.
    let reportBudget (context: string) =
        let tokens = estimateTokens context
        printfn $"Context budget: {tokens} tokens ({float tokens / 80_000.0 * 100.0:F0}%% of 80K)"
        if tokens > 80_000 then
            printfn $"  ⚠ OVER BUDGET by {tokens - 80_000} tokens — reduce source scope"
