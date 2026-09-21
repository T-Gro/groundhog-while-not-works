$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Join-Path $PSScriptRoot (".fake\scheduler-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$oldWork = $env:RALPH_WORK_DIR
$oldState = $env:RALPH_STATE_DIR
$oldIssue = $env:RALPH_ISSUE_NUMBER
$oldLaunch = $env:RALPH_LAUNCH_FILE
$oldFixtureLaunch = $env:RALPH_FIXTURE_LAUNCH
try {
    $env:RALPH_WORK_DIR = Join-Path $root 'worktree'
    $env:RALPH_STATE_DIR = [IO.Path]::Combine($root, 'data', 'ralph', 'issue-910')
    $env:RALPH_ISSUE_NUMBER = '0'
    $env:RALPH_LAUNCH_FILE = ''
    $env:RALPH_FIXTURE_LAUNCH = Join-Path $root 'fixture-launch.json'
    [IO.Directory]::CreateDirectory($env:RALPH_WORK_DIR) | Out-Null
    git -C $env:RALPH_WORK_DIR init --quiet
    git -C $env:RALPH_WORK_DIR config core.autocrlf false
    git -C $env:RALPH_WORK_DIR config core.safecrlf false
    git -C $env:RALPH_WORK_DIR -c user.name=Fixture -c user.email=fixture@example.invalid commit --allow-empty --quiet -m fixture
    $tracked = Join-Path $env:RALPH_WORK_DIR 'tracked.txt'
    [IO.File]::WriteAllText($tracked, "committed`n")
    git -C $env:RALPH_WORK_DIR add tracked.txt
    git -C $env:RALPH_WORK_DIR -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m checkpoint
    [IO.File]::WriteAllText($tracked, "staged`n")
    git -C $env:RALPH_WORK_DIR add tracked.txt
    if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare fixture repository' }
    $launcher = [Diagnostics.Process]::GetCurrentProcess()
    $launcherStart = $launcher.StartTime
    function Invoke-Fixture([string[]] $Arguments, [int] $ExpectedExit) {
        $psi = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = $env:RALPH_WORK_DIR
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.Environment['RALPH_FIXTURE_SINK'] = (Join-Path $root 'sink.txt')
        foreach ($argument in (@('fsi', (Join-Path $PSScriptRoot 'Scheduler.Tests.fsx'), '--', '--fixture') + $Arguments)) {
            $psi.ArgumentList.Add($argument)
        }
        if (($psi.ArgumentList | Measure-Object -Property Length -Sum).Sum -gt 2048) { throw 'Unbounded request argument vector' }
        $process = [Diagnostics.Process]::Start($psi)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        try {
            [IO.File]::WriteAllText($env:RALPH_FIXTURE_LAUNCH, (@{
                Executor = @{ Pid = $launcher.Id; StartedUtc = $launcher.StartTime.ToUniversalTime() }
                Child = @{ Pid = $process.Id; StartedUtc = $process.StartTime.ToUniversalTime() }
            } | ConvertTo-Json))
            if (-not $process.WaitForExit(600000)) { throw 'Scheduler fixtures exceeded 600 seconds' }
            Write-Output $stdout.GetAwaiter().GetResult()
            Write-Output $stderr.GetAwaiter().GetResult()
            Write-Output "SUBPROCESS pid=$($process.Id) actual=$($process.ExitCode) expected=$ExpectedExit"
            if ($process.ExitCode -ne $ExpectedExit) { throw "Scheduler fixtures exited $($process.ExitCode), expected $ExpectedExit" }
            if ($Arguments[0] -eq '--recovery-exit-case') {
                $case = $Arguments[1]
                if (-not $stdout.Result.Contains("PASS recovery exit fixture ${case}:")) { throw 'Recovery exit assertions did not complete' }
                if ($case -in @('fresh', 'resumed') -and -not $stderr.Result.Contains('injected ordinary arbiter transport failure')) {
                    throw 'Ordinary arbiter exception was not propagated'
                }
                if ($case -eq 'checkpoint-fault' -and (-not $stdout.Result.Contains('PASS checkpoint finalization fault:') -or
                    -not $stderr.Result.Contains('Cannot persist scheduler checkpoint:') -or
                    -not $stderr.Result.Contains('scheduler-state.json.new'))) {
                    throw 'Checkpoint finalization fault was not propagated'
                }
            }
            if ($launcher.StartTime -ne $launcherStart) { throw 'Launcher identity changed' }
        } finally {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    Invoke-Fixture @() 0
    foreach ($case in @(@('complete', 0), @('failure', 1), @('blocked', 42), @('fault', 43))) {
        Invoke-Fixture @('--exit-case', $case[0]) $case[1]
    }
    foreach ($case in @(@('fresh', 1), @('resumed', 1), @('replay', 43), @('checkpoint-fault', 43))) {
        Invoke-Fixture @('--recovery-exit-case', $case[0]) $case[1]
    }
    $request = ('{ "quoted": "žluťoučký 🦔 日本語" }' + "`r`n") * 1500 + "`n"
    $requestPath = Join-Path $root 'long request 日本語.txt'
    [IO.File]::WriteAllText($requestPath, $request)
    Invoke-Fixture @('--request-sink', '--request-file', $requestPath, '--yes') 0
    if ($request.Length -lt 50000 -or [IO.File]::ReadAllText((Join-Path $root 'sink.txt')) -cne $request) {
        throw 'Actual request reader subprocess did not preserve exact long Unicode request'
    }
    Write-Output "PASS request-file subprocess sink: $($request.Length) UTF-16 units, bounded arguments, trailing newlines preserved"
} finally {
    $env:RALPH_WORK_DIR = $oldWork
    $env:RALPH_STATE_DIR = $oldState
    $env:RALPH_ISSUE_NUMBER = $oldIssue
    $env:RALPH_LAUNCH_FILE = $oldLaunch
    $env:RALPH_FIXTURE_LAUNCH = $oldFixtureLaunch
    Get-ChildItem -LiteralPath $root -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }
    [IO.Directory]::Delete($root, $true)
}
