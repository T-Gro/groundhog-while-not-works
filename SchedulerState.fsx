#r "nuget: FSharp.SystemTextJson, 1.4.36"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Utils
open TypeDefinitions
open BlockedProtocol

let private options =
    let options = JsonSerializerOptions()
    options.Converters.Add(JsonFSharpConverter())
    options
let private snapshotPath () = Path.Combine(Config.ralphDir, "scheduler-state.json")
let private completedPath () = Path.Combine(Config.ralphDir, "completed-state.json")
exception CheckpointFault of string

let private exists (path: string) =
    try
        File.GetAttributes path |> ignore
        true
    with :? FileNotFoundException | :? DirectoryNotFoundException -> false

let private issue () =
    match Environment.GetEnvironmentVariable "RALPH_ISSUE_NUMBER" with
    | null -> 0
    | value ->
        match Int32.TryParse value with
        | true, number when number >= 0 -> number
        | _ -> failwith "Invalid RALPH_ISSUE_NUMBER."

let blockRequest (output: string) =
    let lines = output.Split('\n') |> Array.filter (fun line -> line.TrimStart().StartsWith("SUBTASK_BLOCKED", StringComparison.OrdinalIgnoreCase))
    if lines.Length = 0 then None
    else
        let invalid = "Invalid or contradictory SUBTASK_BLOCKED request; owner must inspect the retained attempt."
        try
            if lines.Length <> 1 || XmlHelpers.hasSignalAny "SUBTASK_COMPLETE" output
               || XmlHelpers.hasSignalAny "SUBTASK_INCOMPLETE" output then Some invalid
            else
                use json = JsonDocument.Parse(lines[0].Trim().Substring("SUBTASK_BLOCKED".Length).Trim())
                let reason = json.RootElement.GetProperty("reason").GetString()
                let condition = json.RootElement.GetProperty("retryCondition").GetString()
                if json.RootElement.EnumerateObject() |> Seq.length <> 2
                   || String.IsNullOrWhiteSpace reason || reason.Length > 512
                   || condition <> "owner-launch-enrolled" then Some invalid
                else Some reason
        with
        | :? JsonException | :? Collections.Generic.KeyNotFoundException | :? InvalidOperationException -> Some invalid

let save (state: State) =
    try
        validateStateDirectory (Path.GetFullPath Config.workDir) Config.ralphDir
        |> Directory.CreateDirectory
        |> ignore
        let path = snapshotPath ()
        let staging = path + ".new"
        use stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None)
        JsonSerializer.Serialize(stream, state, options)
        stream.Flush true
        stream.Close()
        File.Move(staging, path, true)
        if state.CurrentPhase <> "Complete" then File.Delete(completedPath ())
    with ex -> raise (CheckpointFault $"Cannot persist scheduler checkpoint: {ex.Message}")

let persistBlock sprint reason (state: State) =
    save state
    let head, checkpoint = checkpoint Config.workDir Config.ralphDir
    let block = {
        Version = 1; BlockId = Guid.NewGuid().ToString("N"); Issue = issue ()
        Worktree = Path.GetFullPath Config.workDir; Head = head; Checkpoint = checkpoint
        Sprint = Path.GetFullPath sprint; Reason = reason; RetryCondition = "owner-launch-enrolled"
        LaunchFile = Environment.GetEnvironmentVariable("RALPH_LAUNCH_FILE") |> Option.ofObj |> Option.defaultValue ""
        SnapshotHash = fileHash (snapshotPath ())
    }
    writeBlock Config.ralphDir block
    block

let pendingBlock () =
    try
        readBlock Config.ralphDir
        |> Option.map (fun block ->
            validateBlock Config.ralphDir (issue ()) Config.workDir block
            block)
    with ex -> raise (CheckpointFault $"Cannot read blocked checkpoint: {ex.Message}")

type Restore = Fresh | Finished of State | Paused of BlockRecord | Resumed of State

let restore allowPlanning =
    match pendingBlock () with
    | Some block ->
        let launch = Environment.GetEnvironmentVariable "RALPH_LAUNCH_FILE"
        if exists (Path.Combine(Config.ralphDir, "resume-launch.json")) then
            let saved = JsonSerializer.Deserialize<State>(File.ReadAllText(snapshotPath ()), options)
            if obj.ReferenceEquals(saved, null) || saved.Backlog.IsEmpty
               || not (List.contains block.Sprint saved.ActiveSprints)
               || not (saved.Backlog |> List.exists (fun (item, status, _) ->
                   item.FilePath = block.Sprint && (match status with Blocked _ -> true | _ -> false))) then
                failwith "Blocked scheduler snapshot does not contain its checkpoint sprint."
            if consumeResume Config.ralphDir block launch then
                File.Copy(snapshotPath (), Path.Combine(Config.ralphDir, $"scheduler-state-{block.BlockId}.json"))
                Resumed saved
            else Paused block
        else Paused block
    | None ->
        if allowPlanning then Fresh
        elif exists (snapshotPath ()) then
            failwith "Retained sprint state has no authenticated dispatch checkpoint; explicit recovery is required."
        elif exists (completedPath ()) then
            let saved = JsonSerializer.Deserialize<State>(File.ReadAllText(completedPath ()), options)
            if obj.ReferenceEquals(saved, null) || saved.CurrentPhase <> "Complete"
               || exists (Path.Combine(Config.ralphDir, "resume-launch.json")) then
                failwith "Retained sprint state has no authenticated dispatch checkpoint; explicit recovery is required."
            Finished saved
        elif not (Prompting.SprintFiles.listSprints().IsEmpty) then
            failwith "Retained sprint files have no scheduler state; explicit recovery is required."
        else Fresh

let archiveCompleted () =
    let path = snapshotPath ()
    if exists path then
        File.Copy(path, Path.Combine(Config.ralphDir, $"completed-{Guid.NewGuid():N}.json"))
        File.Move(path, completedPath (), true)
