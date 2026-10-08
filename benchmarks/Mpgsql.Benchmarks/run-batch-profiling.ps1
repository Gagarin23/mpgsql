param(
    [switch] $PrepareOnly,
    [string] $Artifacts = '',
    [string] $Snapshot = '',
    [ValidateRange(1, 65536)] [int] $TraceCohorts = 1024,
    [ValidateRange(1, 65536)] [int] $MemoryCohorts = 128,
    [ValidateRange(1, 86400)] [int] $TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Artifacts)
{
    $Artifacts = Join-Path $repositoryRoot 'artifacts/query-profile/batch'
}
$Artifacts = [IO.Path]::GetFullPath($Artifacts)
if (-not $Snapshot)
{
    $Snapshot = Join-Path $Artifacts 'source-snapshot'
}
$Snapshot = [IO.Path]::GetFullPath($Snapshot)
$planPath = Join-Path $Artifacts 'plan.json'
[IO.Directory]::CreateDirectory($Artifacts) | Out-Null
$toolsRoot = Join-Path $repositoryRoot 'artifacts/query-profile/tools'
$toolsManifest = Get-Content -LiteralPath (Join-Path $toolsRoot 'manifest.json') -Raw | ConvertFrom-Json
$tracePackage = $toolsManifest.Packages | Where-Object Package -eq 'jetbrains.dottrace.commandlinetools.windows-x64'
$memoryPackage = $toolsManifest.Packages | Where-Object Package -eq 'jetbrains.dotmemory.console.windows-x64'
$apiPackage = $toolsManifest.Packages | Where-Object Package -eq 'jetbrains.profiler.api'
$trace = Join-Path $tracePackage.Directory 'tools/dottrace.exe'
$memory = Join-Path $memoryPackage.Directory 'tools/dotMemory.exe'
$api = Join-Path $apiPackage.Directory 'lib/netstandard2.0/JetBrains.Profiler.Api.dll'
$apiDependency = Join-Path (Split-Path $api) 'JetBrains.HabitatDetector.dll'
$assembly = Join-Path $Snapshot 'benchmarks/Mpgsql.Benchmarks/bin/Release/net10.0/Mpgsql.Benchmarks.dll'
foreach ($path in @($trace, $memory, $api, $apiDependency, $assembly))
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Missing: $path"
    }
}

if ($PrepareOnly)
{
    $runs = [Collections.Generic.List[object]]::new()
    foreach ($kind in @('trace', 'memory'))
    {
        foreach ($driver in @('mpgsql', 'npgsql'))
        {
            foreach ($batchPath in @('raw', 'facade'))
            {
                $directory = Join-Path $Artifacts ($kind + '/' + $driver + '-' + $batchPath)
                $cohorts = if ($kind -eq 'trace')
                {
                    $TraceCohorts
                }
                else
                {
                    $MemoryCohorts
                }
                $appArgs = @('--query-batch-profile', '--driver', $driver, '--batch-path', $batchPath,
                '--profile-kind', $kind, '--profiler-api', $api, '--cohorts', "$cohorts",
                '--warmup-cohorts', '32', '--artifacts', $directory)
                if ($kind -eq 'trace')
                {
                    $exe = $trace
                    $snapshotFile = Join-Path $directory 'batch.dtp'
                    $arguments = @('start', '--profiling-type=Sampling', '--time-measurement=ThreadTime', '--use-api',
                    '--core-registration=RegistryFree', '--timeout=180s', '--propagate-exit-code', '--no-check-for-updates',
                    ('--save-to=' + $snapshotFile), ('--work-dir=' + $Snapshot), (Get-Command dotnet -CommandType Application).Source,
                    '--', $assembly) + $appArgs
                }
                else
                {
                    $exe = $memory
                    $snapshotFile = Join-Path $directory 'batch.dmw'
                    $arguments = @('start-net-core', '--use-api', '--core-registration=RegistryFree', '--timeout=180s',
                    ('--temp-dir=' + (Join-Path $directory 'temp')), ('--save-to-file=' + $snapshotFile),
                    '--saving-mode=has-snapshot', $assembly, '--') + $appArgs
                }
                $runs.Add([ordered]@{
                    Kind = $kind; Driver = $driver; BatchPath = $batchPath; Cohorts = $cohorts;
                    Directory = $directory; SnapshotFile = $snapshotFile; Executable = $exe; Arguments = $arguments
                })
            }
        }
    }
    [ordered]@{
        PreparedUtc = [DateTimeOffset]::UtcNow; Snapshot = $Snapshot; TimeoutSeconds = $TimeoutSeconds;
        WrapperPath = $PSCommandPath; WrapperSHA256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;
        ChildEnvironment = 'Canonical uppercase names with effective parent values; global environment unchanged';
        BenchmarkAssembly = $assembly; BenchmarkSHA256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash;
        DriverAssemblySHA256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $assembly) 'Mpgsql.dll') -Algorithm SHA256).Hash;
        ClientAssemblySHA256 = $( if (Test-Path -LiteralPath (Join-Path (Split-Path $assembly) 'Mpgsql.Client.dll'))
        {
            (Get-FileHash -LiteralPath (Join-Path (Split-Path $assembly) 'Mpgsql.Client.dll') -Algorithm SHA256).Hash
        } );
        NpgsqlAssemblySHA256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $assembly) 'Npgsql.dll') -Algorithm SHA256).Hash;
        ApiSHA256 = (Get-FileHash -LiteralPath $api -Algorithm SHA256).Hash;
        ApiDependencyPath = $apiDependency; ApiDependencySHA256 = (Get-FileHash -LiteralPath $apiDependency -Algorithm SHA256).Hash;
        Tools = $toolsManifest; Runs = $runs
    } | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $planPath -Encoding utf8
    Write-Output 'Prepared 8 diagnostic runs; no workload was launched.'
    exit 0
}

$plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $plan.WrapperPath -Algorithm SHA256).Hash -ne $plan.WrapperSHA256)
{
    throw 'Prepared wrapper changed.'
}
if ((Get-FileHash -LiteralPath $plan.BenchmarkAssembly -Algorithm SHA256).Hash -ne $plan.BenchmarkSHA256)
{
    throw 'Prepared assembly changed.'
}
$driverHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $plan.BenchmarkAssembly) 'Mpgsql.dll') -Algorithm SHA256).Hash
$clientHash = if (Test-Path -LiteralPath (Join-Path (Split-Path $plan.BenchmarkAssembly) 'Mpgsql.Client.dll'))
{
    (Get-FileHash -LiteralPath (Join-Path (Split-Path $plan.BenchmarkAssembly) 'Mpgsql.Client.dll') -Algorithm SHA256).Hash
}
$npgsqlHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path $plan.BenchmarkAssembly) 'Npgsql.dll') -Algorithm SHA256).Hash
$apiHash = (Get-FileHash -LiteralPath $api -Algorithm SHA256).Hash
if ($driverHash -ne $plan.DriverAssemblySHA256 -or $clientHash -ne $plan.ClientAssemblySHA256 -or $npgsqlHash -ne $plan.NpgsqlAssemblySHA256 -or $apiHash -ne $plan.ApiSHA256)
{
    throw 'Prepared driver or API assembly changed.'
}
if ((Get-FileHash -LiteralPath $plan.ApiDependencyPath -Algorithm SHA256).Hash -ne $plan.ApiDependencySHA256)
{
    throw 'Prepared API dependency changed.'
}
$completed = [Collections.Generic.List[object]]::new()
foreach ($run in $plan.Runs)
{
    if (Test-Path -LiteralPath (Join-Path $run.Directory 'execution.json'))
    {
        throw ('Run already exists; prepare a new artifacts directory: ' + $run.Directory)
    }
    [IO.Directory]::CreateDirectory($run.Directory) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $run.Directory 'temp')) | Out-Null
    $startInfo = [Diagnostics.ProcessStartInfo]::new($run.Executable)
    $startInfo.WorkingDirectory = $plan.Snapshot
    $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true; $startInfo.RedirectStandardError = $true
    # The host can expose both Path and PATH. JetBrains' case-insensitive map
    # rejects duplicates. Canonicalize only this child's environment, keeping
    # the effective Windows value; never modify the user's process/global PATH.
    $childEnvironment = @{ }
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator())
    {
        $childEnvironment[$entry.Key.ToUpperInvariant()] = [Environment]::GetEnvironmentVariable($entry.Key)
    }
    $startInfo.Environment.Clear()
    foreach ($entry in $childEnvironment.GetEnumerator())
    {
        $startInfo.Environment[$entry.Key] = $entry.Value
    }
    foreach ($argument in $run.Arguments)
    {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $startInfo
    $started = [DateTimeOffset]::UtcNow
    $didStart = $false
    Write-Output ('Starting ' + $run.Kind + ' ' + $run.Driver + '/' + $run.BatchPath)
    try
    {
        if (-not $process.Start())
        {
            throw 'Profiler process did not start.'
        }
        $didStart = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($plan.TimeoutSeconds * 1000)
        if ($timedOut)
        {
            $process.Kill($true);$process.WaitForExit()
        }
        [IO.File]::WriteAllText((Join-Path $run.Directory 'stdout.log'),$stdoutTask.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $run.Directory 'stderr.log'),$stderrTask.GetAwaiter().GetResult())
        $code = if ($timedOut)
        {
            124
        }
        else
        {
            $process.ExitCode
        }
        [ordered]@{
            StartUtc = $started; EndUtc = [DateTimeOffset]::UtcNow; ExitCode = $code; TimedOut = $timedOut;
            Executable = $run.Executable; Arguments = $run.Arguments
        } | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $run.Directory 'execution.json') -Encoding utf8
        if ($code -ne 0)
        {
            throw ('Profiler failed: ' + $run.Kind + ' ' + $run.Driver + '/' + $run.BatchPath + ', exit ' + $code)
        }
        $result = Get-Content -LiteralPath (Join-Path $run.Directory 'runner.json') -Raw | ConvertFrom-Json
        if (-not $result.Completed -or $result.Result.Groups -ne $run.Cohorts * 32 -or $result.Result.Queries -ne $run.Cohorts * 32 * 16 -or $result.Result.Syncs -ne $result.Result.Groups -or -not (Test-Path -LiteralPath $run.SnapshotFile -PathType Leaf))
        {
            throw 'Missing or incomplete snapshot/workload result.'
        }
        $completed.Add($run)
        [ordered]@{ Completed = $completed.Count -eq $plan.Runs.Count; Runs = $completed } | ConvertTo-Json -Depth 6 |
                Set-Content -LiteralPath (Join-Path $Artifacts 'execution-summary.json') -Encoding utf8
        Write-Output ('Completed ' + $run.Kind + ' ' + $run.Driver + '/' + $run.BatchPath)
    }
    finally
    {
        if ($didStart -and -not $process.HasExited)
        {
            $process.Kill($true)
        };$process.Dispose()
    }
}
