# glTF 내보내기/가져오기 왕복 스모크 테스트 (헤드리스)
# 사용: .\tools\smoke-export.ps1 [-Out C:\tmp\smoke.glb] [-Full]
#   -Full: 피벗·계층·비균등 스케일·스킨·애니메이션·라이트·텍스처·선택 내보내기까지 glb/gltf/fbx/obj 전체 왕복(SmokeRoundTrip)
param(
    [string]$Out = (Join-Path $env:TEMP "cube_smoke.glb"),
    [string]$Godot = "D:\Godot\GodotEngine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe",
    [switch]$Full
)
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build Cube.csproj -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Error "build failed"; exit 1 }
    $extra = @(); if ($Full) { $extra += "--full" }
    & $Godot --headless --path . res://scenes/tests/SmokeExport.tscn -- "--out=$Out" @extra
    $code = $LASTEXITCODE
    if ($code -eq 0) { Write-Host "smoke OK -> $Out" } else { Write-Host "smoke FAILED ($code)" }
    exit $code
}
finally { Pop-Location }
