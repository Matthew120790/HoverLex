param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $projectRoot 'build.ps1') -Test:$Test
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
$artifacts = Join-Path $projectRoot 'artifacts/desktop-build'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$keyBootstrap = Join-Path $artifacts 'key-bootstrap.json'
[IO.File]::WriteAllText($keyBootstrap, '{}')
& (Join-Path $PSScriptRoot 'sign-release.ps1') -PayloadPath $keyBootstrap -OutputPath (Join-Path $artifacts 'key-bootstrap.signed.json')
$channelFile = Join-Path $projectRoot 'dist/HoverLex/update-channel.json'
$defaultChannel = @{ Source = (Join-Path $projectRoot 'updates/latest.json') } | ConvertTo-Json -Compress
[IO.File]::WriteAllText($channelFile, $defaultChannel, (New-Object Text.UTF8Encoding($false)))
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')))
$common += '/win32icon:' + (Join-Path $projectRoot 'assets/HoverLex.ico')
$common += '/resource:' + (Join-Path $projectRoot 'assets/HoverLex.ico') + ',hoverlex-icon'
$common += '/resource:' + (Join-Path $projectRoot 'assets/HoverLex-mark.png') + ',hoverlex-mark'
$common += (Join-Path $projectRoot 'src/Design.cs')
foreach ($name in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')) { $common += '/reference:' + (Join-Path $framework $name) }
$common += '/resource:' + (Join-Path $projectRoot '.release-signing/public.xml') + ',update-public-key'
$common += (Join-Path $projectRoot 'desktop/UpdateCore.cs')
$common += (Join-Path $projectRoot 'artifacts/build/VersionInfo.cs')
$launcher = Join-Path $projectRoot 'dist/HoverLex/HoverLexLauncher.exe'
$launcherArgs = @(('/out:' + $launcher)) + $common + @(('/resource:' + $channelFile + ',default-channel'), (Join-Path $projectRoot 'desktop/Launcher.cs'), (Join-Path $projectRoot 'desktop/UpdaterTests.cs'))
& $compiler $launcherArgs
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'HoverLex.exe.config') -Destination (Join-Path $projectRoot 'dist/HoverLex/HoverLexLauncher.exe.config')
& python (Join-Path $PSScriptRoot 'package.py')
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
$payloadPath = Join-Path $projectRoot 'updates/release-payload.json'
& (Join-Path $PSScriptRoot 'sign-release.ps1') -PayloadPath $payloadPath -OutputPath (Join-Path $projectRoot 'updates/latest.json')
$setup = Join-Path $projectRoot ('release/HoverLex-Setup-v' + $version + '.exe')
$packageInfo = Get-Content -LiteralPath $payloadPath -Raw | ConvertFrom-Json
$package = Join-Path $projectRoot ('updates/' + $packageInfo.Package)
$migration = Join-Path $artifacts 'migration-source.txt'
[IO.File]::WriteAllText($migration, (Join-Path $projectRoot 'dist/HoverLex/UserData'), (New-Object Text.UTF8Encoding($false)))
$installerArgs = @(('/out:' + $setup)) + $common + @(('/resource:' + $package + ',initial-package'), ('/resource:' + (Join-Path $projectRoot 'updates/latest.json') + ',initial-release'), ('/resource:' + $channelFile + ',default-channel'), ('/resource:' + $migration + ',migration-source'), (Join-Path $projectRoot 'desktop/Installer.cs'))
& $compiler $installerArgs
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
if ($Test) {
    $testRoot = Join-Path $projectRoot ('artifacts/updater-tests-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $testArgs = @('--self-test', ('"' + $testRoot + '"'), ('"' + (Join-Path $projectRoot '.release-signing/private.xml') + '"'))
    $process = Start-Process -FilePath $launcher -ArgumentList $testArgs -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $testRoot 'updater-tests.txt')
    if ($process.ExitCode -ne 0) { throw 'Updater tests failed.' }
}
Write-Host ('Installer ready: ' + $setup)
