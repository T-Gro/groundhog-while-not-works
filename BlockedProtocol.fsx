module BlockedProtocol

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

let blockedExitCode = 42
let faultExitCode = 43

type BlockRecord = {
    Version: int
    BlockId: string
    Issue: int
    Worktree: string
    Head: string
    Checkpoint: string
    Sprint: string
    Reason: string
    RetryCondition: string
    LaunchFile: string
    SnapshotHash: string
}

type ResumeLaunch = {
    Version: int
    BlockId: string
    Checkpoint: string
    PreviousLaunchFile: string
    NewLaunchFile: string
    NewExecutorPid: int
    NewExecutorStartedUtc: DateTime
}

let samePath left right =
    String.Equals(Path.GetFullPath left, Path.GetFullPath right,
                  if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal)

let ownedPath (path: string) =
    if String.IsNullOrWhiteSpace path || not (Path.IsPathFullyQualified path) then
        failwith "Blocked protocol requires absolute owned paths."
    let path = Path.TrimEndingDirectorySeparator(Path.GetFullPath path)
    let mutable current = path
    while not (String.IsNullOrEmpty current) do
        try
            if File.GetAttributes(current).HasFlag FileAttributes.ReparsePoint then
                failwith $"Redirected blocked protocol path: {current}"
        with :? FileNotFoundException | :? DirectoryNotFoundException -> ()
        current <- Path.GetDirectoryName current
    path

let private pathIn directory name = Path.Combine(ownedPath directory, name) |> ownedPath

let validateStateDirectory worktree stateDir =
    let worktree, stateDir = ownedPath worktree, ownedPath stateDir
    let relative = Path.GetRelativePath(worktree, stateDir)
    if samePath worktree stateDir
       || (not (Path.IsPathFullyQualified relative) && relative.Split(Path.DirectorySeparatorChar)[0] <> "..") then
        failwith "Blocked state must be external to the worktree."
    stateDir

let private writeAtomic<'T> overwrite (path: string) (value: 'T) =
    let path = ownedPath path
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let staging = path + $".{Guid.NewGuid():N}.new"
    try
        use stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)
        JsonSerializer.Serialize<'T>(stream, value)
        stream.Flush(true)
        stream.Dispose()
        File.Move(staging, path, overwrite)
    finally
        if File.Exists staging then File.Delete staging

let atomicWrite path value = writeAtomic true path value
let atomicCreate path value = writeAtomic false path value

let readJson<'T> path =
    use stream = File.OpenRead(ownedPath path)
    use document = JsonDocument.Parse stream
    let root = document.RootElement
    let fields = typeof<'T>.GetProperties() |> Array.map (fun p -> p.Name) |> Set.ofArray
    let actual = root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toArray
    if actual.Length <> fields.Count || Set.ofArray actual <> fields then
        failwith $"Invalid blocked protocol schema: {path}"
    let value = root.Deserialize<'T>()
    if obj.ReferenceEquals(value, null) then failwith $"Null blocked protocol record: {path}"
    value

let fileHash path =
    use stream = File.OpenRead(ownedPath path)
    Convert.ToHexString(SHA256.HashData stream)

let private gitBytes worktree arguments =
    let psi = ProcessStartInfo("git")
    for argument in ["-C"; worktree] @ arguments do psi.ArgumentList.Add argument
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use proc = Process.Start psi
    use output = new MemoryStream()
    let copied = proc.StandardOutput.BaseStream.CopyToAsync output
    let error = proc.StandardError.ReadToEndAsync()
    if not (proc.WaitForExit 120000) then
        proc.Kill true
        failwith "Checkpoint Git command timed out."
    copied.GetAwaiter().GetResult()
    if proc.ExitCode <> 0 then failwith $"Checkpoint Git command failed: {error.Result}"
    output.ToArray()

let checkpoint worktree stateDir =
    let worktree, stateDir = ownedPath worktree, validateStateDirectory worktree stateDir
    let git arguments = gitBytes worktree arguments
    let head = Encoding.UTF8.GetString(git ["rev-parse"; "--verify"; "HEAD"]).Trim()
    let excluded = [".executor-pid"; ".copilot-prompt.txt"; ".copilot-output.md"]
    let paths = "--" :: "." :: (excluded |> List.map (fun p -> ":(exclude)" + p))
    use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
    let append (name: string) (bytes: byte array) =
        let label = Encoding.UTF8.GetBytes name
        hash.AppendData(BitConverter.GetBytes label.Length)
        hash.AppendData label
        hash.AppendData(BitConverter.GetBytes bytes.LongLength)
        hash.AppendData bytes
    append "HEAD" (Encoding.UTF8.GetBytes head)
    append "staged" (git (["diff"; "--no-ext-diff"; "--no-textconv"; "--binary"; "--cached"] @ paths))
    append "unstaged" (git (["diff"; "--no-ext-diff"; "--no-textconv"; "--binary"] @ paths))
    let untracked = Encoding.UTF8.GetString(git ["ls-files"; "--others"; "--exclude-standard"; "-z"])
    for relative in untracked.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.sort do
        if not (List.contains relative excluded) then
            let path = pathIn worktree relative
            append ("untracked:" + relative) (File.ReadAllBytes path)
    let rec sprints directory =
        for path in Directory.GetFileSystemEntries(ownedPath directory) |> Array.sort do
            let path = ownedPath path
            if File.GetAttributes(path).HasFlag FileAttributes.Directory then sprints path
            else append ("sprint:" + path) (File.ReadAllBytes path)
    let sprintDir = pathIn stateDir "sprints"
    try sprints sprintDir
    with :? DirectoryNotFoundException -> ()
    head, Convert.ToHexString(hash.GetHashAndReset())

