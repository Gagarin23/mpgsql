param(
    [ValidateRange(1, 86400)]
    [int] $TimeoutSeconds = 1800,
    [string[]] $BenchmarkArguments = @('--verify-query'),
    [string] $LogDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$assemblyPath = Join-Path $PSScriptRoot 'bin/Release/net10.0/Mpgsql.Benchmarks.dll'
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw 'Build src/Mpgsql.slnx in Release before running this script.'
}
if (-not $LogDirectory) { $LogDirectory = Join-Path $repositoryRoot 'artifacts/query-path/validation/external-timeout' }
$LogDirectory = [IO.Path]::GetFullPath($LogDirectory)
[IO.Directory]::CreateDirectory($LogDirectory) | Out-Null

$startInfo = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -CommandType Application).Source)
$startInfo.WorkingDirectory = $repositoryRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add($assemblyPath)
foreach ($argument in $BenchmarkArguments) { $startInfo.ArgumentList.Add($argument) }

$runProcess = [Diagnostics.Process]::new()
$runProcess.StartInfo = $startInfo
if (-not $runProcess.Start()) { throw 'Could not start the benchmark process.' }
$startUtc = [DateTimeOffset]::UtcNow
try {
    $stdoutTask = $runProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $runProcess.StandardError.ReadToEndAsync()
    $timedOut = -not $runProcess.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        # Only the process launched above and its BDN children are terminated.
        $runProcess.Kill($true)
        $runProcess.WaitForExit()
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $LogDirectory 'stdout.log'), $stdout)
    [IO.File]::WriteAllText((Join-Path $LogDirectory 'stderr.log'), $stderr)
    $resultCode = if ($timedOut) { 124 } else { $runProcess.ExitCode }
    [ordered]@{
        StartUtc = $startUtc
        EndUtc = [DateTimeOffset]::UtcNow
        Executable = $startInfo.FileName
        Assembly = $assemblyPath
        Arguments = $BenchmarkArguments
        TimeoutSeconds = $TimeoutSeconds
        TimedOut = $timedOut
        ExitCode = $resultCode
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $LogDirectory 'execution.json') -Encoding utf8
    Write-Output $stdout
    if ($stderr) { Write-Output $stderr }
    if ($timedOut) { Write-Output "External timeout after $TimeoutSeconds seconds; process tree stopped." }
}
finally {
    if (-not $runProcess.HasExited) { $runProcess.Kill($true) }
    $runProcess.Dispose()
}
exit $resultCode
