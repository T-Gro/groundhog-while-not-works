$ErrorActionPreference = 'Stop'
$directory = $env:RECOVERY_PROMPT_DIRECTORY
@{ Arguments = @($args); Directory = (Get-Location).Path } |
    ConvertTo-Json -Depth 3 -Compress | Set-Content (Join-Path $directory 'invocation.json')
if ($env:RECOVERY_PROMPT_MODE -eq 'closed') {
    [Console]::OpenStandardInput().Close()
    [Console]::Out.WriteLine('SUBTASK_COMPLETE')
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
[Console]::Out.WriteLine($env:RECOVERY_PROMPT_TOKEN)
exit 0
