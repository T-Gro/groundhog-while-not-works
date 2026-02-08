// CI_Retries.fsx - CI monitoring (simplified)
// Loaded by Ralph.fsx after GUI.fsx
// IMPORTANT: No #load here - Ralph.fsx loads all dependencies
//
// Design: CI failures restart the main run() loop with augmented context.
// This module only handles push and polling - no separate CI fixup agents.

#r "nuget: Fli"

open System
open System.Threading
open Fli

/// CI monitoring - push and poll only, failures handled by restarting main loop
module CIMonitor =
    type CIResult = 
        | Success 
        | Pending 
        | Failed of rawOutput: string  // Raw CI output for context augmentation
    
    /// Run git push command
    let runGitPush () =
        try
            let result = cli { Exec "git"; Arguments [| "push" |] } |> Command.execute
            if result.ExitCode = 0 then Ok (result.Text |> Option.defaultValue "")
            else Error (result.Error |> Option.defaultValue "" |> fun e -> e + (result.Text |> Option.defaultValue ""))
        with ex -> Error ex.Message
    
    /// Check CI status via gh CLI - returns status and raw output
    let checkCI () : CIResult =
        try
            let result = cli { Exec "gh"; Arguments [| "pr"; "checks"; "--fail-fast" |] } |> Command.execute
            let output = result.Text |> Option.defaultValue ""
            let errors = result.Error |> Option.defaultValue ""
            let combined = (output + "\n" + errors).Trim()
            
            if output.Contains("pass") && not (output.Contains("fail")) then
                Success
            elif output.Contains("pending") || output.Contains("in_progress") then
                Pending
            else
                Failed combined
        with _ ->
            Pending  // If we can't check, assume pending
    
    /// Get detailed CI failure logs (for context augmentation)
    let getCIFailureLogs () : string =
        try
            // Try to get more detailed failure info
            let result = cli { Exec "gh"; Arguments [| "run"; "view"; "--log-failed" |] } |> Command.execute
            result.Text |> Option.defaultValue ""
        with _ -> ""
    
    /// Poll CI until complete or timeout - returns final status
    let pollCI (setMessage: string -> unit) maxWaitMinutes = async {
        let mutable status = Pending
        let mutable waited = 0
        let intervalMinutes = 2  // Poll every 2 minutes
        
        setMessage "Monitoring CI..."
        
        while status = Pending && waited < maxWaitMinutes do
            status <- checkCI()
            
            match status with
            | Success ->
                setMessage "[green]✓ CI passed![/]"
            | Failed _ ->
                setMessage "[red]✗ CI failed[/]"
            | Pending ->
                setMessage $"[yellow]CI pending... ({waited}/{maxWaitMinutes} min)[/]"
                do! Async.Sleep(intervalMinutes * 60 * 1000)
                waited <- waited + intervalMinutes
        
        // If failed, try to get detailed logs
        match status with
        | Failed basicOutput ->
            let detailedLogs = getCIFailureLogs()
            let combined = 
                if String.IsNullOrEmpty detailedLogs then basicOutput
                else $"{basicOutput}\n\nDETAILED FAILURE LOGS:\n{detailedLogs}"
            return Failed combined
        | other -> return other
    }
