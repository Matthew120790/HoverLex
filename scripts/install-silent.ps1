param(
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'HoverLex'),
    [string]$Desktop = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$StartMenu = (Join-Path ([Environment]::GetFolderPath('Programs')) 'HoverLex'),
    [switch]$AuditOnly,
    [switch]$SkipIconRefresh,
    [switch]$RestartExplorer
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$Target = [IO.Path]::GetFullPath($Target)
$Desktop = [IO.Path]::GetFullPath($Desktop)
$StartMenu = [IO.Path]::GetFullPath($StartMenu)
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
$installer = Join-Path $projectRoot ('release/HoverLex-Setup-v' + $version + '.exe')
$reportRoot = Join-Path $projectRoot ('artifacts/install-audit-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
function Get-DataSnapshot {
    $data = Join-Path $Target 'UserData'
    if (Test-Path -LiteralPath $data) {
        Get-ChildItem -LiteralPath $data -File | ForEach-Object {
            [pscustomobject]@{Name=$_.Name;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        }
    }
}
function Get-InstallationState {
    $pointer = Join-Path $Target 'current.json'
    $launcher = Join-Path $Target 'HoverLexLauncher.exe'
    $result = [ordered]@{Target=$Target;Version=$null;Main=$null;MainVersion=$null;MainHashValid=$false;Launcher=$launcher;LauncherVersion=$null}
    if (Test-Path -LiteralPath $launcher) { $result.LauncherVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($launcher).FileVersion }
    if (Test-Path -LiteralPath $pointer) {
        $state = Get-Content -LiteralPath $pointer -Raw -Encoding UTF8 | ConvertFrom-Json
        $main = [IO.Path]::GetFullPath((Join-Path (Join-Path $Target $state.Directory) 'HoverLex.exe'))
        if (-not $main.StartsWith($Target.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Installed version path escaped the installation directory.' }
        $result.Version = $state.Version; $result.Main = $main
        if (Test-Path -LiteralPath $main) {
            $result.MainVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($main).FileVersion
            $result.MainHashValid = (Get-FileHash -LiteralPath $main -Algorithm SHA256).Hash -eq $state.AppSha256
        }
    }
    [pscustomobject]$result
}
function Get-ProductShortcuts {
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($location in @(@{Kind='Desktop';Folder=$Desktop},@{Kind='StartMenu';Folder=$StartMenu})) {
            if (-not (Test-Path -LiteralPath $location.Folder)) { continue }
            foreach ($file in @(Get-ChildItem -LiteralPath $location.Folder -Filter '*.lnk' -File)) {
                $link = $shell.CreateShortcut($file.FullName)
                try {
                    $targetPath = [string]$link.TargetPath
                    if ($file.Name -like '*HoverLex*' -or $targetPath.StartsWith($Target.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) {
                        [pscustomobject]@{Kind=$location.Kind;Path=$file.FullName;Target=$targetPath;Icon=[string]$link.IconLocation;WorkingDirectory=[string]$link.WorkingDirectory}
                    }
                } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
            }
        }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
}
$before = @(Get-DataSnapshot)
$beforeState = Get-InstallationState
$beforeLinks = @(Get-ProductShortcuts)
$beforeReport = [pscustomobject]@{AuditOnly=[bool]$AuditOnly;Installation=$beforeState;Data=$before;Shortcuts=$beforeLinks}
[IO.File]::WriteAllText((Join-Path $reportRoot 'before.json'),($beforeReport | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
if ($AuditOnly) {
    $beforeReport | ConvertTo-Json -Depth 8
    Write-Host ('Audit saved: ' + $reportRoot)
    return
}
if (-not (Test-Path -LiteralPath $installer)) { throw ('Installer missing: ' + $installer) }
if ($before.Count -gt 0) {
    $backup = Join-Path $reportRoot 'UserData-backup'
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($item in $before) { Copy-Item -LiteralPath (Join-Path (Join-Path $Target 'UserData') $item.Name) -Destination (Join-Path $backup $item.Name) }
}
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','--target',('"' + $Target + '"'),'--desktop',('"' + $Desktop + '"'),'--start-menu',('"' + $StartMenu + '"'),'--log',('"' + (Join-Path $reportRoot 'installer.json') + '"'))
$process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw ('Silent installation failed with exit code ' + $process.ExitCode + '. Details: ' + $reportRoot) }
$after = @(Get-DataSnapshot)
$dataPreserved = $true
foreach ($item in $before) {
    $match = @($after | Where-Object { $_.Name -eq $item.Name -and $_.Sha256 -eq $item.Sha256 })
    if ($match.Count -ne 1) { $dataPreserved = $false }
}
$afterState = Get-InstallationState
$afterLinks = @(Get-ProductShortcuts)
$expectedLauncher = Join-Path $Target 'HoverLexLauncher.exe'
$linksValid = $true
foreach ($kind in @('Desktop','StartMenu')) {
    $owned = @($afterLinks | Where-Object { $_.Kind -eq $kind -and $_.Target -eq $expectedLauncher })
    if ($owned.Count -eq 0) { $linksValid = $false }
    foreach ($link in $owned) { if ($link.Icon -ne ($afterState.Main + ',0')) { $linksValid = $false } }
}
$versionsValid = $afterState.Version -eq $version -and $afterState.MainVersion -eq ($version + '.0') -and $afterState.LauncherVersion -eq ($version + '.0') -and $afterState.MainHashValid
$report = [pscustomobject]@{Success=($dataPreserved -and $linksValid -and $versionsValid);DataPreserved=$dataPreserved;ShortcutsValid=$linksValid;VersionsValid=$versionsValid;Installation=$afterState;DataBefore=$before;DataAfter=$after;Shortcuts=$afterLinks}
[IO.File]::WriteAllText((Join-Path $reportRoot 'after.json'),($report | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
if (-not $report.Success) { throw ('Installation verification failed; inspect ' + $reportRoot) }
if (-not $SkipIconRefresh) {
    $refresh = Join-Path $env:WINDIR 'System32/ie4uinit.exe'
    if (Test-Path -LiteralPath $refresh) {
        $refreshProcess = Start-Process -FilePath $refresh -ArgumentList '-show' -WindowStyle Hidden -Wait -PassThru
        if ($refreshProcess.ExitCode -ne 0) { Write-Warning 'Icon refresh did not complete.' }
    }
}
if ($RestartExplorer) {
    & (Join-Path $env:WINDIR 'System32/taskkill.exe') /f /im explorer.exe
    if ($LASTEXITCODE -ne 0) { throw 'Explorer restart was not completed.' }
    Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -WindowStyle Hidden
}
$report | ConvertTo-Json -Depth 8
Write-Host ('Installation verified. Backup and report: ' + $reportRoot)
