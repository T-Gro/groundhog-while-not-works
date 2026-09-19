#load "ProcessContainment.fsx"

open System
open System.IO
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Utils
open TypeDefinitions
open Prompting

type ResumeCursor =
    { SprintPath: string
      Iteration: int
      Stage: string }

module Resume =
    type SavedResult =
        { Name: string
          Passed: bool
          Summary: string }

    type SavedIteration =
        { Iteration: int
          Output: string
          Results: SavedResult array }

    type SavedSprint =
        { Path: string
          Hash: string
          Status: string
          Iteration: int
          History: SavedIteration array
          Reasons: string array
          Summary: string
          Targets: string array
          Eliminated: string array }

    type Checkpoint =
        { Version: int
          WorkDir: string
          Request: string
          BacklogHash: string
          Sprints: SavedSprint array
          Cursor: ResumeCursor option
          Phase: string
          StartTime: DateTime
          CompletedIterations: int
          ArbiterAttempt: int
          FinalResults: SavedResult array }

    let private hash path =
        File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString

    let private backlogHash () =
        if File.Exists Config.backlogFile then
            hash Config.backlogFile
        else
            ""

    let private results values =
        values
        |> List.map (fun (name, passed, summary) ->
            { Name = name
              Passed = passed
              Summary = summary })
        |> List.toArray

    let save path request (state: State) cursor =
        let sprints =
            state.Backlog
            |> List.map (fun (item, status, timing) ->
                let status, iteration =
                    match status with
                    | Todo -> "Todo", 0
                    | Running(_, n) -> "Running", n
                    | Done n -> "Done", n

                { Path = item.FilePath
                  Hash = hash item.FilePath
                  Status = status
                  Iteration = iteration
                  History =
                    timing.IterationHistory
                    |> List.map (fun h ->
                        { Iteration = h.Iteration
                          Output = h.AgentOutput
                          Results = results h.VerifierResults })
                    |> List.toArray
                  Reasons = timing.IterationReasons |> List.map snd |> List.toArray
                  Summary = timing.Summary |> Option.defaultValue ""
                  Targets = item.TargetVerifiers |> Option.map List.toArray |> Option.toObj
                  Eliminated = Set.toArray item.EliminatedVerifiers })
            |> List.toArray

        let checkpoint =
            { Version = 1
              WorkDir = Path.GetFullPath Config.workDir
              Request = request
              BacklogHash = backlogHash ()
              Sprints = sprints
              Cursor = cursor
              Phase = state.CurrentPhase
              StartTime = state.StartTime
              CompletedIterations = state.CompletedIterations
              ArbiterAttempt = state.ArbiterAttempt
              FinalResults =
                state.FinalVerifierResults
                |> Map.toList
                |> List.choose (fun (name, status) ->
                    match status with
                    | Passed _
                    | Failed _ ->
                        Some(
                            name,
                            (match status with
                             | Passed _ -> true
                             | _ -> false),
                            state.FinalVerifierSummaries |> Map.tryFind name |> Option.defaultValue ""
                        )
                    | _ -> None)
                |> results }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path))
        |> ignore

        let pending = path + ".new"

        use stream =
            new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None)

        JsonSerializer.Serialize(stream, checkpoint)
        stream.Flush(true)
        stream.Close()
        File.Move(pending, path, true)

    let load path request =
        let checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText path)

        if
            checkpoint.Version <> 1
            || checkpoint.WorkDir <> Path.GetFullPath Config.workDir
            || checkpoint.Request <> request
        then
            failwith "Resume checkpoint version, workspace, or request mismatch"

        if checkpoint.BacklogHash <> backlogHash () then
            failwith "Backlog changed since checkpoint"

        let paths = checkpoint.Sprints |> Array.map (fun s -> s.Path)

        if paths.Length = 0 || (paths |> Array.distinct).Length <> paths.Length then
            failwith "Invalid checkpoint sprint list"

        let actual = SprintFiles.listSprints () |> Set.ofList

        if Set.ofArray paths <> actual then
            failwith "Sprint files differ from checkpoint; use explicit replanning"

        let backlog =
            checkpoint.Sprints
            |> Array.map (fun saved ->
                if hash saved.Path <> saved.Hash then
                    failwith $"Sprint changed since checkpoint: {saved.Path}"

                let item =
                    SprintFiles.readSprint saved.Path
                    |> Option.defaultWith (fun () -> failwith $"Missing sprint: {saved.Path}")

                let status =
                    match saved.Status, saved.Iteration with
                    | "Todo", 0 -> Todo
                    | "Running", n when n > 0 -> Running(Implement, n)
                    | "Done", n when n > 0 -> Done n
                    | _ -> failwith "Invalid checkpoint sprint status"

                let history =
                    saved.History
                    |> Array.map (fun h ->
                        { Iteration = h.Iteration
                          AgentOutput = h.Output
                          VerifierResults =
                            h.Results |> Array.map (fun r -> r.Name, r.Passed, r.Summary) |> Array.toList })
                    |> Array.toList

                let timing =
                    { emptyTiming with
                        IterationHistory = history
                        IterationReasons = saved.Reasons |> Array.mapi (fun i reason -> i + 1, reason) |> Array.toList
                        Summary = if saved.Summary = "" then None else Some saved.Summary }

                { item with
                    TargetVerifiers = saved.Targets |> Option.ofObj |> Option.map Array.toList
                    EliminatedVerifiers = Set.ofArray saved.Eliminated },
                status,
                timing)
            |> Array.toList

        let running =
            backlog
            |> List.filter (fun (_, status, _) ->
                match status with
                | Running _ -> true
                | _ -> false)

        match checkpoint.Cursor with
        | Some cursor ->
            if not ([ "Implement"; "Verify" ] |> List.contains cursor.Stage) then
                failwith "Invalid resume stage"

            match running with
            | [ item, Running(_, iteration), timing ] when
                item.FilePath = cursor.SprintPath && iteration = cursor.Iteration
                ->
                if
                    cursor.Stage = "Verify"
                    && not (timing.IterationHistory |> List.exists (fun h -> h.Iteration = iteration))
                then
                    failwith "Verifier checkpoint lacks implementation result"
            | _ -> failwith "Resume cursor does not match unfinished sprint"
        | None ->
            if not running.IsEmpty then
                failwith "Unfinished sprint lacks resume cursor"

        if
            not (
                [ "Executing"; "Fixup"; "Final Verification"; "Complete" ]
                |> List.contains checkpoint.Phase
            )
        then
            failwith "Invalid checkpoint phase"

        let doneOnly =
            backlog
            |> List.forall (fun (_, status, _) ->
                match status with
                | Done _ -> true
                | _ -> false)

        if checkpoint.Phase = "Final Verification" && not doneOnly then
            failwith "Final verification requires completed sprints"

        if checkpoint.Phase = "Complete" then
            let passed =
                checkpoint.FinalResults
                |> Array.filter (fun result -> result.Passed)
                |> Array.map (fun result -> result.Name)
                |> Set.ofArray

            if
                not doneOnly
                || checkpoint.Cursor.IsSome
                || passed <> (Verifiers.listAll () |> Set.ofList)
            then
                failwith "Completed checkpoint lacks completed sprints or final verification"

        { emptyState with
            Backlog = backlog
            CurrentPhase = checkpoint.Phase
            StartTime = checkpoint.StartTime
            CompletedIterations = checkpoint.CompletedIterations
            ArbiterAttempt = checkpoint.ArbiterAttempt
            FinalVerifierResults =
                checkpoint.FinalResults
                |> Array.map (fun r -> r.Name, if r.Passed then Passed 1 else Failed 1)
                |> Map.ofArray
            FinalVerifierSummaries = checkpoint.FinalResults |> Array.map (fun r -> r.Name, r.Summary) |> Map.ofArray },
        checkpoint.Cursor

    let adopt path request =
        if File.Exists path then
            failwith "Checkpoint already exists; use --resume"

        let sprints = SprintFiles.readAllSprints ()

        if not (File.Exists Config.backlogFile) || sprints.IsEmpty then
            failwith "Adoption requires an existing backlog and sprint files"

        let state =
            { emptyState with
                Backlog = sprints |> List.map (fun s -> s, Todo, emptyTiming)
                CurrentPhase = "Executing" }

        save path request state None
        state, None

    let archive directory =
        let target =
            Path.Combine(
                directory,
                "archives",
                DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff")
                + "-"
                + Guid.NewGuid().ToString("N")
            )

        Directory.CreateDirectory target |> ignore

        for file in Directory.GetFiles directory do
            File.Copy(file, Path.Combine(target, Path.GetFileName file))

        let sprints = Path.Combine(directory, "sprints")

        if Directory.Exists sprints then
            Directory.CreateDirectory(Path.Combine(target, "sprints")) |> ignore

            for file in Directory.GetFiles sprints do
                File.Copy(file, Path.Combine(target, "sprints", Path.GetFileName file))

        target