let readBlock stateDir =
    try Some(readJson<BlockRecord> (pathIn stateDir "blocked.json"))
    with :? FileNotFoundException | :? DirectoryNotFoundException -> None

let validateBlock stateDir issue worktree (block: BlockRecord) =
    let hash pattern value = not (isNull value) && Regex.IsMatch(value, pattern)
    let stateDir, worktree = ownedPath stateDir, ownedPath worktree
    let sprintDir = pathIn stateDir "sprints"
    let sprint = ownedPath block.Sprint
    let relative = Path.GetRelativePath(sprintDir, sprint)
    if block.Version <> 1 || block.Issue <> issue || issue < 0
       || not (hash @"\A[0-9a-f]{32}\z" block.BlockId)
       || not (samePath (ownedPath block.Worktree) worktree)
       || not (hash @"\A[0-9a-f]{40}\z" block.Head)
       || not (hash @"\A[0-9A-F]{64}\z" block.Checkpoint)
       || not (hash @"\A[0-9A-F]{64}\z" block.SnapshotHash)
       || String.IsNullOrWhiteSpace block.Reason || block.Reason.Length > 512
       || block.RetryCondition <> "owner-launch-enrolled"
       || Path.IsPathFullyQualified relative || relative = "." || relative.Split(Path.DirectorySeparatorChar)[0] = ".." then
        failwith "Invalid blocked task identity, checkpoint, or retry condition."
    use sprintFile = File.OpenRead sprint
    if issue > 0 || block.LaunchFile <> "" then
        let launch = ownedPath block.LaunchFile
        let dataDir = Path.GetDirectoryName(Path.GetDirectoryName stateDir)
        let captureDir = Path.Combine(dataDir, "executors", string issue)
        if not (samePath (Path.GetDirectoryName launch) captureDir)
           || not (launch.EndsWith(".launch.json", StringComparison.Ordinal)) then
            failwith "Blocked launch is outside the issue-owned capture directory."
    if fileHash (pathIn stateDir "scheduler-state.json") <> block.SnapshotHash then
        failwith "Blocked scheduler snapshot changed."
    if checkpoint worktree stateDir <> (block.Head, block.Checkpoint) then
        failwith "Blocked source or external sprint checkpoint changed."

let writeBlock stateDir (block: BlockRecord) =
    validateBlock stateDir block.Issue block.Worktree block
    let path = pathIn stateDir "blocked.json"
    match readBlock stateDir with
    | Some _ -> failwith "A durable block already exists."
    | None -> atomicCreate path block

let private requireDead pid =
    if pid <= 0 then failwith "Missing previous launch process identity."
    try
        use proc = Process.GetProcessById pid
        if not proc.HasExited then failwith "Previous launch PID is still live (or has been reused)."
    with :? ArgumentException -> ()

module private Native =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ProcessBasicInformation =
        val mutable ExitStatus: nativeint
        val mutable Peb: nativeint
        val mutable Affinity: nativeint
        val mutable Priority: nativeint
        val mutable Pid: nativeint
        val mutable ParentPid: nativeint

    [<DllImport("ntdll.dll")>]
    extern int NtQueryInformationProcess(nativeint handle, int informationClass, ProcessBasicInformation& information, int size, int& returned)

let private parentPid (proc: Process) =
    if OperatingSystem.IsWindows() then
        let mutable information = Unchecked.defaultof<Native.ProcessBasicInformation>
        let mutable returned = 0
        let status = Native.NtQueryInformationProcess(proc.Handle, 0, &information, Marshal.SizeOf<Native.ProcessBasicInformation>(), &returned)
        if status <> 0 then failwith $"Cannot authenticate parent of PID {proc.Id}: NTSTATUS {status}."
        int information.ParentPid
    elif OperatingSystem.IsLinux() then
        let stat = File.ReadAllText($"/proc/{proc.Id}/stat")
        Int32.Parse(stat.Substring(stat.LastIndexOf(')') + 2).Split(' ')[1])
    else
        let psi = ProcessStartInfo("ps")
        for argument in ["-o"; "ppid="; "-p"; string proc.Id] do psi.ArgumentList.Add argument
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        use query = Process.Start psi
        let output = query.StandardOutput.ReadToEndAsync()
        if not (query.WaitForExit 5000) then
            query.Kill()
            query.WaitForExit()
            failwith "Parent identity query timed out."
        if query.ExitCode <> 0 then failwith $"Cannot authenticate parent of PID {proc.Id}."
        Int32.Parse(output.Result.Trim())

