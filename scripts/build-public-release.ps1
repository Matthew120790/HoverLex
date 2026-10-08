param(
    [string]$Repository = 'Matthew120790/HoverLex',
    [string]$OutputDirectory,
    [switch]$Test,
    [switch]$PackageOnly,
    [string]$PublicKeyPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/public-release-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new, empty output directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
& (Join-Path $projectRoot 'build.ps1') -Test:$Test
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
$app = Join-Path $output 'HoverLex'
New-Item -ItemType Directory -Path $app | Out-Null
foreach ($name in @('HoverLex.exe','HoverLex.exe.config','EnglishProofreader.zip','README.md','THIRD-PARTY-NOTICES.md','LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('dist/HoverLex/' + $name)) -Destination $app
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'dist/HoverLex/Dictionary') -Destination $app -Recurse
# 包内的说明可以独立查看，不依赖源码仓库的相对图片路径。
$readme = Join-Path $app 'README.md'
$readmeText = [IO.File]::ReadAllText($readme).Replace('](docs/images/main-window.png)','](https://raw.githubusercontent.com/' + $Repository + '/main/docs/images/main-window.png)')
[IO.File]::WriteAllText($readme,$readmeText,(New-Object Text.UTF8Encoding($false)))
$channel = Join-Path $app 'update-channel.json'
[IO.File]::WriteAllText($channel,(@{Source=('https://github.com/' + $Repository + '/releases/latest/download/latest.json')} | ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))
if ($PackageOnly -and -not $PublicKeyPath) { throw 'PackageOnly requires a trusted public key.' }
if (-not $PublicKeyPath) {
    $bootstrap = Join-Path $output 'bootstrap.json'
    [IO.File]::WriteAllText($bootstrap,'{}')
    & (Join-Path $PSScriptRoot 'sign-release.ps1') -PayloadPath $bootstrap -OutputPath (Join-Path $output 'bootstrap.signed.json')
    $PublicKeyPath = Join-Path $projectRoot '.release-signing/public.xml'
}
$PublicKeyPath = (Resolve-Path -LiteralPath $PublicKeyPath).Path
if ([IO.File]::ReadAllText($PublicKeyPath) -match '<D>') { throw 'Only a public key may be embedded.' }
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$common = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001','/utf8output',('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')))
$common += '/win32icon:' + (Join-Path $projectRoot 'assets/HoverLex.ico')
$common += '/resource:' + (Join-Path $projectRoot 'assets/HoverLex.ico') + ',hoverlex-icon'
$common += '/resource:' + (Join-Path $projectRoot 'assets/HoverLex-mark.png') + ',hoverlex-mark'
foreach ($name in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')) { $common += '/reference:' + (Join-Path $framework $name) }
$common += (Join-Path $projectRoot 'src/Design.cs')
$common += (Join-Path $projectRoot 'desktop/UpdateCore.cs')
$common += (Join-Path $projectRoot 'artifacts/build/VersionInfo.cs')
$common += '/resource:' + $PublicKeyPath + ',update-public-key'
$launcher = Join-Path $app 'HoverLexLauncher.exe'
& $compiler (@(('/out:' + $launcher)) + $common + @(('/resource:' + $channel + ',default-channel'),(Join-Path $projectRoot 'desktop/Launcher.cs'),(Join-Path $projectRoot 'desktop/UpdaterTests.cs')))
if ($LASTEXITCODE -ne 0) { throw 'Public launcher build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'HoverLex.exe.config') -Destination (Join-Path $app 'HoverLexLauncher.exe.config')
$packageName = 'HoverLex-Windows-x64-v' + $version + '.zip'
$package = Join-Path $output $packageName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app,$package,[IO.Compression.CompressionLevel]::Optimal,$true)
$payload = Join-Path $output 'release-payload.json'
$release = @{Version=$version;Revision=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();Package=('https://github.com/' + $Repository + '/releases/download/v' + $version + '/' + $packageName);Size=(Get-Item -LiteralPath $package).Length;Sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant();AppSha256=(Get-FileHash -LiteralPath (Join-Path $app 'HoverLex.exe') -Algorithm SHA256).Hash.ToLowerInvariant()}
[IO.File]::WriteAllText($payload,($release | ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))
if ($PackageOnly) {
    Write-Host ('Package ready for local signing: ' + $output)
    return
}
$manifest = Join-Path $output 'latest.json'
& (Join-Path $PSScriptRoot 'sign-release.ps1') -PayloadPath $payload -OutputPath $manifest
$migration = Join-Path $output 'migration-source.txt'
[IO.File]::WriteAllText($migration,'',(New-Object Text.UTF8Encoding($false)))
$setup = Join-Path $output ('HoverLex-Setup-v' + $version + '.exe')
& $compiler (@(('/out:' + $setup)) + $common + @(('/resource:' + $package + ',initial-package'),('/resource:' + $manifest + ',initial-release'),('/resource:' + $channel + ',default-channel'),('/resource:' + $migration + ',migration-source'),(Join-Path $projectRoot 'desktop/Installer.cs')))
if ($LASTEXITCODE -ne 0) { throw 'Public installer build failed.' }
$checksums = @($setup,$package,$manifest) | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) }
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'),$checksums,(New-Object Text.UTF8Encoding($false)))
if ($Test) {
    $target = Join-Path $output 'test-install'
    & (Join-Path $PSScriptRoot 'install-silent.ps1') -InstallerPath $setup -Target $target -Desktop (Join-Path $output 'test-desktop') -StartMenu (Join-Path $output 'test-start-menu') -SkipIconRefresh | Out-Null
    $installedChannel = Get-Content -LiteralPath (Join-Path $target 'update-channel.json') -Raw | ConvertFrom-Json
    if ($installedChannel.Source -ne ('https://github.com/' + $Repository + '/releases/latest/download/latest.json')) { throw 'Public update channel verification failed.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $target 'UserData') -File).Count -ne 0) { throw 'Public installation unexpectedly contains user data.' }
    $pointer = Get-Content -LiteralPath (Join-Path $target 'current.json') -Raw | ConvertFrom-Json
    $installedApp = Join-Path (Join-Path $target $pointer.Directory) 'HoverLex.exe'
    $process = Start-Process -FilePath $installedApp -ArgumentList '--correction-test' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Installed offline proofreading test failed.' }
    Write-Host 'PASS public silent installation, shortcuts, empty user data, HTTPS update channel and offline proofreading'
}
Write-Host ('Public release ready: ' + $output)
