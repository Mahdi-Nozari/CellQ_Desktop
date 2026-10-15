$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $localDotnet = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
    $dotnetExecutable = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools\cli-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

    # Use separate outputs so a running development window does not lock the checks.
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -Isolated
    if ($LASTEXITCODE -ne 0) { throw 'Build or automated checks failed; no portable package was replaced.' }

    $stage = Join-Path $PSScriptRoot ('artifacts\portable-build\' + [Guid]::NewGuid().ToString('N'))
    $publishDirectory = Join-Path $stage 'publish'
    # Character codes keep the Unicode-path check correct in Windows PowerShell 5.1 too.
    $soloDirectory = Join-Path $stage ('CellQ portable ' + (-join [char[]]@(0x0622, 0x0632, 0x0645, 0x0648, 0x0646)))
    $reportDirectory = Join-Path $stage 'verification'
    New-Item -ItemType Directory -Path $publishDirectory, $soloDirectory, $reportDirectory -Force | Out-Null
    $project = 'src\CellQ.Desktop\CellQ.Desktop.csproj'
    & $dotnetExecutable restore $project --runtime win-x64 --configfile NuGet.Config --packages .tools\packages -p:CellQPortable=true -p:SelfContained=true --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Locked portable restore failed.' }
    & $dotnetExecutable publish $project --configuration Release --runtime win-x64 --self-contained true --no-restore --nologo -p:CellQPortable=true -p:PublishProfile=Portable -p:OutputPath=bin/PortableBuild/ --output $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'CellQ.Desktop.exe') { throw 'Publish must produce exactly one executable, without companion files.' }
    $candidate = Join-Path $soloDirectory 'CellQ.exe'
    Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination $candidate

    # The child has one EXE, an unrelated working directory and no usable DOTNET_ROOT.
    # Verification renders WPF and executes only uploader --help; its transport factory throws.
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $candidate
    $fixture = Join-Path $PSScriptRoot 'tests\Fixtures\pressure-log.csv'
    $start.Arguments = '--verify-package "' + $reportDirectory + '" "' + $fixture + '"'
    $start.WorkingDirectory = $reportDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $stage 'no-installed-runtime'
    $start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $stage 'no-installed-runtime'
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $stage 'runtime-cache'
    $check = New-Object System.Diagnostics.Process
    $check.StartInfo = $start
    try {
        if (-not $check.Start()) { throw 'The single executable could not start.' }
        if (-not $check.WaitForExit(60000)) { $check.Kill(); throw 'The offline package check timed out; no device operation was running.' }
        if ($check.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $reportDirectory 'PASS.txt'))) {
            $failure = Join-Path $reportDirectory 'FAILED.txt'
            if (Test-Path -LiteralPath $failure) { Write-Host (Get-Content -LiteralPath $failure -Raw -Encoding UTF8) }
            throw 'The published executable failed its offline package check.'
        }
    } finally { $check.Dispose() }
    if (@(Get-ChildItem -LiteralPath $soloDirectory -Force).Count -ne 1) { throw 'The app wrote companion files beside the executable.' }

    $distribution = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Path $distribution -Force | Out-Null
    $destination = Join-Path $distribution 'CellQ.exe'
    $replacement = Join-Path $distribution ('.CellQ.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Copy-Item -LiteralPath $candidate -Destination $replacement
        if (Test-Path -LiteralPath $destination) {
            # Atomic replacement preserves the last package if it is still open or replacement fails.
            [System.IO.File]::Replace($replacement, $destination, [NullString]::Value)
        } else { [System.IO.File]::Move($replacement, $destination) }
    } finally { if (Test-Path -LiteralPath $replacement) { Remove-Item -LiteralPath $replacement } }
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $stage 'CellQ.sha256'), $hash + '  CellQ.exe' + "`n", (New-Object System.Text.UTF8Encoding($false)))
    $size = [Math]::Round((Get-Item -LiteralPath $destination).Length / 1MB, 1)
    Write-Host "Portable executable: $destination ($size MB)"
    Write-Host "SHA-256: $hash"
    Write-Host "Verification report and previews: $reportDirectory"
    Write-Host 'Share only dist\CellQ.exe. A clean Windows PC test remains required before wider distribution.'
} finally { Pop-Location }
