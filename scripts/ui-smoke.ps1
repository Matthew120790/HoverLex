$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot ('artifacts/ui-integration-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$lines = New-Object 'Collections.Generic.List[string]'
function Assert-Check($condition,$description) {
    if (-not $condition) { throw ('FAIL ' + $description) }
    $script:lines.Add('PASS ' + $description)
}
function Run-Hidden($executable,$arguments) {
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Assert-Check ($process.ExitCode -eq 0) ('process completed: ' + [IO.Path]::GetFileName($executable))
}
$setup = Join-Path $projectRoot 'release/HoverLex-Setup-v0.3.0.exe'
$oldFixture = Join-Path $projectRoot 'artifacts/installer-test-20261004-120730/Installed App'
$oldRoot = Join-Path $testRoot 'Old App'
$oldDesktop = Join-Path $testRoot 'Old Desktop'
Copy-Item -LiteralPath $oldFixture -Destination $oldRoot -Recurse
New-Item -ItemType Directory -Path $oldDesktop -Force | Out-Null
$oldLauncher = Join-Path $oldRoot 'HoverLexLauncher.exe'
Assert-Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($oldLauncher).FileVersion -eq '0.2.0.0') 'real v0.2.0 launcher fixture'
$words = Join-Path $oldRoot 'UserData/words.json'
$before = (Get-FileHash -LiteralPath $words -Algorithm SHA256).Hash
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path $oldDesktop 'HoverLex.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $oldLauncher; $shortcut.WorkingDirectory = $oldRoot; $shortcut.IconLocation = $oldLauncher + ',0'; $shortcut.Save()
Run-Hidden $oldLauncher @('--check-only', ('"' + $oldRoot + '"'))
$current = Get-Content -LiteralPath (Join-Path $oldRoot 'current.json') -Raw | ConvertFrom-Json
Assert-Check ($current.Version -eq '0.3.0') 'old launcher updates to redesigned v0.3.0'
Assert-Check ((Get-FileHash -LiteralPath $words -Algorithm SHA256).Hash -eq $before) 'old vocabulary unchanged after real upgrade'
Run-Hidden $setup @('--test-install','--target',('"' + $oldRoot + '"'),'--desktop',('"' + $oldDesktop + '"'))
Assert-Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($oldLauncher).FileVersion -eq '0.3.0.0') 'reinstallation refreshes old launcher appearance'
$owned = @(Get-ChildItem -LiteralPath $oldDesktop -Filter '*.lnk' | Where-Object { $_.Name -ne 'HoverLex.lnk' })
Assert-Check ($owned.Count -eq 1) 'installer creates one localized desktop shortcut'
$link = $shell.CreateShortcut($owned[0].FullName)
Assert-Check ($link.TargetPath -eq $oldLauncher) 'shortcut preserves fixed launcher target'
Assert-Check ($link.IconLocation -like '*versions*HoverLex.exe*') 'shortcut uses redesigned version icon'
$icon = $link.IconLocation
Run-Hidden $setup @('--test-install','--target',('"' + $oldRoot + '"'),'--desktop',('"' + $oldDesktop + '"'))
Assert-Check (@(Get-ChildItem -LiteralPath $oldDesktop -Filter '*.lnk').Count -eq 2) 'repeat installation keeps shortcuts without duplicates'
Assert-Check ((Get-FileHash -LiteralPath $words -Algorithm SHA256).Hash -eq $before) 'repeat installation preserves vocabulary'
$fresh = Join-Path $testRoot 'Fresh App'; $freshDesktop = Join-Path $testRoot 'Fresh Desktop'
Run-Hidden $setup @('--test-install','--target',('"' + $fresh + '"'),'--desktop',('"' + $freshDesktop + '"'))
$freshLauncher = Join-Path $fresh 'HoverLexLauncher.exe'
Assert-Check ([Diagnostics.FileVersionInfo]::GetVersionInfo($freshLauncher).FileVersion -eq '0.3.0.0') 'fresh install includes redesigned launcher'
Run-Hidden $freshLauncher @('--check-only',('"' + $fresh + '"'))
$package = Join-Path $projectRoot 'release/HoverLex-Windows-x64-v0.3.0.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try { Assert-Check (@($zip.Entries | Where-Object { $_.FullName -match '(private|UserData|research|preview)' }).Count -eq 0) 'release contains no private keys, vocabulary or test previews' }
finally { $zip.Dispose() }
[IO.File]::WriteAllLines((Join-Path $testRoot 'ui-smoke.txt'),$lines,(New-Object Text.UTF8Encoding($false)))
$lines
Write-Host ('Validation artifacts: ' + $testRoot)
