param(
    [Parameter(Mandatory)][string] $Report,
    [Parameter(Mandatory)][string] $Catalog,
    [Parameter(Mandatory)][string] $Readme,
    [Parameter(Mandatory)][string] $ResultsCsv,
    [Parameter(Mandatory)][string] $Cpu,
    [string] $Date = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$data = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
$coverage = Get-Content -LiteralPath $Catalog -Raw | ConvertFrom-Json
$rows = @($data.Benchmarks)
$expectedCount = $coverage.Profiles.Count * 4
if ($rows.Count -ne $expectedCount) { throw "Expected $expectedCount benchmark results, received $($rows.Count)." }

$lookup = @{}
foreach ($row in $rows) {
    $parameters = @{}
    foreach ($part in ($row.Parameters -split '&')) {
        $pair = $part -split '=', 2
        $parameters[$pair[0]] = $pair[1]
    }
    if ($parameters.ContainsKey('Count') -and $parameters['Count'] -ne '256') {
        throw 'This README snapshot requires the representative Count=256 matrix.'
    }
    # BDN truncates long parameter display strings even in its JSON Parameters
    # field. FullName retains the original string value without that truncation.
    $caseMatch = [regex]::Match($row.FullName, 'Case:\s*"([^"]+)"')
    if (-not $caseMatch.Success) { throw "Missing full profile ID in $($row.FullName)." }
    $key = $caseMatch.Groups[1].Value + '/' + $row.Method
    if ($lookup.ContainsKey($key)) { throw "Duplicate benchmark $key." }
    if ($null -eq $row.Statistics -or $row.Statistics.N -lt 6 -or $row.Statistics.Mean -le 0) {
        throw "Missing or insufficient measurements for $key."
    }
    if ($null -eq $row.Memory.BytesAllocatedPerOperation) { throw "Missing allocation measurement for $key." }
    $lookup[$key] = $row
}

function Number([double] $value, [string] $format) { $value.ToString($format, $culture) }
function Timing($row, [double] $divisor) {
    (Number ($row.Statistics.Mean / $divisor) '0.00') + ' ± ' +
        (Number ($row.Statistics.ConfidenceInterval.Margin / $divisor) '0.00')
}
function Ratio($m, $n) {
    $value = Number ($n.Statistics.Mean / $m.Statistics.Mean) '0.00'
    if ($m.Statistics.ConfidenceInterval.Lower -le $n.Statistics.ConfidenceInterval.Upper -and
        $n.Statistics.ConfidenceInterval.Lower -le $m.Statistics.ConfidenceInterval.Upper) { $value += '†' }
    return $value + '×'
}

$text = [Collections.Generic.List[string]]::new()
$text.Add('<!-- converter-benchmarks:start -->')
$text.Add('## Binary converter benchmarks against Npgsql')
$text.Add('')
$text.Add("Measured on $Date on $Cpu, $($data.HostEnvironmentInfo.OsVersion), $($coverage.Runtime) x64 " +
    "(SDK $($data.HostEnvironmentInfo.DotNetCliVersion)), BenchmarkDotNet $($data.HostEnvironmentInfo.BenchmarkDotNetVersion), Npgsql 10.0.3.")
$text.Add("All $($coverage.ConverterClasses) public converter classes are covered by $($coverage.Profiles.Count) profiles " +
    "and $expectedCount measured scenarios. Before timing, $($coverage.Checks) fixtures passed complete payload, cross-decoding, " +
    'and segmented-read checks, including empty arrays, SIMD boundaries, and NULL patterns.')
$text.Add('')
$text.Add('Each operation handles one scalar or one array of **256 elements**. Nullable arrays contain **12.5% NULLs** ' +
    '(every eighth element). Reads allocate owned results on both sides from the same fully buffered PostgreSQL binary payload. ' +
    'Writes include size determination and encode into the same kind of preallocated `IBufferWriter<byte>`; ' +
    'Npgsql includes its normal `PgWriter` field lifecycle and write-state disposal. ' +
    'Converter resolution, reflection, fixture construction, input buffering, outer field framing, SQL, sockets, and PostgreSQL execution are outside timing.')
$text.Add('')
$text.Add('The harness calls the actual version-pinned [Npgsql converters](https://github.com/npgsql/npgsql/tree/v10.0.3/src/Npgsql/Internal/Converters) ' +
    'with cached typed delegates. Mpgsql uses its public writer and `ReadOnlySequence<byte>` read APIs. ' +
    'These figures describe these API paths, including dispatch and buffer lifecycle costs; they are not end-to-end driver throughput.')
$text.Add('')
$text.Add('Values are deterministic: bytea is 64 bytes; strings contain ASCII and Cyrillic UTF-8; bpchar retains trailing spaces; ' +
    'inet/cidr arrays alternate IPv4 and IPv6. Ordinary numeric has four fractional decimal places; ' +
    '`numeric.wide` is an integer with 128 base-10000 digits. Where representations differ, the table lists **Mpgsql / Npgsql** types. ' +
    'Conversions between those representations happen during setup. In particular, raw `PgNumeric` versus decimal/BigInteger, ' +
    'money cents versus decimal, and `PgInet` versus IPAddress-backed types perform different representation work.')
$text.Add('')
$text.Add('Times are **Mean ± Error** (BenchmarkDotNet 99.9% confidence interval half-width), with 3 warmup and 8 measurement ' +
    'iterations targeting 150 ms each, one process launch, default outlier handling, and the existing machine power policy. ' +
    '**N/M** is Npgsql time divided by Mpgsql time: above 1 means Mpgsql is faster for this fixture. ' +
    '**†** marks overlapping time confidence intervals, so a small apparent difference should not be treated as an established win. ' +
    '**B/op M/N** reports managed bytes allocated per operation by Mpgsql / Npgsql.')
$text.Add('')

$compact = [Collections.Generic.List[object]]::new()
foreach ($shape in @('Scalar', 'Array', 'NullableArray')) {
    $unit = if ($shape -eq 'Scalar') { 'ns' } else { 'µs' }
    $divisor = if ($shape -eq 'Scalar') { 1.0 } else { 1000.0 }
    $title = switch ($shape) { 'Scalar' { 'Scalars' }; 'Array' { 'Arrays without NULLs' }; 'NullableArray' { 'Arrays with NULLs' } }
    foreach ($operation in @('Read', 'Write')) {
        $text.Add("### $title — $($operation.ToLowerInvariant()) ($unit/op)")
        $text.Add('')
        $text.Add('| Profile | Representation M/N | Payload B | Mpgsql | Npgsql | N/M | B/op M/N |')
        $text.Add('|---|---|---:|---:|---:|---:|---:|')
        foreach ($profile in @($coverage.Profiles | Where-Object Shape -EQ $shape)) {
            $m = $lookup[$profile.Id + '/Mpgsql' + $operation]
            $n = $lookup[$profile.Id + '/Npgsql' + $operation]
            if ($null -eq $m -or $null -eq $n) { throw "Missing $operation pair for $($profile.Id)." }
            $mBytes = $m.Memory.BytesAllocatedPerOperation
            $nBytes = $n.Memory.BytesAllocatedPerOperation
            $text.Add('| `' + $profile.Id + '` | `' + $profile.Representation + '` | ' + $profile.PayloadLength + ' | ' +
                (Timing $m $divisor) + ' | ' + (Timing $n $divisor) + ' | ' + (Ratio $m $n) + ' | ' + $mBytes + ' / ' + $nBytes + ' |')
            $compact.Add([pscustomobject]@{ Date = $Date; Cpu = $Cpu; Runtime = $coverage.Runtime; NpgsqlVersion = '10.0.3';
                Profile = $profile.Id; PostgreSqlType = $profile.PostgreSqlType;
                Shape = $shape; Operation = $operation; Count = $(if ($shape -eq 'Scalar') { 1 } else { 256 });
                NullPercent = $(if ($shape -eq 'NullableArray') { '12.5' } else { '0' }); PayloadBytes = $profile.PayloadLength;
                Representation = $profile.Representation; MpgsqlMeanNs = Number $m.Statistics.Mean 'R';
                MpgsqlErrorNs = Number $m.Statistics.ConfidenceInterval.Margin 'R'; NpgsqlMeanNs = Number $n.Statistics.Mean 'R';
                NpgsqlErrorNs = Number $n.Statistics.ConfidenceInterval.Margin 'R';
                NpgsqlOverMpgsql = Number ($n.Statistics.Mean / $m.Statistics.Mean) 'R';
                MpgsqlAllocatedBytes = $mBytes; NpgsqlAllocatedBytes = $nBytes;
                MpgsqlSamples = $m.Statistics.N; NpgsqlSamples = $n.Statistics.N })
        }
        $text.Add('')
    }
}
$text.Add('Reproduction commands and the result-table generator are in ' +
    '[the converter benchmark guide](../../benchmarks/Mpgsql.Benchmarks/Converters/README.md). ' +
    "The [machine-readable snapshot](../../benchmarks/Mpgsql.Benchmarks/Converters/results-$Date.csv) retains unrounded means, errors, and allocations. " +
    'Full measurement JSON, CSV, logs, coverage metadata, and assembly hashes are saved locally under `artifacts/converter-comparison/`.')
$text.Add('<!-- converter-benchmarks:end -->')

$section = $text -join "`n"
$original = Get-Content -LiteralPath $Readme -Raw
$start = $original.IndexOf('<!-- converter-benchmarks:start -->', [StringComparison]::Ordinal)
$end = $original.IndexOf('<!-- converter-benchmarks:end -->', [StringComparison]::Ordinal)
if (($start -lt 0) -ne ($end -lt 0)) { throw 'README has an incomplete benchmark section marker.' }
if ($start -ge 0) {
    $end += '<!-- converter-benchmarks:end -->'.Length
    $updated = $original.Substring(0, $start) + $section + $original.Substring($end)
} else { $updated = $original.TrimEnd() + "`n`n" + $section + "`n" }
$compact | Export-Csv -LiteralPath $ResultsCsv -NoTypeInformation -UseQuotes AsNeeded -Encoding utf8
[IO.File]::WriteAllText((Resolve-Path -LiteralPath $Readme).Path, $updated, [Text.UTF8Encoding]::new($false))
Write-Output "Wrote $($compact.Count) comparison rows ($expectedCount measurements) to README and CSV."
