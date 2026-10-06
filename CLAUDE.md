# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

"Cube"는 게임이 아니라 **게임 리소스 제작용 경량 DCC 툴**이다. Godot 4.7.2 mono(C#) 위에서 동작하며 Autodesk Maya의 인터페이스·조작법·단축키·기능 체계를 기준으로 한다. 집중 영역은 Low-poly 모델링, UV 편집(M2), 리깅/스키닝(M3, 바인드 포즈까지). 영상 렌더링과 키프레임 애니메이션은 범위 밖. glTF/FBX 가져오기·내보내기가 Unity/Godot에 바로 드롭인되는 것이 목표.

현재 M1(뷰포트·Maya 내비게이션·선택·W/E/R 조작기·기본 폴리 편집·Undo·glTF·.cube 저장)이 구현되어 있다. 전체 계획과 M2~M4 범위는 `C:\Users\minkyu\.claude\plans\maya-adaptive-glade.md` 참고.

## 엔진 및 도구 환경

- **Godot 실행 파일**: `D:\Godot\GodotEngine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe` (`.godot/mono/metadata`가 가리키는 버전). `C:\Projects\Godot\Engine\`의 4.6.1은 쓰지 말 것.
- .NET SDK 10 설치, 타깃은 `net8.0`(Godot.NET.Sdk 4.7.2). 8.0 런타임도 설치되어 있어 `dotnet test`가 그대로 돈다.
- C# 핫리로드 없음 → 순수 로직은 `Cube.Core`에 두고 `dotnet test`로 반복한다.

## 자주 쓰는 명령

프로젝트 루트(`C:\Projects\Godot\Projects\Cube`), PowerShell 기준.

```powershell
$godot = "D:\Godot\GodotEngine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"

dotnet build Cube.sln                 # Core + App + Tests 빌드 (Godot 실행 전 반드시 빌드; CLI 실행은 재빌드하지 않음)
dotnet test Cube.sln                  # xUnit (tests/Cube.Core.Tests)
dotnet test tests/Cube.Core.Tests --filter "FullyQualifiedName~MeshOps"   # 단일 테스트 클래스

& $godot --path .                     # 앱 실행 (scenes/Shell.tscn)
& $godot --path . -- --with-cube      # 기본 큐브 하나를 만들고 시작 (개발용)
& $godot -e --path .                  # 에디터
.\tools\smoke-export.ps1              # 헤드리스 glTF 내보내기→가져오기 왕복 스모크 (종료 코드 0이면 성공)
```

### 자동 조작으로 검증하기 (DebugDriver)
GUI 동작은 입력 이벤트 주입 스크립트로 검증한다. 좌표는 뷰포트 로컬 픽셀.
```powershell
& $godot --path . -- --with-cube "--drive=wait 6; move 100 100; press L; release L; key A; wait 2; key F11; wait 1; move 537 300; press L; release L; key E ctrl; wait 2; print; shot C:/tmp/a.png" --quit-after=100
```
스텝: `wait N`, `move X Y`, `press L|M|R [alt|shift|ctrl]`, `release`, `drag X Y [mods]`, `wheel N`, `key NAME [mods]`(Godot Key 이름: `Key4`, `F9`, `Escape`...), `action <actionId>`, `export PATH [selection]`, `import PATH`, `save PATH`, `open PATH`, `print`(노드/선택/Undo/툴/활성 노드 트랜스폼·메시 Y 범위), `shot PATH`. 주입된 입력은 다음 프레임에 처리되므로 입력 스텝 뒤에는 자동으로 한 프레임 쉰다. `--drive`가 있으면 핫키/메뉴 로그(`[Hotkey]`, `[Menu]`, `[Select]`)가 켜진다. `--screenshot=PATH --quit-after=N`만으로 정적 스크린샷도 가능.

Godot MCP 서버(`godot`)도 등록되어 있다: `run_project` → `get_debug_output` → `stop_project`. `add_node`의 properties는 `.tscn`에 저장되지 않으므로 씬은 직접 편집한다.

## 아키텍처

두 프로젝트: **`src/Cube.Core`**(순수 C#, Godot 의존 없음, `System.Numerics`)와 **`Cube.csproj`**(Godot 측, `src/Cube.App`). 경계 변환은 `src/Cube.App/Bridge/MathConvert.cs`와 `GodotMeshBridge.cs` 두 파일뿐이다. `Cube.csproj`는 Godot.NET.Sdk가 하위 `*.cs`를 전부 글로빙하므로 `Compile Remove`로 Core/tests를 제외한다.

### Core (`src/Cube.Core`)
- `Mesh/PolyMesh.cs`: 하프에지 폴리곤 메시. **ID = 슬롯 인덱스, 삭제는 `Alive=false`, 세션 중 재사용 없음**(선택·Undo 안정성). 코너 속성(UV/노멀)은 `HalfEdge`에. 비매니폴드(엣지에 3면, 같은 방향 중복)는 `AddFace`가 -1로 거부. `Compact()`는 저장/내보내기 직전에만.
- `Mesh/MeshOps.cs`: Extrude(Keep Faces Together), DeleteFaces/Edges(면 병합+2가 정점 정리 = Maya Delete Edge/Vertex)/Vertices, MergeVertices, ReverseFaces(연결 요소 전체 뒤집음), Append/ExtractFaces/ConnectedComponents(Combine/Separate). `MeshNormals.Recompute`는 호출자(명령)가 한다. `MeshTessellator` → `RenderMeshData`(코너 언롤 삼각형, 선, 점, 면 중심 + TriToFace/LineToEdge/PointToVertex 매핑).
- `Scene/Document.cs`: Maya DAG(`SceneNode` = transform, `MeshShape` = shape). Godot을 모르고 `Changed(DocChange)`만 발행. `Transform3`는 Maya 채널 박스와 같은 TRS(오일러 도, XYZ 순서, 행벡터 행렬 `S·Rx·Ry·Rz·T`).
- `Selection/SelectionState.cs`: 모드(Object/Vertex/Edge/Face/Uv) + 노드별 `ComponentSet`. 선택 변경은 `SelectionCommand`로 Undo 가능(Maya 동일). `SelectionOps`는 Grow/Shrink/Convert.
- `Commands/`: 자체 `UndoStack`(Godot UndoRedo 미사용). 드래그는 문서를 직접 갱신(프리뷰)하고 놓을 때 `Push(cmd, alreadyApplied: true)`. `MeshEditCommand`는 전체 메시 스냅샷(before/after)이며 위상 변경 통지 동안 해당 노드의 컴포넌트 선택을 비웠다가 복원한다(옛 ID 참조 방지).
- `Picking/`: `CameraProjection`(투영/역투영), `RayPicker`(면=레이, 엣지/정점=화면 거리 임계, camera-based 가림, 마키). `Camera/OrbitCamera`: 피벗 기반 텀블/트랙/돌리/프레임. `Geometry/DragMath`: 조작기 수학. `IO/`: `TriangleSoupToPolyMesh`(용접, 하드엣지 추론, 공면 삼각형→쿼드), `CubeFileFormat`(.cube JSON), 익스포터/임포터 인터페이스.

### App (`src/Cube.App`)
- `App/CubeApp.cs`(autoload): Document, Settings, Hi-DPI 배율, 디버그 인자. `App/DebugDriver.cs`, `App/SmokeExportRunner.cs`.
- `UI/Shell.cs` + `ShellActions.cs`: 레이아웃은 코드로 구성(.tscn은 루트만). **모든 메뉴/셸프/툴박스/핫키는 `ActionRegistry`의 ActionId만 호출한다.** 새 기능은 `RegisterActions`에 액션을 등록하고 `BuildMenus`에 넣는다. `Hotkeys/ShellInput.cs`가 `_Input`에서 키를 라우팅(텍스트 필드 포커스 시 무시, `viewport` 컨텍스트 = 마우스 오버/포커스). 바인딩은 `config/hotkeys.default.json`(`user://hotkeys.json`로 덮어쓰기). Godot `InputMap`은 쓰지 않는다.
- `Viewport/ViewportPanel.cs`: SubViewport(자체 월드, Canvas 배경 그라디언트, 헤드라이트). 입력 순서: `NavigationHandler`(Alt+버튼/휠) → `ToolManager.Current` → 끝. `ViewportDisplay`가 선택/셰이딩 모드를 `MeshView.Style`로 변환. `SceneView`/`MeshView`가 Document를 미러링(표면·와이어·정점·면중심·면 틴트).
- `Tools/`: `SelectTool`(클릭/마키/호버/RMB 모드 메뉴) → `TransformToolBase`(피벗, 축 방향 World/Object/Normal, 드래그 캡처/커밋) → `MoveTool`/`RotateTool`/`ScaleTool`. 조작기는 `Viewport/Gizmos/`(화면 고정 100px, 깊이 무시, CPU 스크린 공간 히트).
- `IO/`: `GltfExporter`(GltfDocument), `GltfImporter`/`FbxImporter`(GenerateScene 순회), `FileActions`/`SceneFileActions`(네이티브 다이얼로그).

### 반드시 지킬 규약
- **코어는 반시계(CCW)가 앞면, Godot은 시계(CW)가 앞면.** `GodotMeshBridge`에서 삼각형마다 인덱스 1,2를 바꾼다(가져오기는 반대). 이걸 빼먹으면 면이 어둡고 컬링이 뒤집힌다.
- **UV 원점**: 코어는 하단 원점(Maya), Godot/glTF는 상단 원점 → 브리지에서 `v = 1 - v`.
- 좌표계: 내부 = Godot 규약(오른손, Y-up, -Z forward, m). 변환은 Exporter/Importer만 담당한다.
- Godot 머티리얼 색: 셰이더에서 정점/인스턴스 색은 선형으로 취급되므로 sRGB 색은 `SrgbToLinear()`로 넘긴다.
- D3D12는 포인트 크기 미지원 → 정점 점은 MultiMesh 쿼드(`assets/shaders/points.gdshader`). 불투명 패스에서는 `render_priority`가 무시되므로 와이어/점은 깊이 바이어스로 표면 위에 올린다.
- `Input.ParseInputEvent`로 주입한 이벤트는 다음 입력 플러시에 처리된다(동기 아님).
- `Cube.Core.Math` 같은 네임스페이스는 `System.Math`를 가리므로 쓰지 않는다(`Geometry` 사용). `FileAccess`는 `Godot.FileAccess`로 한정.

## 테스트
- `tests/Cube.Core.Tests`(xUnit): 메시 빌더/연산/테셀레이션, Undo, 선택 규칙, 카메라, 피킹, DragMath, 삼각형→폴리 변환, .cube 왕복. 새 메시 연산에는 `MeshValidator.Check`가 비어 있는지와 오일러 특성 검사를 넣는다.
- 헤드리스 스모크: `tools/smoke-export.ps1`(`scenes/tests/SmokeExport.tscn`).
- GUI 동작: 위 DebugDriver 스크립트 + 스크린샷. Unity 확인은 `tools/unity-check.md` 체크리스트(수동).

## 버전 및 릴리즈 워크플로
- 버전의 단일 출처는 `project.godot`의 `application/config/version`(현재 `0.0.1`).
- **수정 작업을 완료할 때마다** 버전을 그대로 둔 채 커밋 → `origin/main` 푸시 → 같은 버전의 드래프트 릴리즈 노트 갱신(`gh release edit v<ver> --draft --notes-file -`). 드래프트가 없으면 `gh release create v<ver> --draft --target main`. 버전은 사용자가 올리라고 할 때만 올린다.
- **드래프트 릴리즈에는 빌드 산출물을 패키징해 첨부한다**: `.\tools\build-release.ps1 -Upload` 가 Release 빌드 → Godot Windows 내보내기(`export_presets.cfg`의 "Windows Desktop", `build/windows/`) → `dist/Cube-<ver>-win64.zip` + Inno Setup 인스톨러 `dist/Cube-<ver>-Setup.exe`(`installer/Cube.iss`) → `gh release upload --clobber` 까지 수행한다. 필요 도구: Godot 4.7.2 mono 내보내기 템플릿(`%APPDATA%\Godot\export_templates\4.7.2.stable.mono\`), Inno Setup 6(`winget install JRSoftware.InnoSetup`).
- GitHub 작업은 항상 80dots 계정.

## 파일 규칙
- UTF-8, LF(`.gitattributes`). `.godot/`, `bin/`, `obj/`, `.idea/`는 추적하지 않는다.
- 원격 `origin` → https://github.com/80dots/Cube.git, 기본 브랜치 `main`.
