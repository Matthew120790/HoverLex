$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
$setup = Join-Path $projectRoot ('release/HoverLex-Setup-v' + $version + '.exe')
$testRoot = Join-Path $projectRoot ('artifacts/silent-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results = New-Object 'Collections.Generic.List[string]'
function Check($condition,$message) {
    if (-not $condition) { throw ('FAIL ' + $message) }
    $script:results.Add('PASS ' + $message)
}
function Invoke-Installer($arguments) {
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $process.ExitCode
}
$fresh = Join-Path $testRoot 'Fresh App'; $desktop = Join-Path $testRoot 'Desktop'; $menu = Join-Path $testRoot 'Start Menu'
& (Join-Path $PSScriptRoot 'install-silent.ps1') -Target $fresh -Desktop $desktop -StartMenu $menu -SkipIconRefresh | Out-Null
Check (Test-Path -LiteralPath (Join-Path $fresh 'HoverLexLauncher.exe')) 'VERYSILENT installs without a UI'
$shell = New-Object -ComObject WScript.Shell
foreach ($folder in @($desktop,$menu)) {
    $links = @(Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File)
    Check ($links.Count -eq 1) ('one shortcut in ' + [IO.Path]::GetFileName($folder))
    $link = $shell.CreateShortcut($links[0].FullName)
    Check ($link.TargetPath -eq (Join-Path $fresh 'HoverLexLauncher.exe')) 'shortcut keeps automatic update launcher'
    Check ($link.IconLocation -like '*versions*HoverLex.exe*') 'shortcut uses latest app icon'
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null
}
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
$words = Join-Path $fresh 'UserData/words.json'
[IO.File]::WriteAllText($words,'[{"Word":"curiosity","Meaning":"preserve existing vocabulary","Context":"keep context"}]',(New-Object Text.UTF8Encoding($false)))
$hash = (Get-FileHash -LiteralPath $words -Algorithm SHA256).Hash
& (Join-Path $PSScriptRoot 'install-silent.ps1') -Target $fresh -Desktop $desktop -StartMenu $menu -SkipIconRefresh | Out-Null
Check ((Get-FileHash -LiteralPath $words -Algorithm SHA256).Hash -eq $hash) 'repeat silent install leaves vocabulary byte-for-byte unchanged'
Check (@(Get-ChildItem -LiteralPath $desktop -Filter '*.lnk' -File).Count -eq 1) 'repeat install keeps one desktop shortcut'
Check (@(Get-ChildItem -LiteralPath $menu -Filter '*.lnk' -File).Count -eq 1) 'repeat install keeps one Start menu shortcut'
$current = Get-Content -LiteralPath (Join-Path $fresh 'current.json') -Raw | ConvertFrom-Json
$versionRoot = Join-Path $fresh $current.Directory
Check ((Get-FileHash -LiteralPath (Join-Path $versionRoot 'EnglishProofreader.zip') -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'assets/EnglishProofreader.zip') -Algorithm SHA256).Hash) 'installed proofreading component matches the build'
$shell = New-Object -ComObject WScript.Shell
$extraPath = Join-Path $desktop 'HoverLex extra.lnk'
$extra = $shell.CreateShortcut($extraPath)
$extra.TargetPath = Join-Path $fresh 'HoverLexLauncher.exe'; $extra.Arguments = '--fixture'; $extra.IconLocation = (Join-Path $fresh 'old-icon.exe') + ',0'; $extra.Save()
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($extra) | Out-Null
$otherPath = Join-Path $desktop 'Unrelated.lnk'
$other = $shell.CreateShortcut($otherPath); $other.TargetPath = Join-Path $env:WINDIR 'System32/notepad.exe'; $other.IconLocation = $other.TargetPath + ',0'; $other.Save()
$otherHash = (Get-FileHash -LiteralPath $otherPath -Algorithm SHA256).Hash
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($other) | Out-Null
& (Join-Path $PSScriptRoot 'install-silent.ps1') -Target $fresh -Desktop $desktop -StartMenu $menu -SkipIconRefresh | Out-Null
$extra = $shell.CreateShortcut($extraPath)
Check ($extra.IconLocation -eq ((Join-Path $versionRoot 'HoverLex.exe') + ',0') -and $extra.Arguments -eq '--fixture') 'additional product shortcuts refresh icons and retain arguments'
Check ((Get-FileHash -LiteralPath $otherPath -Algorithm SHA256).Hash -eq $otherHash) 'unrelated shortcuts remain unchanged'
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($extra) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
$aliasRoot = Join-Path $testRoot 'S Alias App'; $aliasDesktop = Join-Path $testRoot 'S Desktop'; $aliasMenu = Join-Path $testRoot 'S Menu'; $aliasLog = Join-Path $testRoot 'S-install.json'
$aliasExit = Invoke-Installer @('/S','--target',('"' + $aliasRoot + '"'),'--desktop',('"' + $aliasDesktop + '"'),'--start-menu',('"' + $aliasMenu + '"'),'--log',('"' + $aliasLog + '"'))
Check ($aliasExit -eq 0 -and (Test-Path -LiteralPath (Join-Path $aliasRoot 'current.json'))) 'S alias installs silently'
Check ((Get-Content -LiteralPath $aliasLog -Raw | ConvertFrom-Json).Success) 'silent installation writes a success log'
$invalidRoot = Join-Path $testRoot 'Invalid App'; $invalidLog = Join-Path $testRoot 'invalid.json'
$invalidExit = Invoke-Installer @('/VERYSILENT','--log',('"' + $invalidLog + '"'),'--target',('"' + $invalidRoot + '"'))
Check ($invalidExit -eq 2 -and -not (Test-Path -LiteralPath $invalidRoot)) 'partial path overrides fail before installation'
$unknownExit = Invoke-Installer @('/VERYSILENT','--log',('"' + $invalidLog + '"'),'--unknown')
Check ($unknownExit -eq 2) 'unknown arguments return exit code 2 without showing dialogs'
Check (-not (Get-Content -LiteralPath $invalidLog -Raw | ConvertFrom-Json).Success) 'argument failures write a failure log'
$missingValue = Invoke-Installer @('/VERYSILENT','--target')
Check ($missingValue -eq 2) 'missing path returns exit code 2 without showing dialogs'
[IO.File]::WriteAllLines((Join-Path $testRoot 'silent-tests.txt'),$results,(New-Object Text.UTF8Encoding($false)))
$results
Write-Host ('Silent test artifacts: ' + $testRoot)
