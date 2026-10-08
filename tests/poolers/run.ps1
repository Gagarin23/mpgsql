param([switch]$KeepRunning)

$ErrorActionPreference = 'Stop'
$mpgsqlRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$mpgsqlConfig = Join-Path $PSScriptRoot 'compose.yaml'
$mpgsqlProject = Join-Path $mpgsqlRepo 'tests/Mpgsql.IntegrationTests'
$mpgsqlVariables = @('MPGSQL_TEST_HOST', 'MPGSQL_TEST_PORT', 'MPGSQL_TEST_USER', 'MPGSQL_TEST_PASSWORD',
    'MPGSQL_TEST_DATABASE', 'MPGSQL_TEST_UPPER_ONLY', 'MPGSQL_TEST_POOL_MODE', 'MPGSQL_TEST_ACTIVE_TERMINAL_EOF_ONLY')
$mpgsqlSaved = @{}
foreach ($mpgsqlName in $mpgsqlVariables) { $mpgsqlSaved[$mpgsqlName] = [Environment]::GetEnvironmentVariable($mpgsqlName) }

try {
    docker compose -f $mpgsqlConfig up -d --wait --wait-timeout 60
    if ($LASTEXITCODE -ne 0) { throw 'The pooler test matrix did not start.' }
    docker compose -f $mpgsqlConfig exec -T postgres postgres --version
    docker compose -f $mpgsqlConfig exec -T pgbouncer-session pgbouncer --version
    docker compose -f $mpgsqlConfig exec -T doorman-session pg_doorman --version
    $env:MPGSQL_TEST_HOST = '127.0.0.1'
    $env:MPGSQL_TEST_USER = 'mpgsql'
    $env:MPGSQL_TEST_PASSWORD = 'mpgsql-local-only'
    $env:MPGSQL_TEST_DATABASE = 'mpgsql'
    $env:MPGSQL_TEST_UPPER_ONLY = '1'
    foreach ($mpgsqlEndpoint in @(
        @{Name='PostgreSQL'; Port=16430; Mode='session'},
        @{Name='PgBouncer session'; Port=16431; Mode='session'},
        @{Name='PgBouncer transaction'; Port=16432; Mode='transaction'},
        @{Name='pg_doorman session'; Port=16433; Mode='session'; ActiveTerminalEofOnly=$true},
        @{Name='pg_doorman transaction'; Port=16434; Mode='transaction'; ActiveTerminalEofOnly=$true}
    )) {
        Write-Host ('Checking ' + $mpgsqlEndpoint.Name)
        $env:MPGSQL_TEST_PORT = [string]$mpgsqlEndpoint.Port
        $env:MPGSQL_TEST_POOL_MODE = $mpgsqlEndpoint.Mode
        # pg_doorman 3.10.6 forwards no FATAL for an active self-termination; idle gets its own FATAL.
        $env:MPGSQL_TEST_ACTIVE_TERMINAL_EOF_ONLY = if ($mpgsqlEndpoint.ActiveTerminalEofOnly) { '1' } else { '0' }
        dotnet run --project $mpgsqlProject -c Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw ('Integration checks failed: ' + $mpgsqlEndpoint.Name) }
    }
}
finally {
    foreach ($mpgsqlName in $mpgsqlVariables) { [Environment]::SetEnvironmentVariable($mpgsqlName, $mpgsqlSaved[$mpgsqlName]) }
    if (!$KeepRunning) { docker compose -f $mpgsqlConfig down -v }
}
