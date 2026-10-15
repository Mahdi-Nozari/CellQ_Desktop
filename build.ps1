param([switch]$SkipUiChecks, [switch]$Isolated)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $localDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
    $dotnetExecutable = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    & $dotnetExecutable restore CellQ.Desktop.sln --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --packages .tools\packages --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    $buildArguments = @('build', 'CellQ.Desktop.sln', '--configuration', 'Release', '--no-restore', '--nologo')
    if ($Isolated) { $buildArguments += '-p:OutputPath=bin/BuildChecks/' }
    & $dotnetExecutable @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $protocolChecks = if ($Isolated) { 'tests\CellQ.Protocol.Tests\bin\BuildChecks\CellQ.Protocol.Tests.dll' } else { 'tests\CellQ.Protocol.Tests\bin\Release\net10.0\CellQ.Protocol.Tests.dll' }
    & $dotnetExecutable $protocolChecks
    if ($LASTEXITCODE -ne 0) { throw 'Protocol checks failed.' }
    if (-not $SkipUiChecks) {
        $uiChecks = if ($Isolated) { 'tools\CellQ.UiChecks\bin\BuildChecks\CellQ.UiChecks.dll' } else { 'tools\CellQ.UiChecks\bin\Release\net10.0-windows\CellQ.UiChecks.dll' }
        & $dotnetExecutable $uiChecks artifacts\ui-checks
        if ($LASTEXITCODE -ne 0) { throw 'UI checks failed.' }
    }
} finally { Pop-Location }
