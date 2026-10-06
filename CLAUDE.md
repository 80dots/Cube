# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

Godot 4.7 기반 3D 프로젝트 "Cube". 메인 씬은 `scenes/Main.tscn`(Node3D 루트 + Camera3D, DirectionalLight3D, BoxMesh 큐브)이며 `run/main_scene`으로 지정되어 있다. 스크립트는 아직 없다.

`project.godot`에 고정된 주요 설정:
- 렌더러: Forward Plus, Windows 렌더링 드라이버 `d3d12`
- 3D 물리 엔진: Jolt Physics
- 창 스트레치: `canvas_items` / `expand`
- .NET 어셈블리 이름: `Cube` (C# 프로젝트로 설정됨)

## 엔진 및 도구 환경

- **사용할 Godot 실행 파일** (이 프로젝트의 `.godot/mono/metadata`가 가리키는 버전):
  `D:\Godot\GodotEngine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`
  - `C:\Projects\Godot\Engine\` 아래에는 4.6.1이 있으나 `project.godot`의 `config/features`가 4.7이므로 사용하지 말 것.
  - 콘솔 출력이 필요하면 `_console.exe`, 에디터 GUI만 띄울 때는 `.exe`를 사용한다.
- .NET SDK 10.0 (`dotnet --version` → 10.0.203). C# 스크립트를 쓰려면 mono 빌드 엔진이 필요하다.
- `.csproj` / `.sln`은 아직 없다. 에디터에서 첫 C# 스크립트를 만들면 Godot이 자동 생성하며, 그 뒤부터 `dotnet build`가 가능하다.

## 자주 쓰는 명령

프로젝트 루트(`C:\Projects\Godot\Projects\Cube`)에서 실행한다. 아래는 PowerShell 기준이다.

```powershell
$godot = "D:\Godot\GodotEngine\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"

# 에디터 열기
& $godot -e --path .

# 메인 씬 실행 (project.godot의 run/main_scene 필요)
& $godot --path .

# 특정 씬 실행
& $godot --path . res://scenes/Main.tscn

# 헤드리스 실행 (창 없이 스크립트 동작 확인)
& $godot --headless --path . res://scenes/Main.tscn

# 에디터를 열지 않고 리소스 임포트만 수행 (.godot/imported 갱신)
& $godot --headless --path . --import

# C# 빌드 (.csproj 생성 이후)
dotnet build
```

테스트 프레임워크(GUT, gdUnit4, GodotTestDriver 등)는 아직 도입되지 않았다.

## Godot MCP 서버

`godot` MCP 서버(`@coding-solo/godot-mcp`)가 user 스코프(`~/.claude.json`)에 등록되어 있다. 에디터 애드온 없이 `GODOT_PATH`로 지정된 4.7.2 콘솔 실행 파일을 직접 구동한다.

- 주요 도구: `launch_editor`, `run_project`, `get_debug_output`, `stop_project`, `get_project_info`, `create_scene`, `add_node`, `save_scene`, `get_uid`, `update_project_uids`
- 프로젝트 실행 후 런타임 에러를 확인할 때는 `run_project` → `get_debug_output` → `stop_project` 순으로 쓴다.
- `add_node`의 `properties`(position, rotation_degrees 등)는 `.tscn`에 저장되지 않는다(4.7.2에서 확인). 변환값·메시·머티리얼 같은 속성은 `.tscn`을 직접 편집한다. `create_scene`의 루트 노드 이름도 `root`로 고정되므로 필요하면 파일에서 바꾼다.
- 같은 `.tscn`에 대한 MCP 호출은 파일을 통째로 다시 쓰므로 병렬로 보내지 말고 순차 실행한다.
- 서버가 보이지 않으면 `claude mcp get godot`으로 상태를 확인한다. 재등록 시 Git Bash에서는 `MSYS_NO_PATHCONV=1`을 켜야 `cmd /c`의 `/c`가 경로로 변환되지 않는다.

## 버전 및 릴리즈 워크플로

- 버전의 단일 출처는 `project.godot`의 `application/config/version`이다 (초기값 `0.0.1`).
- **수정 작업을 완료할 때마다** 현재 버전을 그대로 유지한 채 커밋하고 `origin/main`에 푸시한 뒤, 해당 버전의 **드래프트 릴리즈**를 만든다. 버전은 사용자가 올리라고 할 때만 올린다.
- 드래프트 릴리즈는 `gh release create v<version> --draft --target main` 으로 만든다. 같은 버전의 드래프트가 이미 있으면 새로 만들지 말고 `gh release edit v<version> --notes ...`로 노트를 갱신한다.
- GitHub 작업은 항상 80dots 계정으로 한다.

## 파일 규칙

- `.editorconfig`: 모든 파일 UTF-8.
- `.gitattributes`: 텍스트 파일 줄바꿈은 LF로 정규화. Windows에서 작업하더라도 CRLF를 커밋하지 않는다.
- `.gitignore`: `.godot/`(엔진 캐시)과 `/android/`는 추적하지 않는다. `.godot/`은 언제든 `--import`로 재생성 가능하므로 직접 수정하지 않는다.
- 원격은 `origin` → https://github.com/80dots/Cube.git, 기본 브랜치 `main`.
