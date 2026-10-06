# glTF 내보내기/가져오기 왕복 스모크 테스트 (헤드리스)
# 사용: .\tools\smoke-export.ps1 [-Out C:\tmp\smoke.glb]
param(
    [string]$Out = (Join-Path $env:TEMP "cube_smoke.glb"),
    [string]$Godot = "D:\Godot\GodotEngine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
)
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build Cube.csproj -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "build failed"; exit 1 }
    & $Godot --headless --path . res://scenes/tests/SmokeExport.tscn -- "--out=$Out"
    $code = $LASTEXITCODE
    if ($code -eq 0) { Write-Host "smoke OK -> $Out" } else { Write-Host "smoke FAILED ($code)" }
    exit $code
}
finally { Pop-Location }