module ProcessCapture =
    type Budget = { Idle: TimeSpan; Hard: TimeSpan }

    type Result =
        { Phase: string
          Pid: int
          Stdout: string
          Stderr: string
          ExitCode: Nullable<int>
          Reason: string
          InputCompleted: bool
          StreamsDrained: bool
          OwnedProcessesStopped: bool
          Failures: string array
          Elapsed: TimeSpan
          StdoutPath: string
          StderrPath: string
          ResultPath: string }

    let runCaptured (psi: ProcessStartInfo) phase budget prefix (input: string option) =
        if budget.Idle <= TimeSpan.Zero || budget.Hard <= TimeSpan.Zero then
            invalidArg "budget" "Budgets must be positive"

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath prefix))
        |> ignore

        let stdoutPath, stderrPath, resultPath =
            prefix + ".stdout.log", prefix + ".stderr.log", prefix + ".result.json"

        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.RedirectStandardInput <- input.IsSome

        if input.IsSome then
            psi.StandardInputEncoding <- UTF8Encoding(false)

        let clock = Stopwatch.StartNew()
        let mutable lastActivity = 0L
        let failures = ResizeArray<string>()
        File.WriteAllText(stdoutPath, "")
        File.WriteAllText(stderrPath, "")
        use owned = new ProcessContainment.OwnedProcess(psi)
        let child = owned.Process

        let drain (stream: Stream) encoding path =
            Task.Run(fun () ->
                use reader = new StreamReader(stream, encoding, true, 4096, true)
                use output = new StreamWriter(path, false, UTF8Encoding(false))
                output.AutoFlush <- true
                let buffer = Array.zeroCreate<char> 4096
                let mutable count = reader.Read(buffer, 0, buffer.Length)

                while count > 0 do
                    Interlocked.Exchange(&lastActivity, clock.Elapsed.Ticks) |> ignore
                    output.Write(buffer, 0, count)
                    count <- reader.Read(buffer, 0, buffer.Length))

        let encoding value =
            if isNull value then Console.OutputEncoding else value

        let stdout = drain owned.Stdout (encoding psi.StandardOutputEncoding) stdoutPath
        let stderr = drain owned.Stderr (encoding psi.StandardErrorEncoding) stderrPath

        let inputTask =
            Task.Run(fun () ->
                match input with
                | Some text ->
                    use writer = new StreamWriter(owned.Stdin.Value, psi.StandardInputEncoding, 4096)
                    writer.WriteLine text
                | None -> ())

        let mutable reason = "Exited"
        let mutable lastReported = 0L

        try
            while not (child.WaitForExit(50)) && reason = "Exited" do
                if clock.Elapsed >= budget.Hard then
                    reason <- "HardBudget"
                elif clock.Elapsed.Ticks - Interlocked.Read(&lastActivity) >= budget.Idle.Ticks then
                    reason <- "IdleLease"
                elif clock.Elapsed.Ticks - lastReported >= TimeSpan.FromSeconds(30.0).Ticks then
                    let activity = Interlocked.Read(&lastActivity)

                    if activity > lastReported then
                        Logging.info $"Agent output: phase={phase}, pid={child.Id}, elapsed={clock.Elapsed}"

                    lastReported <- clock.Elapsed.Ticks
        with ex ->
            reason <- "CaptureFailed"
            failures.Add ex.Message

        let failed category message =
            failures.Add $"{category}: {message}"

            if reason = "Exited" then
                reason <- category

        // The job outlives the direct parent, including descendants with no captured pipes.
        let cleanup = Stopwatch.StartNew()
        let mutable stopped = false

        try
            owned.Terminate()

            while not stopped && cleanup.ElapsedMilliseconds < 5000L do
                stopped <- owned.ActiveCount = 0u

                if not stopped then
                    Thread.Sleep 20

            if not stopped then
                failed "CleanupFailed" "Owned processes did not exit within five seconds"
        with ex ->
            failed "CleanupFailed" ex.Message

        let exitCode =
            if child.HasExited then
                Nullable child.ExitCode
            else
                Nullable()

        if not exitCode.HasValue then
            failed "CleanupFailed" "Parent exit code unavailable"

        // One shared I/O deadline; input completion includes closing the pipe.
        try
            Task.WaitAll([| stdout; stderr; inputTask |], 5000) |> ignore
        with :? AggregateException ->
            ()

        let completed category (work: Task) =
            if work.IsCompletedSuccessfully then
                true
            else
                let detail =
                    if work.IsFaulted then work.Exception.Flatten().Message
                    elif work.IsCanceled then "Task cancelled"
                    else "I/O completion exceeded five seconds"

                failed category detail
                false

        let stdoutDone = completed "DrainFailed" stdout
        let stderrDone = completed "DrainFailed" stderr
        let inputDone = completed "InputFailed" inputTask

        let read path =
            try
                use snapshot =
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

                use reader = new StreamReader(snapshot)
                reader.ReadToEnd()
            with ex ->
                failed "DrainFailed" ex.Message
                ""

        let output, error = read stdoutPath, read stderrPath

        let result =
            { Phase = phase
              Pid = child.Id
              Stdout = output
              Stderr = error
              ExitCode = exitCode
              Reason = reason
              InputCompleted = inputDone
              StreamsDrained = stdoutDone && stderrDone
              OwnedProcessesStopped = stopped
              Failures = failures.ToArray()
              Elapsed = clock.Elapsed
              StdoutPath = stdoutPath
              StderrPath = stderrPath
              ResultPath = resultPath }

        let pending = resultPath + ".new"

        use receipt =
            new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None)

        JsonSerializer.Serialize(receipt, result)
        receipt.Flush(true)
        receipt.Close()
        File.Move(pending, resultPath, true)
        result
