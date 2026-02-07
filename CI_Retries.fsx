// CI_Retries.fsx - CI monitoring and fixup logic
// Loaded by Ralph.fsx after GUI.fsx
// IMPORTANT: No #load here - Ralph.fsx loads all dependencies

#r "nuget: Fli"

open System
open System.Threading
open Fli
open TypeDefinitions

/// CI monitoring types and operations
module CIMonitor =
    type BuildStatus = Pending | Success | Failed of failures: string list
    
    /// Run git push command
    let runGitPush () =
        try
            let result = cli { Exec "git"; Arguments [| "push" |] } |> Command.execute
            if result.ExitCode = 0 then Ok (result.Text |> Option.defaultValue "")
            else Error (result.Error |> Option.defaultValue "" |> fun e -> e + (result.Text |> Option.defaultValue ""))
        with ex -> Error ex.Message
    
    /// Build the failure extraction prompt
    let buildFailureExtractionPrompt (buildOutput: string) =
        [
            "--- CI FAILURE ANALYZER ---"
            ""
            "Your job is to extract UNIQUE failures from CI build output."
            "This CI has many dimensions (OS, config, product switches) so same failure may appear multiple times."
            ""
            "ANALYZE THIS OUTPUT AND LIST:"
            "1. UNIQUE build failures (don't repeat the same error)"
            "2. UNIQUE test failures (don't repeat the same failing test)"
            ""
            "Format your response as:"
            "BUILD_FAILURES:"
            "- error 1"
            "- error 2"
            ""
            "TEST_FAILURES:"
            "- TestName1"
            "- TestName2"
            ""
            "If no failures, output: NO_FAILURES"
            ""
            "BUILD OUTPUT:"
            buildOutput
        ] |> String.concat "\n"
    
    /// Parse failures from agent response
    let parseFailuresFromResponse (result: string) : string list =
        if result.Contains("NO_FAILURES") then []
        else 
            result.Split('\n') 
            |> Array.filter (fun l -> l.StartsWith("- "))
            |> Array.map (fun l -> l.Substring(2).Trim()) 
            |> Array.toList
    
    /// Check build status via gh CLI - returns (quick result option, raw output for LLM)
    let checkBuildStatusRaw () =
        try
            let result = cli { Exec "gh"; Arguments [| "pr"; "checks"; "--fail-fast" |] } |> Command.execute
            let output = result.Text |> Option.defaultValue ""
            let errors = result.Error |> Option.defaultValue ""
            let combined = output + "\n" + errors
            
            if output.Contains("pass") && not (output.Contains("fail")) then
                Some Success, combined
            elif output.Contains("pending") || output.Contains("in_progress") then
                Some Pending, combined
            else
                None, combined  // Need LLM analysis
        with _ ->
            Some Pending, ""  // If we can't check, assume pending
    
    /// Build the fixup prompt for CI failures
    let buildFixupPrompt (failures: string list) =
        let failureLines = failures |> List.map (fun f -> "- " + f)
        [
            "--- CI FIXUP AGENT ---"
            ""
            "CI has failed with the following UNIQUE issues:"
        ] @ failureLines @ [
            ""
            "Your job is to fix these CI failures."
            ""
            "1. Read .tools/ralph/BACKLOG.md to understand the feature"
            "2. Investigate each failure"
            "3. Make targeted fixes"
            "4. Commit and push the fixes"
            ""
            "Focus only on fixing CI failures, not redesigning."
        ] |> String.concat "\n"
    
    /// Extract failures using LLM - takes runAgent as callback
    let extractFailuresWithLLM (runAgent: string -> string -> bool -> Async<string>) (buildOutput: string) showWin = async {
        let prompt = buildFailureExtractionPrompt buildOutput
        let! result = runAgent prompt "CI-FailureAnalyzer" showWin
        return parseFailuresFromResponse result
    }
    
    /// Check build status - takes runAgent for LLM fallback
    let checkBuildStatus (runAgent: string -> string -> bool -> Async<string>) showWin = async {
        let quickResult, rawOutput = checkBuildStatusRaw()
        match quickResult with
        | Some status -> return status
        | None ->
            let! failures = extractFailuresWithLLM runAgent rawOutput showWin
            return Failed failures
    }
    
    /// Monitor CI with polling - takes callbacks for state updates
    let monitorCI (runAgent: string -> string -> bool -> Async<string>) (setMessage: string -> unit) showWin maxWaitMinutes = async {
        let mutable status = Pending
        let mutable waited = 0
        let intervalMinutes = 30
        
        setMessage "Starting CI monitoring..."
        
        while status = Pending && waited < maxWaitMinutes do
            let! s = checkBuildStatus runAgent showWin
            status <- s
            
            match status with
            | Success ->
                setMessage "[green]✓ CI passed![/]"
            | Failed failures ->
                setMessage $"[red]✗ CI failed with {failures.Length} unique failures[/]"
            | Pending ->
                setMessage $"[yellow]CI pending... waiting {intervalMinutes}min (total: {waited}min)[/]"
                Thread.Sleep(intervalMinutes * 60 * 1000)
                waited <- waited + intervalMinutes
        
        return status
    }
    
    /// Run the full CI fixup loop - takes callbacks
    let runCIFixupLoop (runAgent: string -> string -> bool -> Async<string>) (setMessage: string -> unit) request showWin = async {
        let mutable status = Pending
        let mutable iteration = 0
        let maxIterations = 5
        
        while not (status = Success) && iteration < maxIterations do
            iteration <- iteration + 1
            setMessage $"CI Fixup iteration {iteration}/{maxIterations}"
            
            let! s = monitorCI runAgent setMessage showWin 180
            status <- s
            
            match status with
            | Success -> ()
            | Pending -> 
                setMessage "[yellow]CI still pending after max wait[/]"
            | Failed failures ->
                if iteration < maxIterations then
                    let fixupPrompt = buildFixupPrompt failures
                    let! _ = runAgent fixupPrompt "CI-Fixup" showWin
                    
                    match runGitPush() with
                    | Ok _ -> setMessage "[green]Pushed fixes[/]"
                    | Error e -> setMessage $"[red]Push failed[/]"
        
        return status
    }
