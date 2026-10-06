# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 프로젝트 개요

Godot 4.7 기반 3D 프로젝트 "Cube". 아직 씬(`.tscn`)과 스크립트가 없는 초기 템플릿 상태이며, `project.godot`만 설정되어 있다.

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

## 파일 규칙

- `.editorconfig`: 모든 파일 UTF-8.
- `.gitattributes`: 텍스트 파일 줄바꿈은 LF로 정규화. Windows에서 작업하더라도 CRLF를 커밋하지 않는다.
- `.gitignore`: `.godot/`(엔진 캐시)과 `/android/`는 추적하지 않는다. `.godot/`은 언제든 `--import`로 재생성 가능하므로 직접 수정하지 않는다.
- git 저장소는 아직 초기화되지 않았다(`git init` 미실행).
