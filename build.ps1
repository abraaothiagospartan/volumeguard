# Compila o VolumeGuard e o instalador com o compilador do .NET Framework 4.8 que já vem no Windows.
# Uso:  powershell -ExecutionPolicy Bypass -File build.ps1
# Saída: dist\VolumeGuard.exe (versão portátil) e release\VolumeGuard-Setup-<versão>.exe (+ SHA256SUMS.txt)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf  = Join-Path $fw 'WPF'
$csc  = Join-Path $fw 'csc.exe'
$dist = Join-Path $root 'dist'
$rel  = Join-Path $root 'release'
$obj  = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $dist, $rel, $obj, (Join-Path $root 'assets') | Out-Null

$version = [regex]::Match((Get-Content "$root\src\Properties\BuildInfo.cs" -Raw), 'Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Versão não encontrada em BuildInfo.cs' }

$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Runtime.Serialization.dll', 'System.Xml.dll'
) | ForEach-Object { "/r:$_" }
# sem /debug: nenhum .pdb nem caminho local vai para dentro do .exe
$common = @('/nologo', '/platform:x64', '/optimize+', '/debug-')

# 1) Ícone
$ico = Join-Path $root 'assets\app.ico'
& $csc @common /target:exe /out:"$obj\IconGen.exe" /r:System.Drawing.dll "$root\tools\IconGen.cs" "$root\src\UI\IconArt.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o gerador de ícone' }
& "$obj\IconGen.exe" $ico
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o ícone' }

# 2) App
$app = Join-Path $dist 'VolumeGuard.exe'
$xaml = Get-ChildItem "$root\src\UI\Xaml\*.xaml" | ForEach-Object { "/resource:$($_.FullName),VolumeGuard.Xaml.$($_.Name)" }
& $csc @common /target:winexe /warn:4 /out:"$app" /win32icon:"$ico" /win32manifest:"$root\src\app.manifest" `
    "/resource:$ico,VolumeGuard.app.ico" @xaml @refs /recurse:"$root\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o VolumeGuard' }

# 3) Instalador (leva o app dentro)
$setup = Join-Path $rel "VolumeGuard-Setup-$version.exe"
& $csc @common /target:winexe /warn:4 /out:"$setup" /win32icon:"$ico" /win32manifest:"$root\installer\setup.manifest" `
    "/resource:$app,Setup.payload.exe" "/resource:$ico,Setup.app.ico" `
    "/resource:$root\src\UI\Xaml\Theme.xaml,Setup.Theme.xaml" "/resource:$root\installer\Setup.xaml,Setup.Setup.xaml" `
    @refs "$root\installer\Setup.cs" "$root\src\Properties\BuildInfo.cs"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o instalador' }

# 4) Versão portátil (zip) e hashes
$zip = Join-Path $rel "VolumeGuard-$version-portatil.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $app, (Join-Path $root 'LICENSE') -DestinationPath $zip
$sums = Get-ChildItem $rel -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
Set-Content -Path (Join-Path $rel 'SHA256SUMS.txt') -Value $sums -Encoding ASCII
Write-Host "OK -> $app"
Write-Host "OK -> $setup"
Write-Host "OK -> $zip"
