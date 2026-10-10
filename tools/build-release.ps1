# 릴리즈 빌드: dotnet Release 빌드 → Godot Windows 내보내기 → Inno Setup 인스톨러 + zip → Android APK(릴리즈 서명) → (옵션) 릴리즈에 업로드
# 사용: .\tools\build-release.ps1 [-Upload] [-Tag v0.0.1] [-SkipAndroid]
# Android(v0.0.76): export_presets.cfg "Android" 프리셋의 version/code·name을 project.godot 버전으로 맞춘 뒤 --export-release.
#   서명 키스토어 = %APPDATA%\Godot\keystores\cube-release.keystore, 사용자/비밀번호 = 같은 폴더의 cube-release.txt(1행 alias, 2행 password; 저장소 밖).
#   Godot이 GODOT_ANDROID_KEYSTORE_RELEASE_PATH/USER/PASSWORD 환경 변수로 읽는다. 결과 dist\Cube-<ver>-android.apk.
param(
    [switch]$Upload,
    [switch]$SkipAndroid,
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

    # 외부 앱 애드온(assets/addons/<app>/*)은 항상 같이 배포한다: 빌드 폴더 addons\(zip·인스톨러 포함) + 별도 Cube-<ver>-addons.zip
    Write-Host "== addons"
    $addonsSrc = Join-Path $root "assets\addons"
    $addonsDst = Join-Path $buildDir "addons"
    Copy-Item -Recurse -Force $addonsSrc $addonsDst
    Get-ChildItem -Recurse -File $addonsDst | ForEach-Object { Write-Host ("   {0,10:N0}  addons\{1}" -f $_.Length, $_.FullName.Substring($addonsDst.Length + 1)) }
    $addonsZip = Join-Path $distDir "Cube-$version-addons.zip"
    if (Test-Path $addonsZip) { Remove-Item $addonsZip }
    Compress-Archive -Path (Join-Path $addonsSrc "*") -DestinationPath $addonsZip

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

    $apk = $null
    if (-not $SkipAndroid) {
        Write-Host "== godot export (Android)"
        # 프리셋 버전 동기화: version/code = 패치 번호(정수, 증가), version/name = 버전 문자열
        $code = [int]($version.Split('.')[-1])
        $presets = Get-Content export_presets.cfg -Raw
        $presets = [regex]::Replace($presets, 'version/code=\d+', "version/code=$code")
        $presets = [regex]::Replace($presets, 'version/name="[^"]*"', "version/name=`"$version`"")
        [IO.File]::WriteAllText((Join-Path $root "export_presets.cfg"), $presets, (New-Object System.Text.UTF8Encoding $false))
        $ksDir = Join-Path $env:APPDATA "Godot\keystores"
        $ksFile = Join-Path $ksDir "cube-release.keystore"; $ksPass = Join-Path $ksDir "cube-release.txt"
        if (-not (Test-Path $ksFile) -or -not (Test-Path $ksPass)) { throw "릴리즈 키스토어가 없습니다: $ksFile / $ksPass (keytool -genkeypair -alias cube ... 로 만들고 txt에 alias·password를 한 줄씩)" }
        $lines = Get-Content $ksPass
        $env:GODOT_ANDROID_KEYSTORE_RELEASE_PATH = $ksFile
        $env:GODOT_ANDROID_KEYSTORE_RELEASE_USER = $lines[0].Trim()
        $env:GODOT_ANDROID_KEYSTORE_RELEASE_PASSWORD = $lines[1].Trim()
        $androidDir = Join-Path $root "build\android"
        New-Item -ItemType Directory -Force $androidDir | Out-Null
        $apkBuild = Join-Path $androidDir "Cube.apk"
        if (Test-Path $apkBuild) { Remove-Item $apkBuild }
        & $Godot --headless --path . --export-release "Android" $apkBuild
        if ($LASTEXITCODE -ne 0) { throw "godot android export 실패 ($LASTEXITCODE)" }
        if (-not (Test-Path $apkBuild)) { throw "APK가 생성되지 않았습니다" }
        $apk = Join-Path $distDir "Cube-$version-android.apk"
        Copy-Item -Force $apkBuild $apk
        Write-Host ("   {0,12:N0}  {1}" -f (Get-Item $apk).Length, (Split-Path -Leaf $apk))
    }

    Write-Host "== 산출물"
    Get-ChildItem $distDir | ForEach-Object { Write-Host ("   {0,12:N0}  {1}" -f $_.Length, $_.Name) }

    if ($Upload) {
        Write-Host "== gh release upload $Tag"
        $files = @($setup, $zip, $addonsZip); if ($apk) { $files += $apk }
        gh release upload $Tag @files --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release upload 실패" }
    }
    Write-Host "OK"
}
finally { Pop-Location }
