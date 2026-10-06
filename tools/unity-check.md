# Unity / Godot drop-in 체크리스트 (glTF)

Cube가 내보낸 `.glb`를 엔진에 넣었을 때 확인할 항목. 내부 좌표계는 Godot 규약(오른손, Y-up, -Z forward, m)이며 glTF는 Godot 내장 익스포터가 쓴다.

## Godot (4.x 새 프로젝트)
1. `.glb`를 프로젝트 폴더에 복사 → 자동 임포트.
2. 씬에 인스턴스: 1유닛 큐브가 1m, 원점에 생성한 오브젝트가 원점에 있어야 한다.
3. 노드 이름이 Cube의 Outliner 이름과 같다(`pCube1`, `polySurface1` ...).
4. 하드 엣지로 만든 큐브는 면별 노멀(각진 셰이딩), 구는 부드러운 셰이딩.
5. 뒤집힌 면 없음(백페이스 컬링 상태에서 바깥면이 보임).

## Unity (glTFast 또는 UnityGLTF)
1. `.glb`를 Assets에 복사 → 임포트.
2. 임포트된 루트의 Scale이 1, 1유닛 큐브의 메시 bounds가 1m.
3. 트랜스폼 노드가 Hierarchy에 Cube 이름 그대로 나타난다.
4. Scene 뷰에서 노멀이 바깥을 향한다(기본 머티리얼로 안쪽이 보이면 안 됨).
5. 좌표: Cube에서 +X로 옮긴 오브젝트가 Unity에서도 +X에 있다(glTF→Unity 변환은 임포터가 Z를 뒤집어 처리). -Z forward 오브젝트는 Unity에서 +Z forward 로 보일 수 있으니 캐릭터 정면은 Cube의 -Z를 기준으로 만든다.
6. UV: 텍스처를 입혔을 때 상하 반전 없음(Cube는 하단 원점 UV를 glTF 상단 원점으로 변환해 쓴다).

## 알려진 제한 (M1)
- 머티리얼은 회색 `lambert1` 하나만 내보낸다.
- FBX 내보내기는 M4에서 자체 writer로 추가한다. FBX 가져오기는 Godot(ufbx)로 가능.
