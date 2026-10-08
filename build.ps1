param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Requires 64-bit Windows and .NET Framework 4.8.' }
$outDir = Join-Path $projectRoot 'dist/HoverLex'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid VERSION.' }
$generatedDir = Join-Path $projectRoot 'artifacts/build'
New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null
$versionSource = Join-Path $generatedDir 'VersionInfo.cs'
$versionText = 'using System.Reflection;' + [Environment]::NewLine + '[assembly: AssemblyTitle("HoverLex")]' + [Environment]::NewLine + '[assembly: AssemblyFileVersion("' + $version + '.0")]' + [Environment]::NewLine + '[assembly: AssemblyVersion("' + $version + '.0")]'
[IO.File]::WriteAllText($versionSource, $versionText, (New-Object Text.UTF8Encoding($false)))
$references = @('System.dll', 'System.Core.dll', 'System.Security.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'Accessibility.dll', 'System.Web.Extensions.dll', 'System.Speech.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'System.Runtime.dll', 'System.ObjectModel.dll')
$argsList = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001', ('/out:' + (Join-Path $outDir 'HoverLex.exe')), ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')))
$argsList += '/win32icon:' + (Join-Path $projectRoot 'assets/HoverLex.ico')
$argsList += '/resource:' + (Join-Path $projectRoot 'assets/HoverLex.ico') + ',hoverlex-icon'
$argsList += '/resource:' + (Join-Path $projectRoot 'assets/HoverLex-mark.png') + ',hoverlex-mark'
foreach ($name in $references) {
    $referencePath = Join-Path $framework $name
    if (-not (Test-Path -LiteralPath $referencePath)) { $referencePath = Join-Path $framework ('WPF/' + $name) }
    $argsList += '/reference:' + $referencePath
}
foreach ($name in @('UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll')) { $argsList += '/reference:' + (Join-Path $framework ('WPF/' + $name)) }
$argsList += (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$argsList += $versionSource
& $compiler $argsList
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
foreach ($name in @('README.md', 'SPIKES-INPUT-TRANSLATION.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE', 'HoverLex.exe.config')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $name)) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $outDir }
}
Write-Host ('Built: ' + (Join-Path $outDir 'HoverLex.exe'))
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets/EnglishProofreader.zip') -Destination $outDir
if ($Test) {
    $process = Start-Process -FilePath (Join-Path $outDir 'HoverLex.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath (Join-Path $outDir 'self-test.txt')
    if ($process.ExitCode -ne 0) { throw 'Self-test failed.' }
}
