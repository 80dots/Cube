# Unity / Godot drop-in 체크리스트 (glTF / FBX)

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

## FBX (자체 바이너리 7.4 writer, M4)
1. 파일은 cm 단위(UnitScaleFactor 1, 정점/이동 ×100). Unity 임포트 설정 "Convert Units" 켜짐(기본)에서 Scale Factor 1 → 1유닛 큐브가 1m. Godot(ufbx) 임포트는 자동으로 m로 변환.
2. 축: Y-up, FrontAxis Z, CoordAxis X(Maya와 같음). Unity에서는 Maya FBX와 같은 규칙으로 들어온다(정면 -Z 기준).
3. 폴리곤은 n각형 그대로(쿼드 유지). 노멀 ByPolygonVertex → 하드/소프트 엣지 유지. UV 세트 이름 `map1`.
4. 머티리얼: Lambert / Phong(DiffuseColor, SpecularColor, Shininess) + 컬러 텍스처(FileName 절대 경로, RelativeFilename은 FBX 파일 기준 상대 경로). 텍스처 파일을 FBX 옆에 두면 Unity/Godot이 바로 찾는다.
5. 조인트: Model LimbNode + NodeAttribute Skeleton 계층. Unity Rig 탭에서 Generic/Humanoid 아바타를 만들 수 있다.
6. 스킨: Deformer Skin/Cluster + BindPose. 바인드 포즈 = 내보낸 시점의 포즈. Unity에서 SkinnedMeshRenderer가 만들어지고 본을 움직이면 메시가 따라와야 한다.
7. 라이트: Point/Directional/Spot NodeAttribute(Intensity ×100). Unity는 "Import Lights"가 켜진 경우에만 가져온다.

## 알려진 제한
- 애니메이션(Takes)은 없다(바인드 포즈까지).
- 머티리얼 텍스처는 컬러(Diffuse) 한 장만.