let private requireLive (pid, started: DateTime) =
    if pid <= 0 || started.Kind <> DateTimeKind.Utc || started = DateTime.MinValue then
        failwith "Missing enrolled process identity."
    use proc = Process.GetProcessById pid
    if proc.HasExited || proc.StartTime.ToUniversalTime() <> started then
        failwith "Enrolled process is dead or its PID has been reused."

let private requireAncestor ancestor descendant =
    requireLive ancestor
    requireLive descendant
    let visited = System.Collections.Generic.HashSet<int>()
    let mutable current = descendant
    while current <> ancestor do
        let pid, started = current
        if visited.Count >= 128 || not (visited.Add pid) then failwith "Invalid process ancestry."
        use proc = Process.GetProcessById pid
        requireLive current
        use parent = Process.GetProcessById(parentPid proc)
        let parentIdentity = parent.Id, parent.StartTime.ToUniversalTime()
        if parent.HasExited || snd parentIdentity > started then failwith "Parent PID was reused."
        current <- parentIdentity
    requireLive ancestor
    requireLive descendant

let consumeResume stateDir (block: BlockRecord) launchFile =
    validateBlock stateDir block.Issue block.Worktree block
    if readBlock stateDir <> Some block then failwith "Resume requires its exact current durable block."
    let claimPath = pathIn stateDir "resume-launch.json"
    let claim =
        try Some(readJson<ResumeLaunch> claimPath)
        with :? FileNotFoundException -> None
    match claim with
    | None -> false
    | Some claim ->
        if claim.Version <> 1 || claim.BlockId <> block.BlockId || claim.Checkpoint <> block.Checkpoint
           || not (samePath (ownedPath claim.PreviousLaunchFile) block.LaunchFile)
           || not (samePath (ownedPath claim.NewLaunchFile) (ownedPath launchFile))
           || not (samePath (Path.GetDirectoryName launchFile) (Path.GetDirectoryName block.LaunchFile))
           || not (launchFile.EndsWith(".launch.json", StringComparison.Ordinal))
           || samePath launchFile block.LaunchFile || claim.NewExecutorPid <= 0
           || claim.NewExecutorStartedUtc.Kind <> DateTimeKind.Utc then
            failwith "Resume launch claim does not match the blocked checkpoint."
        use previous = JsonDocument.Parse(File.ReadAllText(ownedPath block.LaunchFile))
        use current = JsonDocument.Parse(File.ReadAllText(ownedPath launchFile))
        let validate (root: JsonElement) =
            if root.GetProperty("Issue").GetInt32() <> block.Issue
               || not (samePath (ownedPath (root.GetProperty("Worktree").GetString())) block.Worktree) then
                failwith "Resume launch task mismatch."
        validate previous.RootElement
        validate current.RootElement
        for name in ["Executor"; "Child"] do
            let old = previous.RootElement.GetProperty name
            if old.GetProperty("StartedUtc").GetDateTime() = DateTime.MinValue then failwith "Missing previous start identity."
            requireDead (old.GetProperty("Pid").GetInt32())
        let executor = current.RootElement.GetProperty "Executor"
        if executor.GetProperty("Pid").GetInt32() <> claim.NewExecutorPid
           || executor.GetProperty("StartedUtc").GetDateTime() <> claim.NewExecutorStartedUtc then
            failwith "Resume executor binding changed."
        let executorIdentity = claim.NewExecutorPid, claim.NewExecutorStartedUtc
        let child = current.RootElement.GetProperty "Child"
        let childIdentity = child.GetProperty("Pid").GetInt32(), child.GetProperty("StartedUtc").GetDateTime()
        if childIdentity = executorIdentity then failwith "Resume child must be a distinct executor descendant."
        requireAncestor executorIdentity childIdentity
        use consumer = Process.GetCurrentProcess()
        requireAncestor childIdentity (consumer.Id, consumer.StartTime.ToUniversalTime())
        let used = pathIn stateDir $"resume-used-{block.BlockId}.json"
        let archived = pathIn stateDir $"blocked-{block.BlockId}.json"
        File.Move(claimPath, used)
        File.Move(pathIn stateDir "blocked.json", archived)
        true
