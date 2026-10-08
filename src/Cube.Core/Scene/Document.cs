using Cube.Core.Commands;
using Cube.Core.Selection;

namespace Cube.Core.Scene;

/// <summary>문서 변경 통지의 종류. 뷰(SceneView/MeshView/Outliner/Properties)는 종류에 따라 갱신 범위를 정한다.</summary>
public enum ChangeKind
{
    /// <summary>문서 전체 교체.</summary>
    Reset,            // 문서 전체 교체(새 문서/열기)
    /// <summary>노드(와 하위 트리) 추가.</summary>
    NodeAdded,
    /// <summary>노드(와 하위 트리) 제거.</summary>
    NodeRemoved,
    /// <summary>노드 이름 변경.</summary>
    NodeRenamed,
    /// <summary>부모 변경(계층 이동).</summary>
    NodeReparented,
    /// <summary>로컬 트랜스폼(TRS/피벗) 변경.</summary>
    TransformChanged,
    /// <summary>표시/숨김 변경.</summary>
    VisibilityChanged,
    /// <summary>메시 위상 변경.</summary>
    MeshTopology,     // 위상 변경 → 전체 재빌드
    /// <summary>정점 위치만 변경.</summary>
    MeshGeometry,     // 위치만 변경 → 포지션 갱신
    /// <summary>위상은 같고 코너/엣지 속성 변경.</summary>
    MeshAttributes,   // 노멀/UV/하드엣지 등 → 전체 재빌드(위상 동일)
    /// <summary>스킨 변경.</summary>
    SkinChanged,      // skinCluster 부착/제거/가중치 변경 → 변형·가중치 표시 갱신
    /// <summary>구성 이력 변경.</summary>
    HistoryChanged,   // 구성 이력 목록 변경(메시는 그대로)
    /// <summary>라이트 변경.</summary>
    LightChanged,     // 라이트 속성 변경
    /// <summary>머티리얼 변경.</summary>
    MaterialChanged,  // 머티리얼 라이브러리(Node=None) 또는 노드 할당/속성 변경
    /// <summary>표시 옵션 변경.</summary>
    DisplayChanged,   // 표시 옵션(스무스 프리뷰 등) 변경 → 뷰 재빌드
    /// <summary>애니메이션 클립 목록 변경.</summary>
    AnimationsChanged, // 애니메이션 클립 목록 변경(Node=None)
    /// <summary>재생 포즈 변경.</summary>
    PoseChanged, // 재생 포즈(SceneNode.Pose) 변경(Node=None). 문서 데이터는 그대로
    /// <summary>선택 변경(SelectionState.Changed를 문서 통지로 중계; 더티 표시 안 함).</summary>
    Selection,
}

/// <summary>문서 변경 통지 하나: 종류와 대상 노드(전역 변경이면 <see cref="NodeId.None"/>).</summary>
public readonly record struct DocChange(ChangeKind Kind, NodeId Node);

/// <summary>
/// 편집 문서. 씬 DAG, 선택 상태, Undo 스택을 소유한다. Godot을 모르며 변경은 <see cref="Changed"/>로만 알린다.
/// </summary>
/// <remarks>
/// 트리는 보이지 않는 <see cref="Root"/> 아래에 매달리고, 모든 노드는 ID → 노드 사전(_nodes)으로도 색인된다.
/// 노드 ID는 증가만 하는 카운터로 배정되어 세션 중 재사용되지 않는다(Undo로 다시 넣은 노드는 원래 ID 유지).
/// </remarks>
public sealed class Document
{
    /// <summary>다음에 배정할 노드 ID 값(1부터).</summary>
    private int _nextId = 1;
    /// <summary>ID → 노드 색인(루트 제외, 트리에 들어 있는 모든 노드).</summary>
    private readonly Dictionary<NodeId, SceneNode> _nodes = new();

