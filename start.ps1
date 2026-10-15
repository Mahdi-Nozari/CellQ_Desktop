param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArguments)
$ErrorActionPreference = 'Stop'
$appExecutable = Join-Path $PSScriptRoot 'dist\CellQ.exe'
if (-not (Test-Path -LiteralPath $appExecutable)) { throw 'Run publish.cmd first.' }
& $appExecutable @AppArguments
