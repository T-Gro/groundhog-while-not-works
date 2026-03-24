open System
open System.IO
open System.Text.RegularExpressions

/// Module analysis and sprint splitting for large-scale TypeScript→Go porting.
/// Reads a TS source tree, groups files into context-budget-respecting chunks,
/// computes a topological porting order, and generates sprint markdown files.

module PortingSplit =

    // ── Types ────────────────────────────────────────────────────────────────

    /// One source file with its estimated token cost.
    type SourceFile = {
        RelPath: string     // Relative to source root, e.g. "src/checker/types.ts"
        Lines: int
        Chars: int
        EstTokens: int      // ~4 chars per token heuristic
    }

    /// A logical module (directory or explicit grouping).
    type Module = {
        Name: string
        Dir: string         // Relative directory path
        Files: SourceFile list
        TotalTokens: int
        Imports: string list // Other module names this depends on
    }

    /// A sprint chunk – one or more modules sized for a single LLM context window.
    type SprintChunk = {
        Order: int
        Name: string
        Modules: Module list
        TotalTokens: int
        DependsOn: string list  // Names of earlier chunks that must be ported first
    }

    // ── Configuration ────────────────────────────────────────────────────────

    /// Maximum tokens per sprint (leave room for prompt, instructions, and output).
    /// A 200k context model can fit ~100k source tokens plus overhead.
    let defaultMaxTokensPerSprint = 60_000

    /// Estimate tokens from character count (~4 chars/token for code).
    let estimateTokens (chars: int) = max 1 (chars / 4)

    // ── Source scanning ──────────────────────────────────────────────────────

    /// Scan a directory tree for TypeScript files and measure each one.
    let scanSourceFiles (rootDir: string) : SourceFile list =
        if not (Directory.Exists rootDir) then []
        else
            Directory.EnumerateFiles(rootDir, "*.ts", SearchOption.AllDirectories)
            |> Seq.filter (fun f -> not (f.Contains "node_modules") && not (f.EndsWith ".d.ts"))
            |> Seq.map (fun f ->
                let rel = Path.GetRelativePath(rootDir, f)
                let content = File.ReadAllText f
                let lines = content.Split('\n').Length
                { RelPath = rel; Lines = lines; Chars = content.Length; EstTokens = estimateTokens content.Length })
            |> Seq.toList

    // ── Import extraction ────────────────────────────────────────────────────

    /// Very simple import extractor – captures `from './xxx'` or `from '../xxx'` style imports.
    let private importPattern = Regex(@"(?:import|from)\s+['""]\.\.?/([^'""]+)['""]", RegexOptions.Compiled)

    let extractImportedModules (rootDir: string) (file: SourceFile) : string list =
        let fullPath = Path.Combine(rootDir, file.RelPath)
        if not (File.Exists fullPath) then []
        else
            let content = File.ReadAllText fullPath
            importPattern.Matches(content)
            |> Seq.cast<Match>
            |> Seq.map (fun m ->
                let raw = m.Groups.[1].Value
                // Normalise to directory-level module name
                let parts = raw.Replace("\\", "/").Split('/')
                if parts.Length >= 2 then parts.[0]
                else Path.GetDirectoryName(file.RelPath).Replace("\\", "/").Split('/') |> Array.tryHead |> Option.defaultValue raw)
            |> Seq.distinct
            |> Seq.toList

    // ── Module grouping ──────────────────────────────────────────────────────

    /// Group source files into modules by their top-level directory.
    let groupIntoModules (rootDir: string) (files: SourceFile list) : Module list =
        files
        |> List.groupBy (fun f ->
            let parts = f.RelPath.Replace("\\", "/").Split('/')
            if parts.Length >= 2 then parts.[0] else "(root)")
        |> List.map (fun (dir, files) ->
            let imports =
                files
                |> List.collect (extractImportedModules rootDir)
                |> List.distinct
                |> List.filter (fun d -> d <> dir)
            { Name = dir; Dir = dir; Files = files
              TotalTokens = files |> List.sumBy (fun f -> f.EstTokens)
              Imports = imports })
        |> List.sortBy (fun m -> m.TotalTokens)

    // ── Topological sort ─────────────────────────────────────────────────────

    /// Kahn's algorithm – returns modules in dependency-first order.
    let topologicalSort (modules: Module list) : Module list =
        let nameSet = modules |> List.map (fun m -> m.Name) |> Set.ofList
        let inDeg = System.Collections.Generic.Dictionary<string, int>()
        let adj = System.Collections.Generic.Dictionary<string, ResizeArray<string>>()
        for m in modules do
            if not (inDeg.ContainsKey m.Name) then inDeg.[m.Name] <- 0
            if not (adj.ContainsKey m.Name) then adj.[m.Name] <- ResizeArray()
        for m in modules do
            for dep in m.Imports do
                if nameSet.Contains dep then
                    inDeg.[m.Name] <- inDeg.[m.Name] + 1
                    if not (adj.ContainsKey dep) then adj.[dep] <- ResizeArray()
                    adj.[dep].Add(m.Name)
        let queue = System.Collections.Generic.Queue<string>()
        for kv in inDeg do if kv.Value = 0 then queue.Enqueue kv.Key
        let result = ResizeArray()
        while queue.Count > 0 do
            let cur = queue.Dequeue()
            result.Add cur
            if adj.ContainsKey cur then
                for next in adj.[cur] do
                    inDeg.[next] <- inDeg.[next] - 1
                    if inDeg.[next] = 0 then queue.Enqueue next
        // Append any remaining (cycles) at the end
        let ordered = Set.ofSeq result
        for m in modules do
            if not (ordered.Contains m.Name) then result.Add m.Name
        let lookup = modules |> List.map (fun m -> m.Name, m) |> Map.ofList
        result |> Seq.choose (fun n -> Map.tryFind n lookup) |> Seq.toList

    // ── Chunking ─────────────────────────────────────────────────────────────

    /// Pack modules into sprint-sized chunks respecting context budget.
    /// Each chunk is as large as possible without exceeding maxTokens.
    let chunkModules (maxTokens: int) (sortedModules: Module list) : SprintChunk list =
        let mutable chunks = []
        let mutable current = []
        let mutable currentTokens = 0
        let mutable order = 1
        let mutable completedModules = Set.empty
        
        for m in sortedModules do
            if m.TotalTokens > maxTokens then
                // Module too large for a single chunk – split its files
                if current <> [] then
                    let deps = current |> List.collect (fun (mm: Module) -> mm.Imports) |> List.distinct |> List.filter (fun d -> completedModules.Contains d)
                    chunks <- { Order = order; Name = current |> List.map (fun mm -> mm.Name) |> String.concat "+"; Modules = List.rev current; TotalTokens = currentTokens; DependsOn = deps } :: chunks
                    for mm in current do completedModules <- completedModules.Add mm.Name
                    order <- order + 1
                    current <- []
                    currentTokens <- 0
                // Split large module into sub-chunks by file groups
                let mutable subFiles = []
                let mutable subTokens = 0
                for f in m.Files do
                    if subTokens + f.EstTokens > maxTokens && subFiles <> [] then
                        let subMod = { m with Files = List.rev subFiles; TotalTokens = subTokens; Name = $"{m.Name}-part{order}" }
                        let deps = m.Imports |> List.filter (fun d -> completedModules.Contains d)
                        chunks <- { Order = order; Name = subMod.Name; Modules = [subMod]; TotalTokens = subTokens; DependsOn = deps } :: chunks
                        completedModules <- completedModules.Add subMod.Name
                        order <- order + 1
                        subFiles <- []
                        subTokens <- 0
                    subFiles <- f :: subFiles
                    subTokens <- subTokens + f.EstTokens
                if subFiles <> [] then
                    let subMod = { m with Files = List.rev subFiles; TotalTokens = subTokens; Name = $"{m.Name}-part{order}" }
                    let deps = m.Imports |> List.filter (fun d -> completedModules.Contains d)
                    chunks <- { Order = order; Name = subMod.Name; Modules = [subMod]; TotalTokens = subTokens; DependsOn = deps } :: chunks
                    completedModules <- completedModules.Add subMod.Name
                    order <- order + 1
            elif currentTokens + m.TotalTokens > maxTokens then
                // Flush current chunk
                let deps = current |> List.collect (fun mm -> mm.Imports) |> List.distinct |> List.filter (fun d -> completedModules.Contains d)
                chunks <- { Order = order; Name = current |> List.map (fun mm -> mm.Name) |> String.concat "+"; Modules = List.rev current; TotalTokens = currentTokens; DependsOn = deps } :: chunks
                for mm in current do completedModules <- completedModules.Add mm.Name
                order <- order + 1
                current <- [m]
                currentTokens <- m.TotalTokens
            else
                current <- m :: current
                currentTokens <- currentTokens + m.TotalTokens
        // Flush remaining
        if current <> [] then
            let deps = current |> List.collect (fun mm -> mm.Imports) |> List.distinct |> List.filter (fun d -> completedModules.Contains d)
            chunks <- { Order = order; Name = current |> List.map (fun mm -> mm.Name) |> String.concat "+"; Modules = List.rev current; TotalTokens = currentTokens; DependsOn = deps } :: chunks
        chunks |> List.rev

    // ── Sprint generation ────────────────────────────────────────────────────

    /// Render a SprintChunk as a markdown sprint file (matching Ralph's template format).
    let renderSprintMarkdown (goModulePath: string) (chunk: SprintChunk) : string =
        let filesSection =
            chunk.Modules
            |> List.collect (fun m ->
                m.Files |> List.map (fun f -> $"- `{f.RelPath}` ({f.Lines} lines, ~{f.EstTokens} tokens) → `{goModulePath}/{m.Dir}/{Path.GetFileNameWithoutExtension(f.RelPath)}.go`"))
            |> String.concat "\n"

        let depsNote =
            if chunk.DependsOn.IsEmpty then "None – this chunk can be ported independently."
            else chunk.DependsOn |> List.map (fun d -> $"- {d}") |> String.concat "\n" |> sprintf "These chunks must be ported first:\n%s"

        $"""---
---
# Sprint: Port {chunk.Name} to Go

## Context
Port the TypeScript module(s) in `{chunk.Name}` to idiomatic Go.
Estimated context budget: ~{chunk.TotalTokens} tokens of source ({chunk.Modules |> List.sumBy (fun m -> m.Files.Length)} files).

### Dependencies
{depsNote}

## Description
Translate the following TypeScript source files to idiomatic Go, preserving behavior exactly.
Target Go package path: `{goModulePath}/{chunk.Name |> fun n -> n.Replace("-", "_").ToLowerInvariant()}`

### Source Files to Port
{filesSection}

### Implementation Steps
1. Create Go package directory and `package` declaration
2. Define Go types matching the TypeScript interfaces/types (use structs + interfaces)
3. Translate each exported function, preserving signatures where possible
4. Translate each non-exported helper
5. Add Go doc comments matching the original TSDoc/JSDoc
6. Write table-driven tests in `_test.go` mirroring any existing `.test.ts` or `.spec.ts` files

### Patterns to Follow
- Use `error` returns instead of exceptions
- Use `context.Context` for cancellation where the TS code uses async/await
- Prefer value receivers on small structs, pointer receivers on large ones
- Keep the same module/package boundaries as the TS code

### What to Avoid
- Do NOT use `interface{{}}` / `any` unless the TS code is truly `unknown`
- Do NOT add dependencies beyond the Go standard library unless absolutely necessary
- Do NOT stub out functions — every function must be fully implemented

### Expected Behavior
All existing TypeScript tests, when translated to Go table-driven tests, should pass.

## Definition of Done
- `go build ./...` succeeds with no errors
- `go vet ./...` reports no issues
- `go test ./...` passes for this package
- Every exported TS function has a corresponding Go function
- Every existing TS test has a corresponding Go test
- No `TODO` or `FIXME` placeholders remain
"""

    /// Write sprint files to disk in Ralph's expected format.
    let writeSprintFiles (sprintsDir: string) (goModulePath: string) (chunks: SprintChunk list) =
        Directory.CreateDirectory sprintsDir |> ignore
        chunks |> List.iter (fun chunk ->
            let safeName = chunk.Name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_")
            let fileName = sprintf "%02d_%s.md" chunk.Order safeName
            let filePath = Path.Combine(sprintsDir, fileName)
            File.WriteAllText(filePath, renderSprintMarkdown goModulePath chunk))

    // ── Public API ───────────────────────────────────────────────────────────

    /// Full pipeline: scan → group → sort → chunk → write sprints.
    /// Returns the list of chunks and a summary string.
    let analyzeAndSplit (sourceDir: string) (sprintsDir: string) (goModulePath: string) (maxTokens: int option) : SprintChunk list * string =
        let budget = maxTokens |> Option.defaultValue defaultMaxTokensPerSprint
        let files = scanSourceFiles sourceDir
        let modules = groupIntoModules sourceDir files
        let sorted = topologicalSort modules
        let chunks = chunkModules budget sorted
        writeSprintFiles sprintsDir goModulePath chunks
        let summary =
            [ $"Source: {sourceDir}"
              $"Files scanned: {files.Length}"
              $"Modules found: {modules.Length}"
              $"Sprints created: {chunks.Length}"
              $"Token budget/sprint: {budget}"
              $"Total source tokens: {files |> List.sumBy (fun f -> f.EstTokens)}"
              ""
              "Sprint breakdown:"
              yield! chunks |> List.map (fun c ->
                $"  {c.Order}. {c.Name} — {c.TotalTokens} tokens, {c.Modules |> List.sumBy (fun m -> m.Files.Length)} files") ]
            |> String.concat "\n"
        (chunks, summary)
