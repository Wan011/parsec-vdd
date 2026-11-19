param([string]$Output = (Join-Path $PSScriptRoot '../artifacts'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$Output = [IO.Path]::GetFullPath($Output)
foreach ($project in @('tests/HeartbeatTests.csproj', 'app/ParsecDisplay.csproj')) {
    & dotnet restore (Join-Path $root $project)
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
}
$tests = Join-Path $Output 'tests'
& dotnet build (Join-Path $root 'tests/HeartbeatTests.csproj') -c Release --no-restore -o $tests
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& (Join-Path $tests 'HeartbeatTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Driver simulation tests failed' }
$app = Join-Path $Output 'app'
& dotnet build (Join-Path $root 'app/ParsecDisplay.csproj') -c Release --no-restore -o $app
if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
$exe = Join-Path $app 'ParsecDisplay.exe'
Get-FileHash -LiteralPath $exe -Algorithm SHA256
