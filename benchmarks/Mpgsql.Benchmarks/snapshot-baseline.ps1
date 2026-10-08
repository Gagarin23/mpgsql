$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$snapshotRoot = Join-Path $taskRoot 'artifacts/protocol-baseline'
if (Test-Path -LiteralPath $snapshotRoot)
{
    throw 'The baseline snapshot already exists; it has not been overwritten.'
}
$snapshotSources = Join-Path $snapshotRoot 'Protocol'
New-Item -ItemType Directory -Path $snapshotSources -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src/Mpgsql/Protocol') -Filter '*.cs' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $snapshotSources $_.Name)
}
$projectText = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Mpgsql.ProtocolBaseline</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@
[IO.File]::WriteAllText((Join-Path $snapshotRoot 'Mpgsql.ProtocolBaseline.csproj'), $projectText)
Get-ChildItem -LiteralPath $snapshotSources -Filter '*.cs' -File | Get-FileHash -Algorithm SHA256 |
        Select-Object @{ Name = 'File'; Expression = { Split-Path $_.Path -Leaf } }, Hash |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $snapshotRoot 'source-hashes.json')
dotnet build (Join-Path $snapshotRoot 'Mpgsql.ProtocolBaseline.csproj') -c Release
if ($LASTEXITCODE -ne 0)
{
    throw 'Baseline build failed.'
}
