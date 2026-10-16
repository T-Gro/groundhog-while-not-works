$ErrorActionPreference = 'Stop'
$directory = $env:RECOVERY_PROMPT_DIRECTORY
function Write-Completion {
    [Console]::Out.WriteLine((@{ type = 'assistant.message'; data = @{ content = $env:RECOVERY_PROMPT_TOKEN; phase = 'final_answer'; toolRequests = @() } } | ConvertTo-Json -Depth 4 -Compress))
    [Console]::Out.WriteLine('{"type":"result","exitCode":0}')
}
@{ Arguments = @($args); Directory = (Get-Location).Path } |
    ConvertTo-Json -Depth 3 -Compress | Set-Content (Join-Path $directory 'invocation.json')
if ($env:RECOVERY_PROMPT_MODE -eq 'closed') {
    [Console]::OpenStandardInput().Close()
    Write-Completion
    exit 0
}
$inputBytes = [IO.MemoryStream]::new()
[Console]::OpenStandardInput().CopyTo($inputBytes)
$received = $inputBytes.ToArray()
[IO.File]::WriteAllBytes((Join-Path $directory 'received.bin'), $received)
$expected = [IO.File]::ReadAllBytes((Join-Path $directory 'expected.bin'))
if ([Convert]::ToBase64String($received) -cne [Convert]::ToBase64String($expected)) {
    [IO.File]::WriteAllText((Join-Path $directory 'mismatch.txt'), "PROMPT_BYTES_MISMATCH expected=$($expected.Length) actual=$($received.Length)")
    [Console]::Error.WriteLine('PROMPT_BYTES_MISMATCH')
    exit 41
}
Write-Completion
exit 0