    /// <summary>보이지 않는 최상위 노드(ID None, IsRoot).</summary>
    public SceneNode Root { get; }
    /// <summary>문서의 모든 노드(읽기 전용 색인).</summary>
    public IReadOnlyDictionary<NodeId, SceneNode> Nodes => _nodes;
    /// <summary>선택 상태(모드, 오브젝트, 컴포넌트).</summary>
    public SelectionState Selection { get; }
    /// <summary>문서 전용 Undo 스택.</summary>
    public UndoStack Undo { get; }
    /// <summary>문서 머티리얼 라이브러리(ID 1부터; 0은 기본 lambert1).</summary>
    public List<MaterialDef> Materials { get; } = new();
    /// <summary>가져온 애니메이션 클립(보기·재생·내보내기 전용; Cube는 키를 만들거나 편집하지 않는다).</summary>
    public List<AnimationClip> Animations { get; } = new();
    /// <summary>다음에 배정할 머티리얼 ID.</summary>
    private int _nextMaterialId = 1;
    /// <summary>새 머티리얼 ID를 배정한다(증가만 함).</summary>
    public int NextMaterialId() => _nextMaterialId++;
    /// <summary>baseName 뒤에 1부터 번호를 붙여 기존 머티리얼과 겹치지 않는 이름을 만든다.</summary>
    public string UniqueMaterialName(string baseName)
    {
        var used = new HashSet<string>(Materials.Select(m => m.Name));
        for (int i = 1; ; i++) { string cand = baseName + i; if (!used.Contains(cand)) return cand; }
    }
    /// <summary>ID로 머티리얼을 찾는다(0 이하 = 기본 머티리얼 → null).</summary>
    public MaterialDef? FindMaterial(int id) => id <= 0 ? null : Materials.FirstOrDefault(m => m.Id == id);
    /// <summary>파일 로드 등 ID가 이미 있는 머티리얼을 넣을 때.</summary>
    /// <remarks>다음 배정 ID가 기존 ID와 겹치지 않도록 카운터를 앞당긴다.</remarks>
    public void AddMaterialWithId(MaterialDef m) { Materials.Add(m); if (m.Id >= _nextMaterialId) _nextMaterialId = m.Id + 1; }
    /// <summary>현재 저장 경로(.cube). 새 문서면 null.</summary>
    public string? FilePath { get; set; }
    /// <summary>저장되지 않은 변경이 있는지(선택 변경은 제외).</summary>
    public bool IsDirty { get; set; }

    /// <summary>모든 문서 변경 통지. 리스너는 Godot 측 뷰들이다.</summary>
    public event Action<DocChange>? Changed;

    /// <summary>빈 문서를 만든다: 루트, 선택 상태, Undo 스택을 생성하고 선택 변경을 문서 통지로 중계한다.</summary>
    public Document()
    {
        Root = new SceneNode { Name = "root", IsRoot = true, Id = NodeId.None };
        Selection = new SelectionState(this);
        Undo = new UndoStack(this);
        Selection.Changed += () => Notify(new DocChange(ChangeKind.Selection, NodeId.None));
    }

    /// <summary>새 노드 ID를 배정한다.</summary>
    public NodeId AllocateId() => new(_nextId++);

    /// <summary>ID로 노드를 찾는다(없으면 null).</summary>
    public SceneNode? Find(NodeId id) => _nodes.TryGetValue(id, out var n) ? n : null;

    /// <summary>ID로 노드를 얻는다(없으면 KeyNotFoundException).</summary>
    public SceneNode Get(NodeId id) => _nodes.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException($"node {id}");

    /// <summary>노드를 트리에 넣는다. Id가 None이면 새로 할당한다(Undo 재삽입은 기존 Id 유지).</summary>
    /// <param name="node">넣을 노드(하위 트리 포함).</param>
    /// <param name="parent">부모(null이면 루트).</param>
    /// <param name="index">형제 중 삽입 위치(-1 또는 범위 밖이면 끝).</param>
    public void AddNode(SceneNode node, SceneNode? parent = null, int index = -1)
    {
        // ID 배정과 중복 검사.
        if (node.Id.IsNone) node.Id = AllocateId();
        if (_nodes.ContainsKey(node.Id)) throw new InvalidOperationException($"node {node.Id} already in document");
        // 부모의 자식 목록에 연결.
        parent ??= Root;
        node.Parent = parent;
        if (index < 0 || index > parent.Children.Count) parent.Children.Add(node); else parent.Children.Insert(index, node);
        // 자신과 하위 노드를 모두 색인(하위의 빈 ID도 이때 배정).
        _nodes[node.Id] = node;
        foreach (var d in node.Descendants()) { if (d.Id.IsNone) d.Id = AllocateId(); _nodes[d.Id] = d; }
        IsDirty = true;
        Notify(new DocChange(ChangeKind.NodeAdded, node.Id));
    }

    /// <summary>노드와 하위 전체에 ID를 미리 배정한다(이미 있으면 유지). 문서에 넣기 전에 ID로 참조해야 할 때(가져온 스킨의 조인트 등) 쓴다.</summary>
    public void AssignIds(SceneNode node)
    {
        if (node.Id.IsNone) node.Id = AllocateId();
        foreach (var d in node.Descendants()) if (d.Id.IsNone) d.Id = AllocateId();
    }

