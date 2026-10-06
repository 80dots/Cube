# 릴리즈 빌드: dotnet Release 빌드 → Godot Windows 내보내기 → Inno Setup 인스톨러 + zip → (옵션) 드래프트 릴리즈에 업로드
# 사용: .\tools\build-release.ps1 [-Upload] [-Tag v0.0.1]
param(
    [switch]$Upload,
    [string]$Tag,
    [string]$Godot = "D:\Godot\GodotEngine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # 버전은 project.godot의 application/config/version 에서 읽는다
    $version = (Select-String -Path project.godot -Pattern 'config/version="([^"]+)"').Matches[0].Groups[1].Value
    if (-not $version) { throw "project.godot에 config/version이 없습니다" }
    if (-not $Tag) { $Tag = "v$version" }
    Write-Host "== Cube $version ($Tag)"

    $buildDir = Join-Path $root "build\windows"
    $distDir = Join-Path $root "dist"
    if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
    New-Item -ItemType Directory -Force $buildDir | Out-Null
    New-Item -ItemType Directory -Force $distDir | Out-Null

    Write-Host "== dotnet build (Release)"
    dotnet build Cube.csproj -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 실패" }

    Write-Host "== godot export (Windows Desktop)"
    & $Godot --headless --path . --export-release "Windows Desktop" (Join-Path $buildDir "Cube.exe")
    if ($LASTEXITCODE -ne 0) { throw "godot export 실패 ($LASTEXITCODE)" }
    if (-not (Test-Path (Join-Path $buildDir "Cube.exe"))) { throw "Cube.exe가 생성되지 않았습니다" }
    Get-ChildItem $buildDir | ForEach-Object { Write-Host ("   {0,10:N0}  {1}" -f $_.Length, $_.Name) }

    Write-Host "== zip"
    $zip = Join-Path $distDir "Cube-$version-win64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $buildDir "*") -DestinationPath $zip

    Write-Host "== Inno Setup"
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "ISCC.exe(Inno Setup 6)를 찾을 수 없습니다. winget install JRSoftware.InnoSetup" }
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$buildDir" "/DOutputDir=$distDir" (Join-Path $root "installer\Cube.iss")
    if ($LASTEXITCODE -ne 0) { throw "ISCC 실패 ($LASTEXITCODE)" }
    $setup = Join-Path $distDir "Cube-$version-Setup.exe"
    if (-not (Test-Path $setup)) { throw "인스톨러가 생성되지 않았습니다" }

    Write-Host "== 산출물"
    Get-ChildItem $distDir | ForEach-Object { Write-Host ("   {0,12:N0}  {1}" -f $_.Length, $_.Name) }

    if ($Upload) {
        Write-Host "== gh release upload $Tag"
        gh release upload $Tag $setup $zip --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release upload 실패" }
    }
    Write-Host "OK"
}
finally { Pop-Location }
