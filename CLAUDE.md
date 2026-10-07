# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

"Cube"는 게임이 아니라 **게임 리소스 제작용 경량 DCC 툴**이다. Godot 4.7.2 mono(C#) 위에서 동작하며 Autodesk Maya의 인터페이스·조작법·단축키·기능 체계를 기준으로 한다. 집중 영역은 Low-poly 모델링, UV 편집(M2), 리깅/스키닝(M3, 바인드 포즈까지). 영상 렌더링과 키프레임 애니메이션은 범위 밖. glTF/FBX 가져오기·내보내기가 Unity/Godot에 바로 드롭인되는 것이 목표.

현재 M1(뷰포트·Maya 내비게이션·선택·W/E/R 조작기·기본 폴리 편집·Undo·glTF·.cube 저장)과 M2(UV 편집기·UV 투영/Cut/Sew/Unfold/Layout·Insert Edge Loop·Bevel·Bridge·더블클릭 루프 선택·X/V 스냅·4분할 뷰·파이 메뉴)·M3(조인트·스무스 바인드·가중치 페인트·LBS 변형 표시·glTF 스켈레톤/스킨 내보내기·가져오기·.cube 저장)가 구현되어 있다. 다음은 M4(바이너리 FBX writer). 전체 계획과 M2~M4 범위는 `C:\Users\minkyu\.claude\plans\maya-adaptive-glade.md` 참고.

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
스텝: `wait N`, `move X Y`, `press L|M|R [alt|shift|ctrl]`, `dblclick L`(DoubleClick=true 프레스; 앞에 press/release로 첫 클릭을 보낼 것), `release`, `drag X Y [mods]`, `wheel N`, `key NAME [mods]`(Godot Key 이름: `Key4`, `F9`, `Escape`...), `action <actionId>`, `export PATH [selection]`, `import PATH`, `save PATH`, `open PATH`, `print`(노드/선택/Undo/툴/활성 노드 트랜스폼·메시 AABB·컴포넌트 수 v/e/f/u), `gizmo`(조작기 피벗/축), `panel N`(활성 뷰포트 선택: 0 top, 1 persp, 2 front, 3 side), `axisdrag X|Y|Z px` / `ringdrag X|Y|Z|S px` / `centerdrag dx dy`(조작기 핸들을 찾아 드래그; 뷰 각도 무관), `histedit INDEX PARAM VALUE[,Y,Z]`(활성 노드 히스토리 파라미터 편집), `matnew TYPE`(머티리얼 생성+선택에 할당), `mattex ID PATH`(머티리얼 컬러 텍스처), `matassign ID`(선택에 할당, 0 = lambert1), `shelf N`(셸프 탭 0 Polygons/1 UV/2 Rigging), `keydown NAME`/`keyup NAME`(홀드 키), `shot PATH`. `print`의 active 줄에 `material=`과 피벗(`P<...>`, 0이면 생략)이 찍힌다. `key` 이름은 Godot Key 열거형(`Key3`, `F12`)이며 `print`는 면/정점 수·스무스 프리뷰·uv0·히스토리 목록도 찍는다. 확인 다이얼로그를 띄우는 액션(`file.new`)은 입력을 막아 멈추므로 드라이브에서 쓰지 않는다. 좌표는 `c+10`처럼 뷰포트 중심 기준도 된다. 뷰포트 로컬 원점은 1600×900 창에서 전역 (326, 200)쯤이며(셸프 높이에 따라 달라짐) 플로팅 패널 등 뷰포트 밖 UI를 누르려면 전역 좌표에서 이를 뺀다. 주입된 입력은 다음 프레임에 처리되므로 입력 스텝 뒤에는 자동으로 한 프레임 쉰다. `--drive`가 있으면 핫키/메뉴 로그(`[Hotkey]`, `[Menu]`, `[Select]`)가 켜진다. `--screenshot=PATH --quit-after=N`만으로 정적 스크린샷도 가능.

Godot MCP 서버(`godot`)도 등록되어 있다: `run_project` → `get_debug_output` → `stop_project`. `add_node`의 properties는 `.tscn`에 저장되지 않으므로 씬은 직접 편집한다.

## 아키텍처

두 프로젝트: **`src/Cube.Core`**(순수 C#, Godot 의존 없음, `System.Numerics`)와 **`Cube.csproj`**(Godot 측, `src/Cube.App`). 경계 변환은 `src/Cube.App/Bridge/MathConvert.cs`와 `GodotMeshBridge.cs` 두 파일뿐이다. `Cube.csproj`는 Godot.NET.Sdk가 하위 `*.cs`를 전부 글로빙하므로 `Compile Remove`로 Core/tests를 제외한다.

### Core (`src/Cube.Core`)
- `Mesh/PolyMesh.cs`: 하프에지 폴리곤 메시. **ID = 슬롯 인덱스, 삭제는 `Alive=false`, 세션 중 재사용 없음**(선택·Undo 안정성). 코너 속성(UV/노멀)은 `HalfEdge`에. 비매니폴드(엣지에 3면, 같은 방향 중복)는 `AddFace`가 -1로 거부. `Compact()`는 저장/내보내기 직전에만.
- `Mesh/MeshOps.cs`: Extrude(Keep Faces Together), DeleteFaces/Edges(면 병합+2가 정점 정리 = Maya Delete Edge/Vertex)/Vertices, MergeVertices, ReverseFaces(연결 요소 전체 뒤집음), Append/ExtractFaces/ConnectedComponents(Combine/Separate). `MeshNormals.Recompute`는 호출자(명령)가 한다. `MeshTessellator` → `RenderMeshData`(코너 언롤 삼각형, 선, 점, 면 중심 + TriToFace/LineToEdge/PointToVertex 매핑).
- `Scene/Document.cs`: Maya DAG(`SceneNode` = transform, `MeshShape` = shape). Godot을 모르고 `Changed(DocChange)`만 발행. `Transform3`는 Maya 채널 박스와 같은 TRS + **Pivot**(오브젝트 공간 회전/스케일 피벗; 행벡터 행렬 `T(-P)·S·Rx·Ry·Rz·T(P+T)`, 피벗의 부모 공간 위치 = P+T, `SceneNode.PivotWorld`). `FromMatrix(m, pivot)`/`SolveTranslation`은 피벗을 유지하며 행렬에서 복원하고 `WithPivotKeepingMatrix`는 월드를 유지한 채 피벗만 옮긴다(Center Pivot/Edit Pivot). 기존 노드의 Local을 행렬에서 다시 만들 때는 반드시 `FromMatrix(m, node.Local.Pivot)`를 쓴다(CompensateChildren, JointOps.Orient, InsertJoint). `.cube`에 `pivot`으로 저장.
- `Mesh/MeshOps.*.cs`: `MeshOps`는 partial. `MeshOps.Loops.cs`(EdgeLoop/EdgeRing/InsertEdgeLoop), `MeshOps.Bevel.cs`(BevelEdges: 1세그먼트 챔퍼, 엣지 쿼드 + 정점 캡), `MeshOps.Bridge.cs`(BridgeEdges: 경계 엣지 체인 2개를 쿼드로 연결). 범용 위상 명령은 `Commands/MeshOpCommand.cs`(람다가 메시를 바꾸고 새 선택 컴포넌트를 돌려줌).
- `Uv/UvOps.cs`: `UvTopology.Build`(심이 아닌 엣지에서 UV가 같은 코너를 합쳐 UV 점/셸을 만든다) + 투영(Planar/Cylindrical/Spherical, 경계와 UV 불연속 엣지를 심으로 표시)/CutEdges/SewEdges/UnfoldRelax/Layout/Flip. `Edge.Seam`이 UV 심이며 `.cube`에 `seams`로 저장된다. UV 편집 명령은 `Commands/UvCommands.cs`의 `UvEditCommand`(코너 UV + 심 스냅샷; 즉시형 또는 Capture/Commit 드래그형).
- `Commands/History.cs`: **구성 이력**(Maya construction history). `MeshShape.History`는 `HistoryEntry`(이름, 편집 가능한 `HistoryParams`, 적용 직전 메시 `Before`, `Replay(mesh, params)`) 목록. `MeshEditCommand`는 `MakeHistoryEntry()`로 항목을 넣고 Undo 시 뺀다. 파라미터가 있는 명령은 `MeshOpCommand(name, node, HistoryParams, (m, p) => …)` 형태로 만든다(Bevel Distance, Insert Edge Loop Position, Smooth Levels, Merge Threshold). 컴포넌트 이동/회전/스케일은 `MoveVerticesCommand`에 `ComponentTransformOp`+파라미터(Translate/Angle/Scale)를 붙여 기록한다(`IAppliedHook`: alreadyApplied로 들어와도 항목 등록). `EditHistoryCommand`가 파라미터를 바꾸고 `HistoryReplay.Rebuild`로 그 항목부터 끝까지 다시 실행한다(ID가 슬롯 인덱스라 뒤 항목의 컴포넌트 참조가 유지됨). Properties의 History 그룹(최신이 위)에서 항목을 고르면 파라미터 스핀박스가 나온다. `.cube`에는 저장하지 않는다(Delete History = `edit.deleteHistory`).
- `Mesh/MeshOps.Subdivide.cs`: `CatmullClark`(n각형·경계, 코너 UV 보간, 하드엣지/심 플래그 상속)과 `Smooth(levels)`. Mesh → Smooth는 히스토리 항목(Levels). **Smooth Mesh Preview**는 `MeshShape.SmoothPreview`(0 케이지/1 케이지+스무스/2 스무스, 1·2·3키 = `display.smoothPreview*`, `ChangeKind.DisplayChanged`) — `MeshView`가 표면만 서브디비전 결과로 바꾸고 선택/편집은 케이지 그대로.
- `Scene/Rig.cs`: `JointShape`(조인트 = JointShape를 가진 SceneNode, `IsJoint`)와 `SkinCluster`(조인트 ID 목록, 바인드 시점 조인트 월드 역행렬, 메시 바인드 월드, 정점별 (조인트, 가중치) 최대 4개). `MeshShape.Skin`에 붙는다. `Rig/SkinOps.cs`: `SmoothBind`(본 선분까지 거리 1/d² 가중, 상위 4개 정규화), `Deform`(LBS; 표시 전용, 메시 정점은 바인드 위치 그대로), `PaintVertex`(Replace/Add/Smooth, 다른 조인트는 비율 유지 정규화), `NormalizeAll`. 명령은 `Commands/SkinCommands.cs`(`SetSkinCommand` 바인드/디태치, `WeightPaintCommand` 스트로크). `ChangeKind.SkinChanged`. `.cube`에 `jointRadius`/`skin`으로 저장.
- `Document.AddNode`는 하위 노드에도 ID를 배정한다. 문서에 넣기 전에 ID로 참조해야 하면(가져온 스킨의 조인트) `Document.AssignIds`를 먼저 호출한다.
- `Selection/SelectionState.cs`: 모드(Object/Vertex/Edge/Face/Uv) + 노드별 `ComponentSet`. 선택 변경은 `SelectionCommand`로 Undo 가능(Maya 동일). `SelectionOps`는 Grow/Shrink/Convert.
- `Commands/`: 자체 `UndoStack`(Godot UndoRedo 미사용). 드래그는 문서를 직접 갱신(프리뷰)하고 놓을 때 `Push(cmd, alreadyApplied: true)`. `MeshEditCommand`는 전체 메시 스냅샷(before/after)이며 위상 변경 통지 동안 해당 노드의 컴포넌트 선택을 비웠다가 복원한다(옛 ID 참조 방지).
- `Picking/`: `CameraProjection`(투영/역투영), `RayPicker`(면=레이, 엣지/정점=화면 거리 임계, camera-based 가림, 마키). `Camera/OrbitCamera`: 피벗 기반 텀블/트랙/돌리/프레임. `Geometry/DragMath`: 조작기 수학. `IO/`: `TriangleSoupToPolyMesh`(용접, 하드엣지 추론, 공면 삼각형→쿼드), `CubeFileFormat`(.cube JSON), 익스포터/임포터 인터페이스.

### App (`src/Cube.App`)
- `App/CubeApp.cs`(autoload): Document, Settings, Hi-DPI 배율, 디버그 인자. `App/DebugDriver.cs`, `App/SmokeExportRunner.cs`.
- `UI/Shell.cs` + `ShellActions.cs`: 레이아웃은 코드로 구성(.tscn은 루트만). **모든 메뉴/셸프/툴박스/핫키는 `ActionRegistry`의 ActionId만 호출한다.** 예외는 파이의 동적 항목: `PieItem.Run`(직접 실행)과 `PieItem.Sub`(서브 파이 생성기). 서브 파이는 버튼을 뗀 뒤에도 열려 있는 sticky 모드(`PieMenu.Open(..., sticky, title)`)로 LMB 클릭이 선택, RMB/Esc가 닫음(`ViewportPanel.ExecutePie`). Edit 파이(오브젝트 모드)의 "Assign Material ▸"가 이 방식으로 lambert1/문서 머티리얼/Material Editor를 보여 준다(`PieMenus.MaterialItems`). `select.hierarchy`(Select 메뉴·Select 파이)는 선택 오브젝트의 자손을 모두 선택한다. 새 기능은 `RegisterActions`에 액션을 등록하고 `BuildMenus`에 넣는다. `Hotkeys/ShellInput.cs`가 `_Input`에서 키를 라우팅(텍스트 필드 포커스 시 무시, `viewport` 컨텍스트 = 마우스 오버/포커스). 홀드 키는 `HeldKeys` + `HeldKeysChanged` 이벤트(상태 라인 스냅 버튼이 X/V를 누르는 동안 눌린 상태로 표시됨, `SyncStatusLine`). 바인딩은 `config/hotkeys.default.json`(`user://hotkeys.json`로 덮어쓰기). Godot `InputMap`은 쓰지 않는다.
- `Viewport/ViewportLayout.cs`: 패널 4개(top/persp/front/side)를 항상 만들어 두고 단일 ↔ 4분할을 토글한다(Space 탭, `view.toggleLayout`). 마우스가 들어가거나 눌린 패널이 **활성 패널**(`Shell.Viewport`)이며 `ToolContext.Viewport`가 바뀌면 툴이 기즈모를 그 패널로 옮긴다. 표시/뷰 액션은 활성 패널에 적용된다.
- `Viewport/ViewportPanel.cs`: SubViewport(자체 월드, Canvas 배경 그라디언트, 헤드라이트). 입력 순서: `NavigationHandler`(Alt+버튼/휠) → 파이 메뉴(`UI/PieMenu.cs`, RMB 홀드 = 기본 파이(모드 전환), Shift+RMB = Edit 파이(현재 모드 액션), Ctrl+RMB = Select 파이(To Edge/Boundary Edge/Vertex/Face/UV/UV Island, Grow/Shrink), Space 홀드 = 뷰 전환; 항목은 `UI/PieMenus.cs`. UV 편집기 RMB = UV 파이(`PieMenus.UvMenu`) — **UV 편집기에 기능을 추가하면 반드시 UvMenu에도 넣는다**. 9개 이상이면 아래 다열 오버플로) → `ToolManager.Current` → 끝. 우상단 `ViewportHud`(클릭 가능한 뷰 큐브, Persp/Ortho 토글, Wireframe/Shaded/Textured/Lit/UV Grid 버튼). UV Grid 모드는 `assets/textures/uv_grid.png`를 메시 UV로 입힌다.
- **플로팅 패널**(`UI/FloatingPanel.cs`): UV Editor, Material Editor, Paint Skin Weights는 Godot `Window`가 아니라 셸 위에 떠 있는 `PanelContainer`(제목 바 드래그, 우하단 크기 조절, ✕ 닫기)다. 임베디드 Window는 포커스를 가진 동안 창 밖의 RMB/MMB를 가로채 뷰포트 파이 메뉴가 열리지 않았기 때문. `Open()/Close()/Visible`, 내용은 `Content`에 넣는다. 크기 조절은 내용 위에 겹쳐 둔 별도 그립 `Control`(우하단 18px, PanelContainer가 자식을 꽉 채우므로 앵커용 Control 하나를 사이에 둠)이 받는다 — UvCanvas처럼 내용이 입력을 모두 잡아도 동작. 이동/리사이즈는 `GetGlobalMousePosition()`이 아니라 **이벤트의 GlobalPosition**을 쓴다(DebugDriver 주입 입력은 OS 커서를 움직이지 않음).
- `UI/UvEditor/`: UV 편집기는 플로팅 패널(`UvEditorWindow`, Windows → UV Editor)이고 캔버스(`UvCanvas`)가 선택 노드의 UV를 그린다. 툴바는 아이콘(Obj/UV/Edge/Face/**Island** 모드 + 투영/편집/Frame/배경). Island는 UV 모드의 변형(`UvCanvas.IslandMode`, `mode.uvIsland`): 점·엣지·면 중 무엇을 집어도(클릭/마키) 심으로 분리된 섬 전체. 투영/Unfold/Layout/Flip은 `ShellUvActions.TargetFaces`로 선택에 한정된다(면 모드 = 선택 면, 엣지/정점/UV/Island = 선택 UV 점을 모두 포함하는 면, 없으면 하나라도 포함하는 면, 오브젝트 = 전체). `uv.autoSeams`(`Uv/AutoSeams.cs`: 이면각 ≥55°·하드 엣지 + 영역이 원반이 되도록 닫힌 면은 가장 먼 두 정점 경로 둘, 튜브는 경계 사이 최단 경로)는 엣지를 선택만 하고, `uv.autoWrap`(`UvOps.AutoWrap`)은 심 적용 → 영역별 초기화(법선이 상쇄되는 둘러싸는 영역은 심 경로에 랩 경계를 맞춘 임의 축 원통 투영, 아니면 최적 평면) → Unfold → Layout을 한 번에 한다. 선택 UI는 뷰포트와 같다(호버 프리셀렉션, Shift/Ctrl 수식어, 마키). 2D 조작기(W: X/Y 화살표+중앙, E: 링, R: X/Y 상자+중앙)는 선택 중심에 그려지고 핸들을 잡아야 변형된다. 파이 정책도 뷰포트와 같다: RMB = 모드 파이(`PieMenus.UvModeMenu`), Shift+RMB = Edit 파이(`UvMenu`, UV 기능 전부), Ctrl+RMB = Select 파이(`UvSelectMenu`). Alt+MMB 팬, Alt+RMB/휠 줌, F/A 프레임, UV/Edge/Face 모드 클릭·마키 선택, W/E/R 드래그로 UV 점 이동/회전/스케일(`UvEditCommand` Capture/Commit). 임베디드 창은 루트의 `_Input`을 받지 못하므로 `UvCanvas._UnhandledKeyInput`이 `Shell.Hotkeys._Input`으로 키를 넘긴다. 액션은 `UI/ShellUvActions.cs`(`uv.*`).
- **라이트**(`Scene/Light.cs` `LightShape` Directional/Point/Spot, Create → Lights): `Viewport/LightView.cs`가 실제 Godot 라이트 + 선 아이콘(X-ray)을 그리고 Properties의 Light 그룹(Type/Color/Intensity/Range/Cone Angle → `SetLightCommand`)에서 편집한다. 오브젝트 모드에서 아이콘 중심(12px)으로 피킹. glTF 내보내기에 Light3D로 포함(`.cube`에도 저장).
- **머티리얼**(`Scene/Materials.cs` `MaterialDef` Lambert/BlinnPhong/Pbr/Unlit/Matcap + `TexturePath`, `Document.Materials`, `SceneNode.MaterialId`; 명령은 `Commands/SceneCommands.cs`): Material → Material Editor(`UI/MaterialEditorWindow.cs`: 목록(List/Thumbnails — `UI/MaterialThumbnails.cs`가 머티리얼마다 SubViewport에 구를 렌더해 ItemList 아이콘으로 씀)/New/Delete/Assign to Selected + 속성). 속성 편집기는 `UI/MaterialPropsEditor.cs`로 분리되어 Properties 패널의 Material 그룹(할당 OptionButton + 같은 편집기)에서도 쓴다. 표시는 `Viewport/MaterialCache.cs`가 `assets/shaders/mat_*.gdshader`(blinn/pbr/unlit/matcap; lambert는 surface.gdshader; 모두 `albedo_tex`/`use_texture` 유니폼)로 ShaderMaterial을 만들고 `ViewportDisplay.ApplyStyle`이 `SurfaceMaterial`로 넣으며 `MeshView.MappedTexture`(UV 편집기 Mapped Texture 배경)도 채운다. 이미지는 `MaterialCache.LoadTexture`(경로 캐시). 내보내기는 StandardMaterial3D(AlbedoTexture 포함)로 변환(Unlit/Matcap은 unshaded). 기본 카메라는 `OrbitCamera.MayaDefault()` = (2.8, 2.1, 2.8)에서 원점(1m 큐브가 크게 보이도록).
- **Create Polygon Tool**(`Tools/CreatePolygonTool.cs`, Create 메뉴): 클릭한 점(원근 = 지면, 직교 = 화면 평면)을 오버레이 폴리라인으로 보여 주고 Enter로 `MeshBuilder.Polygon`(법선 힌트로 CCW 보정) 노드를 만든다.
- **스켈레톤 추가 기능**(`UI/ShellSceneActions.cs`, `Rig/JointOps.cs`): Mirror Joint(축 X/Y/Z, 평면 = 월드 원점/부모/선택 조인트, 이름 치환; `JointOps.Mirror`는 월드 반사 후 오른손계 복원), Orient Joint(Maya 옵션: 주축/보조축/보조축 월드 방향/음수/자식 포함; `JointOps.Orient`는 주축이 자식 평균을 향하고 끝 조인트는 부모를 따르며 자식 월드 유지), Insert Joint Tool(`Tools/InsertJointTool.cs`, `Picker.PickBone` → `InsertJointCommand`), Display → Joint Local Rotation Axes(`Settings.ShowJointAxes`, `JointView` 축 선). Move/Rotate/Scale 툴에서 **Ctrl 드래그**는 조인트만 움직이고 자식 월드 트랜스폼을 유지한다(`TransformToolBase._isolate`, 자식 로컬 보정이 같은 `TransformNodesCommand`에 포함).
- **뷰포트 UV 모드**(F12): 정점 위치에 UV 점을 파랑으로, 그 정점의 UV 점이 하나라도 선택되면 빨강으로 그린다(`MeshView.UvNormal/UvSelected`, `MeshView.UvTopo` 캐시). 클릭/마키는 정점을 집은 뒤 `Picker.ExpandUv`로 그 정점의 모든 UV 점 ID로 바꾼다(UV 편집기와 같은 `UvTopology` 순서).
- `Tools/InsertEdgeLoopTool.cs`: 엣지 클릭 위치의 비율 t로 `MeshOps.InsertEdgeLoop`. `SelectTool`은 더블클릭 시 엣지 루프(경계면 보더 루프)/면 셸을 선택한다.
- **스냅(Maya)**: `MoveTool`은 X 홀드 또는 상태 라인 Snap to Grid 토글(`snap.grid`, `Settings.SnapToGrid`) = 그리드 스냅(단위 = Preferences Grid Spacing cm, `Settings.GridSpacingCm`, 기본 100 → `Shell.ApplyGridSettings`가 `GridView.Spacing`에도 적용), V 홀드 또는 Snap to Points 토글(`snap.point`) = 커서 근처 점(정점·조인트·라이트) 스냅(`ViewportPanel.IsGridSnapHeld/IsPointSnapHeld`가 홀드와 토글을 합친다). 점 스냅은 `Settings.RetainComponentSpacing`(기본 on)이 꺼지면 선택 정점/피벗을 모두 그 점으로 모은다(`ApplyCollapseTo`). J 홀드 증분은 `Settings.RotateSnapDegrees`(15)/`ScaleSnapStep`(0.25). 커브/라이브 서피스 스냅은 없음.
- **피벗**: 오브젝트 모드 조작기는 `SceneNode.PivotWorld`에 놓인다. Edit → Center Pivot(`edit.centerPivot`, 메시 AABB 중심), Edit → Edit Pivot(`edit.editPivot`, **Insert** 토글; `Tools/EditPivotTool.cs` = MoveTool 파생으로 피벗만 옮김 `ApplyPivotMove/ApplyPivotTo`, Esc/다른 툴로 종료). Properties Transformation에 Pivot 행(편집은 월드 유지). Edit 파이(오브젝트 모드)에도 있다.
- 셸프는 `TabContainer` 탭 3개(Polygons / UV / Rigging, `Shell.PolyShelf/UvShelf/RigShelf`, `MakeShelfTab`)이며 버튼은 아이콘(위)+텍스트(아래)로 `ShelfButton(action, label, icon)`이 만든다(`_shelfButtons`에 등록되어 `RefreshShelf`가 CanExecute/IsChecked를 반영; IsChecked가 있는 액션은 토글 버튼). 아이콘은 `shelf_*.svg`(폴리곤), `uv_*.svg`(UV), `rig_*/skin_*.svg`(리깅). 아이콘은 `assets/icons/*.svg`가 원본이지만 앱은 `UI/IconData.cs`에 내장된 SVG 문자열을 쓴다(내보낸 빌드에는 .svg/.png 같은 임포트 대상 파일이 포함되지 않는다). SVG를 바꾸면 `python tools/gen-icons.py`로 재생성. UV 그리드 텍스처는 `assets/textures/uv_grid.bin`(PNG 바이트, 임포터 회피)이며 `Icons.LoadPng`로 읽는다. 내보내기 프리셋 include_filter에 `assets/textures/*.bin`이 있다.
- 표면은 `assets/shaders/surface.gdshader`(양면, 뒷면은 검정)로 그린다. 셰이딩/UV 그리드 모드 모두 이 셰이더에 `use_texture` 유니폼만 다르다.
- 선택 옵션: 클릭은 항상 보이는 요소 우선(`Settings.CameraBasedSelection`), 박스(마키) 선택은 `Settings.MarqueeSelectThrough`(기본 on, HUD 토글)에 따라 가려진 요소도 포함한다. 마우스 내비게이션 감도는 `Settings.MouseSensitivityPercent`(기본 80)를 `NavigationHandler`가 곱한다.
- UI 배율: `CubeApp.UiScale = 화면 DPI 배율 × Settings.UiScalePercent(기본 130)`. 창 `ContentScaleFactor`는 쓰지 않는다(3D 뷰포트가 흐려짐). 테마/위젯/픽셀 상수가 모두 `UiScale`을 곱한다. Edit → Preferences(`UI/PreferencesDialog.cs`)에서 바꾸면 `CubeApp.ReloadShell()`이 셸을 다시 만든다(문서 유지). 우측 도크는 Properties(`UI/Docks/PropertiesPanel.cs`, Maya Channel Box 역할). `ViewportDisplay`가 선택/셰이딩 모드를 `MeshView.Style`로 변환. `SceneView`/`MeshView`가 Document를 미러링(표면·와이어·정점·면중심·면 틴트).
- `Tools/`: `SelectTool`(클릭/마키/호버) → `TransformToolBase`(피벗, 축 방향 World/Local(Object)/Normal, 드래그 캡처/커밋) → `MoveTool`/`RotateTool`/`ScaleTool`. 오브젝트 회전/스케일은 행렬 분해 없이 TRS 속성을 직접 갱신한다(비균등 스케일+회전에서도 안전). 축 방향은 툴박스 하단 아이콘 버튼(`axis.world/local/normal`)이며 바꾸면 `ToolContext.AxisOrientationChanged`로 기즈모가 즉시 갱신된다. 조작기는 `Viewport/Gizmos/`(화면 고정 100px, 깊이 무시, CPU 스크린 공간 히트).
- `IO/`: `GltfExporter`(GltfDocument), `GltfImporter`/`FbxImporter`(GenerateScene 순회), `FileActions`/`SceneFileActions`(네이티브 다이얼로그). `DocumentToGodotScene`은 루트 조인트마다 `Skeleton3D`(본 rest = 조인트 로컬, 바인드 포즈 = 내보내기 시점 현재 포즈)를 만들고, 스킨 메시는 그 스켈레톤의 자식 `MeshInstance3D`로 스켈레톤 공간에 베이크(BONES/WEIGHTS 코너당 4개 + `Skin` 역바인드 = skelWorld·inv(jointWorld))한다. 가져오기는 `Skeleton3D` 본을 조인트 노드로, 스킨 메시의 BONES/WEIGHTS를 `TriangleSoupToPolyMesh`의 정점 맵으로 옮기고 바인드 행렬은 rest 포즈에서 계산한다.
- `Viewport/JointView.cs`: 조인트 구 + 자식으로 향하는 팔면체 본, 깊이 테스트 없음(X-ray). `SceneView`가 조인트/스킨을 미러링: 트랜스폼 변경마다 `UpdateSkins`(LBS → `MeshView.SetDeformed`), `RefreshJoints`. `Picker.PickJoint`가 오브젝트 모드에서 조인트 구(10px)/본(6px)을 먼저 집는다. `Tools/JointTool.cs`(클릭마다 체인에 조인트 추가, 원근 = 지면 평면, 직교 = 화면 평면, Enter 완료), `Tools/PaintWeightsTool.cs` + `UI/PaintWeightsWindow.cs`(영향 목록/모드/값/반지름/Flood/Normalize; `Shell.WeightDisplay`로 표면을 흑백 가중치 램프로 표시 — `surface.gdshader`의 `use_vertex_color`). 액션은 `UI/ShellRigActions.cs`(`skeleton.jointTool`, `skin.bind/detach/paintTool/normalize/rebind`).

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
- 버전의 단일 출처는 `project.godot`의 `application/config/version`(현재 `0.0.6`). v0.0.1~v0.0.6은 공개 릴리즈됨.
- **수정 작업을 완료할 때마다** 패치 버전을 하나 올리고(공개된 태그는 재사용 불가) 커밋 → `origin/main` 푸시 → `dist/release-notes-v<ver>.md` 작성 → `gh release create v<ver> --target main --title v<ver> --latest --notes-file <file>`로 **바로 public 릴리즈**(2026-10-07 사용자 지시: 드래프트 아님) → `.	oolsuild-release.ps1 -Upload`로 빌드 산출물 첨부. 마이너/메이저 버전은 사용자가 올리라고 할 때만.
- 푸시: `gh auth setup-git`으로 github.com 자격 증명이 gh(80dots)로 고정되어 있어 `git push origin main`이 팝업 없이 동작한다. 그래도 자동 세션에서는 `GIT_TERMINAL_PROMPT=0`과 `timeout 90`으로 감싼다.
- 릴리즈 노트는 **UTF-8 파일**(Write 도구로 작성, 이전 버전 노트를 아래에 이어 붙임)을 `--notes-file`로 넘긴다. Python/PowerShell 표준 출력을 파이프로 넘기면 Windows 콘솔 인코딩(cp949) 때문에 한글이 깨진다.
- `tools/build-release.ps1 -Upload`: Release 빌드 → Godot Windows 내보내기(`export_presets.cfg`의 "Windows Desktop", `build/windows/`) → `dist/Cube-<ver>-win64.zip` + Inno Setup 인스톨러 `dist/Cube-<ver>-Setup.exe`(`installer/Cube.iss`) → `gh release upload --clobber`. 버전은 project.godot에서 읽으므로 릴리즈(태그)를 먼저 만들어 둔다. 필요 도구: Godot 4.7.2 mono 내보내기 템플릿(`%APPDATA%\Godot\export_templates.7.2.stable.mono\`), Inno Setup 6(`winget install JRSoftware.InnoSetup`).
- GitHub 작업은 항상 80dots 계정.

## 파일 규칙
- UTF-8, LF(`.gitattributes`). `.godot/`, `bin/`, `obj/`, `.idea/`는 추적하지 않는다.
- 원격 `origin` → https://github.com/80dots/Cube.git, 기본 브랜치 `main`.
