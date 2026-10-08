$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloadRoot = Join-Path $projectRoot 'artifacts/proofreader-download'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
$ProgressPreference = 'SilentlyContinue'
$javaInfoPath = Join-Path $downloadRoot 'java-source.json'
$javaZip = Join-Path $downloadRoot 'java.zip'
if (-not (Test-Path -LiteralPath $javaInfoPath)) {
    $javaRelease = Invoke-RestMethod -Uri 'https://api.adoptium.net/v3/assets/latest/21/hotspot?os=windows&architecture=x64&image_type=jre'
    $javaRelease[0].binary.package | Select-Object link,checksum,size | ConvertTo-Json | Set-Content -LiteralPath $javaInfoPath -Encoding UTF8
}
$javaPackage = Get-Content -Raw -LiteralPath $javaInfoPath | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $javaZip)) { Invoke-WebRequest -Uri $javaPackage.link -OutFile $javaZip }
if ((Get-FileHash -LiteralPath $javaZip -Algorithm SHA256).Hash -ne $javaPackage.checksum) { throw 'Java checksum mismatch' }
$mavenZip = Join-Path $downloadRoot 'maven.zip'
$mavenUrl = 'https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.11/apache-maven-3.9.11-bin.zip'
if (-not (Test-Path -LiteralPath $mavenZip)) { Invoke-WebRequest -Uri $mavenUrl -OutFile $mavenZip }
$mavenHash = [string](Invoke-WebRequest -Uri ($mavenUrl + '.sha512')).Content
if ((Get-FileHash -LiteralPath $mavenZip -Algorithm SHA512).Hash -ne $mavenHash.Trim()) { throw 'Maven checksum mismatch' }
& python -c 'import zipfile,sys; from pathlib import Path; p=Path(sys.argv[1]); zipfile.ZipFile(p/"java.zip").extractall(p/"java-runtime"); zipfile.ZipFile(p/"maven.zip").extractall(p/"maven")' $downloadRoot
if ($LASTEXITCODE -ne 0) { throw 'Runtime extraction failed' }
$javaRoot = Get-ChildItem -LiteralPath (Join-Path $downloadRoot 'java-runtime') -Directory | Select-Object -First 1 -ExpandProperty FullName
$previousJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $javaRoot
    & (Join-Path $downloadRoot 'maven/apache-maven-3.9.11/bin/mvn.cmd') -B -C -f (Join-Path $PSScriptRoot 'proofreader-pom.xml') ('-Dmaven.repo.local=' + (Join-Path $projectRoot 'artifacts/proofreader-maven-cache')) dependency:copy-dependencies ('-DoutputDirectory=' + (Join-Path $downloadRoot 'libs')) -DincludeScope=runtime
    if ($LASTEXITCODE -ne 0) { throw 'Proofreader dependencies failed' }
} finally { $env:JAVA_HOME = $previousJava }
& python (Join-Path $PSScriptRoot 'download-proofreader.py')
if ($LASTEXITCODE -ne 0) { throw 'Command-line component download failed' }
& python (Join-Path $PSScriptRoot 'prepare-proofreader.py')
if ($LASTEXITCODE -ne 0) { throw 'Proofreader packaging failed' }
