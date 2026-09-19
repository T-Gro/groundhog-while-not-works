open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Recovery

let run root inherited =
    let check condition message =
        if not condition then
            failwith message

    let directory = Path.Combine(root, "parent-first-" + inherited)
    Directory.CreateDirectory directory |> ignore
    let path name = Path.Combine(directory, name)
    let quote (text: string) = "'" + text.Replace("'", "''") + "'"
    let identity name = path (name + ".identity")
    let handlesAssembly = Path.Combine(root, "Capture.Handles.dll")
    let arguments = [| ""; "spaces and \"quotes\""; "trailing\\"; "λ" |]
    let environment = "quoted \"environment\" λ"

    if not (File.Exists handlesAssembly) then
        let psi = ProcessStartInfo("dotnet", "--list-sdks", RedirectStandardOutput = true)
        use listing = Process.Start psi
        let sdk = listing.StandardOutput.ReadToEnd().Trim().Split('\n') |> Array.last
        check (listing.WaitForExit(30000) && listing.ExitCode = 0) "SDK discovery failed"
        let location = sdk.Substring(sdk.IndexOf('[') + 1).TrimEnd(']', '\r')
        let version = sdk.Split(' ')[0]
        let compiler = Path.Combine(location, version, "FSharp", "fsc.dll")
        let build = ProcessStartInfo("dotnet", UseShellExecute = false)

        for arg in
            [ compiler
              "--nologo"
              "--target:library"
              "--targetprofile:netcore"
              "--out:" + handlesAssembly
              Path.Combine(__SOURCE_DIRECTORY__, "Capture.Handles.fs") ] do
            build.ArgumentList.Add arg

        use child = Process.Start build

        if not (child.WaitForExit(30000)) then
            child.Kill(true)
            failwith "Fixture native-handle helper compilation timed out"

        check (child.ExitCode = 0) "Fixture native-handle helper compilation failed"

    let ownedProcess name =
        if File.Exists(identity name) then
            let parts = File.ReadAllLines(identity name)

            try
                let p = Process.GetProcessById(int parts[0])

                if p.StartTime.ToUniversalTime().Ticks = int64 parts[1] && not p.HasExited then
                    Some p
                else
                    p.Dispose()
                    None
            with
            | :? ArgumentException
            | :? InvalidOperationException -> None
        else
            None

    let alive name =
        match ownedProcess name with
        | Some p ->
            p.Dispose()
            true
        | None -> false

    let record name =
        $"[IO.File]::WriteAllLines({quote (identity name)}, @([string]$PID, [string]([Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks)))"

    File.WriteAllText(path "descendant.ps1", record "descendant" + "; Start-Sleep -Seconds 120")
    File.WriteAllText(path "control.ps1", record "control" + "; Start-Sleep -Seconds 120")

    let excludedHandles =
        [ if inherited <> "stdout" && inherited <> "both" then
              "-11"
          if inherited <> "stderr" && inherited <> "both" then
              "-12" ]
        |> String.concat ", "

    File.WriteAllText(
        path "parent.ps1",
        $"""
$ErrorActionPreference = 'Stop'
{record "parent"}
[IO.File]::WriteAllText({quote (path "launch.json")}, (@{{ Arguments = @($args); Directory = [Environment]::CurrentDirectory; Environment = $env:CAPTURE_FIXTURE_VALUE }} | ConvertTo-Json -Compress))
[Console]::Out.WriteLine('parent-stdout')
[Console]::Error.WriteLine('parent-stderr')
[void][Reflection.Assembly]::LoadFrom({quote handlesAssembly})
# Process.Start otherwise inherits unused original handles as well as redirected ones.
foreach ($kind in @({excludedHandles})) {{
    if (-not [CaptureFixture.Handles]::ClearInheritance($kind)) {{ throw 'Cannot isolate inherited fixture handles' }}
}}
$psi = [Diagnostics.ProcessStartInfo]::new('pwsh')
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = ${if inherited = "stdout" || inherited = "both" then
                                    "false"
                                else
                                    "true"}
$psi.RedirectStandardError = ${if inherited = "stderr" || inherited = "both" then
                                   "false"
                               else
                                   "true"}
foreach ($arg in @('-NoProfile', '-File', {quote (path "descendant.ps1")})) {{ $psi.ArgumentList.Add($arg) }}
$child = [Diagnostics.Process]::Start($psi)
if (-not [Threading.SpinWait]::SpinUntil([Func[bool]]{{ [IO.File]::Exists({quote (identity "descendant")}) }}, 30000)) {{ throw 'Descendant readiness timed out' }}
[IO.File]::WriteAllText({quote (path "parent-exit.utc")}, [DateTime]::UtcNow.ToString('O'))
exit 7
"""
    )

    let startInfo file =
        let psi = ProcessStartInfo("pwsh", UseShellExecute = false)
        psi.WorkingDirectory <- directory
        psi.Environment["CAPTURE_FIXTURE_VALUE"] <- environment

        for arg in [ "-NoProfile"; "-File"; path file ] do
            psi.ArgumentList.Add arg

        if file = "parent.ps1" then
            for arg in arguments do
                psi.ArgumentList.Add arg

        psi

    let mutable observed = ""

    try
        use control = Process.Start(startInfo "control.ps1")

        check
            (SpinWait.SpinUntil((fun () -> File.Exists(identity "control") || control.HasExited), 30000)
             && not control.HasExited)
            "Control readiness timed out"

        let budget: ProcessCapture.Budget =
            { Idle = TimeSpan.FromSeconds 40.
              Hard = TimeSpan.FromSeconds 45. }

        let prefix = path "capture"
        let mutable result = None
        let mutable error = ""

        try
            result <- Some(ProcessCapture.runCaptured (startInfo "parent.ps1") "parent-first" budget prefix None)
        with ex ->
            error <- ex.Message

        let elapsedSinceExit =
            if File.Exists(path "parent-exit.utc") then
                DateTime.UtcNow
                - DateTime.Parse(File.ReadAllText(path "parent-exit.utc")).ToUniversalTime()
            else
                TimeSpan.MaxValue

        observed <-
            JsonSerializer.Serialize
                {| Inherited = inherited
                   Error = error
                   ReceiptExists = File.Exists(prefix + ".result.json")
                   DescendantAlive = alive "descendant"
                   ControlAlive = alive "control"
                   AfterParentExitSeconds = elapsedSinceExit.TotalSeconds |}

        File.WriteAllText(path "observed.json", observed)
        printfn "%s" observed
        check (File.Exists(identity "descendant")) "Fixture never reached descendant readiness"
        check (File.Exists(prefix + ".result.json")) $"Terminal receipt missing after parent exit: {error}"

        let actual =
            result |> Option.defaultWith (fun () -> failwith $"Capture threw: {error}")

        let receipt =
            JsonSerializer.Deserialize<ProcessCapture.Result>(File.ReadAllText actual.ResultPath)

        check (receipt = actual) "Serialized receipt differs from observed capture"
        check (actual.Phase = "parent-first") "Phase lost"
        let parentIdentity = File.ReadAllLines(identity "parent")
        check (actual.Pid = int parentIdentity[0]) "Parent PID lost"
        use launch = JsonDocument.Parse(File.ReadAllText(path "launch.json"))

        let launchArgs =
            launch.RootElement.GetProperty("Arguments").EnumerateArray()
            |> Seq.map (fun arg -> arg.GetString())
            |> Seq.toArray

        check (launchArgs = arguments) "ArgumentList changed across native launch"
        check (launch.RootElement.GetProperty("Directory").GetString() = directory) "Working directory changed"
        check (launch.RootElement.GetProperty("Environment").GetString() = environment) "Environment changed"
        check (actual.ExitCode = Nullable 7 && actual.Reason = "Exited") "Known parent exit lost"

        check
            (actual.InputCompleted
             && actual.StreamsDrained
             && actual.OwnedProcessesStopped
             && actual.Failures.Length = 0)
            "Clean capture receipt has incomplete lifecycle fields"

        check (actual.Stdout = "parent-stdout" + Environment.NewLine) "Exact stdout lost"
        check (actual.Stderr = "parent-stderr" + Environment.NewLine) "Exact stderr lost"
        check (File.ReadAllText actual.StdoutPath = actual.Stdout) "Durable stdout differs"
        check (File.ReadAllText actual.StderrPath = actual.Stderr) "Durable stderr differs"
        check (not (alive "descendant")) "Owned descendant survived parent exit"
        check (alive "control") "Unrelated control was terminated"
        check (elapsedSinceExit < TimeSpan.FromSeconds 12.) "Post-parent cleanup exceeded ten-second grace"
    finally
        let errors = ResizeArray<string>()

        for name in [ "parent"; "descendant"; "control" ] do
            try
                match ownedProcess name with
                | Some p ->
                    use p = p
                    p.Kill()
                    check (p.WaitForExit(5000)) $"Fixture cleanup timed out: {name}"
                | None -> ()
            with ex ->
                errors.Add $"{name}: {ex.Message}"

        File.WriteAllText(
            path "cleanup.json",
            JsonSerializer.Serialize
                {| AllOwnedStopped = not (alive "parent" || alive "descendant" || alive "control")
                   Observed = observed |}
        )

        check (errors.Count = 0) (String.concat "; " errors)
