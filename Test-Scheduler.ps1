$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Join-Path ([IO.Path]::GetTempPath()) ("ralph-fixture-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$oldWork = $env:RALPH_WORK_DIR
$oldState = $env:RALPH_STATE_DIR
$oldIssue = $env:RALPH_ISSUE_NUMBER
$oldLaunch = $env:RALPH_LAUNCH_FILE
try {
    $env:RALPH_WORK_DIR = Join-Path $root 'worktree'
    $env:RALPH_STATE_DIR = [IO.Path]::Combine($root, 'data', 'ralph', 'issue-910')
    $env:RALPH_ISSUE_NUMBER = '0'
    $env:RALPH_LAUNCH_FILE = ''
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
    $psi = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $env:RALPH_WORK_DIR
    foreach ($argument in @('fsi', (Join-Path $PSScriptRoot 'Scheduler.Tests.fsx'), '--', '--fixture')) {
        $psi.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($psi)
    if (-not $process.WaitForExit(600000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Scheduler fixtures exceeded 600 seconds'
    }
    if ($process.ExitCode -ne 0) { throw "Scheduler fixtures exited $($process.ExitCode)" }
} finally {
    $env:RALPH_WORK_DIR = $oldWork
    $env:RALPH_STATE_DIR = $oldState
    $env:RALPH_ISSUE_NUMBER = $oldIssue
    $env:RALPH_LAUNCH_FILE = $oldLaunch
    Get-ChildItem -LiteralPath $root -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }
    [IO.Directory]::Delete($root, $true)
}