    /// <summary>노드(와 하위 트리)를 트리에서 뗀다. 객체는 보존되어 다시 AddNode 할 수 있다. 반환값은 부모 내 인덱스.</summary>
    public int RemoveNode(SceneNode node)
    {
        if (node.Parent == null) return -1;
        // 부모에서 분리하고 색인에서 자신과 하위를 제거.
        int index = node.Parent.Children.IndexOf(node);
        node.Parent.Children.Remove(node);
        _nodes.Remove(node.Id);
        foreach (var d in node.Descendants()) _nodes.Remove(d.Id);
        // 선택에서도 조용히 제거(통지는 아래 NodeRemoved 한 번으로).
        Selection.RemoveObject(node.Id, silent: true);
        foreach (var d in node.Descendants()) Selection.RemoveObject(d.Id, silent: true);
        node.Parent = null;
        IsDirty = true;
        Notify(new DocChange(ChangeKind.NodeRemoved, node.Id));
        return index;
    }

    /// <summary>노드를 다른 부모 아래로 옮긴다(로컬 트랜스폼은 그대로이므로 월드 유지가 필요하면 호출자가 보정).</summary>
    public void Reparent(SceneNode node, SceneNode newParent, int index = -1)
    {
        if (node.Parent != null) node.Parent.Children.Remove(node);
        node.Parent = newParent;
        if (index < 0 || index > newParent.Children.Count) newParent.Children.Add(node); else newParent.Children.Insert(index, node);
        IsDirty = true;
        Notify(new DocChange(ChangeKind.NodeReparented, node.Id));
    }

    /// <summary>통지마다 증가하는 일련번호. 같은 통지를 받는 여러 리스너(뷰포트 패널 4개)가 비싼 계산(스킨 변형)을 한 번만 하고 공유하는 키.</summary>
    public int ChangeSerial { get; private set; }

    /// <summary>변경을 알린다. 선택 외 변경은 더티로 표시하고 일련번호를 올린 뒤 리스너를 호출한다.</summary>
    public void Notify(DocChange change)
    {
        if (change.Kind != ChangeKind.Selection) IsDirty = true;
        ChangeSerial++;
        Changed?.Invoke(change);
    }

    /// <summary>문서를 비운다(새 문서). Undo 이력도 지운다.</summary>
    public void Clear()
    {
        // 트리·색인·카운터 초기화.
        foreach (var c in Root.Children.ToArray()) { c.Parent = null; }
        Root.Children.Clear();
        _nodes.Clear();
        _nextId = 1;
        Materials.Clear(); _nextMaterialId = 1;
        Animations.Clear();
        Selection.ClearAll(silent: true);
        Undo.Clear();
        FilePath = null;
        IsDirty = false;
        Notify(new DocChange(ChangeKind.Reset, NodeId.None));
        // Notify가 IsDirty를 true로 만들므로 다시 내린다.
        IsDirty = false;
    }

    /// <summary>모든 라이트 노드.</summary>
    public IEnumerable<SceneNode> LightNodes()
    {
        foreach (var n in _nodes.Values) if (n.IsLight) yield return n;
    }

    /// <summary>모든 조인트 노드.</summary>
    public IEnumerable<SceneNode> JointNodes()
    {
        foreach (var n in _nodes.Values) if (n.IsJoint) yield return n;
    }

    /// <summary>skinCluster가 붙은 메시 노드.</summary>
    public IEnumerable<SceneNode> SkinnedNodes()
    {
        foreach (var n in _nodes.Values) if (n.Skin != null) yield return n;
    }

    /// <summary>표시 가능한 모든 메시 노드.</summary>
    public IEnumerable<SceneNode> MeshNodes()
    {
        foreach (var n in _nodes.Values) if (n.MeshShape != null) yield return n;
    }

    /// <summary>이름이 겹치지 않도록 접미 번호를 붙인다(Maya: pCube1, pCube2 ...).</summary>
    /// <remarks>baseName이 비어 있지 않고 숫자로 끝나지 않으며 미사용이면 그대로 쓴다. 아니면 끝 숫자를 뗀 stem에 1부터 번호를 붙여 첫 빈 이름을 고른다.</remarks>
    public string UniqueName(string baseName)
    {
        var used = new HashSet<string>();
        foreach (var n in _nodes.Values) used.Add(n.Name);
        if (!used.Contains(baseName) && !char.IsDigit(baseName[^1])) return baseName;
        // 끝 숫자 제거(전부 숫자면 원래 이름을 stem으로).
        string stem = baseName.TrimEnd("0123456789".ToCharArray());
        if (stem.Length == 0) stem = baseName;
        for (int i = 1; ; i++)
        {
            string cand = stem + i;
            if (!used.Contains(cand)) return cand;
        }
    }
}
