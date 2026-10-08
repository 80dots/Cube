using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// Document의 DAG를 Godot Node3D 트리로 미러링한다. DocChange를 받아 추가/제거/트랜스폼/메시 변경을 동기화한다.
/// </summary>
/// <remarks>
/// 뷰포트 패널마다 SceneView가 하나씩 있다(패널 4개 = 미러 4벌, 각자 자체 World3D). 문서 노드마다 뷰 노드를 하나 만들고
/// Godot 부모-자식 관계를 문서 DAG와 같게 유지하므로, 각 뷰에는 로컬 트랜스폼(<c>SceneNode.Evaluated</c> = 재생 포즈 또는 Local)만 넣으면 된다.
/// 셰이프 종류에 따라 MeshView/JointView/LightView, 셰이프가 없으면 빈 Node3D(그룹)를 만든다.
/// 스킨 메시의 LBS 변형은 <see cref="SkinDeformCache"/>가 패널 사이에서 공유하며, 숨은 패널은 갱신을 미뤘다가 <see cref="FlushSkins"/>로 반영한다.
/// </remarks>
public partial class SceneView : Node3D
{
    /// <summary>미러링 중인 문서(null = 아직 Bind 전).</summary>
    private Document? _doc;
    /// <summary>문서 노드 ID → 뷰 노드(모든 종류).</summary>
    private readonly Dictionary<NodeId, Node3D> _views = new();
    /// <summary>메시 노드 ID → MeshView(피킹·스타일 적용용 빠른 조회).</summary>
    private readonly Dictionary<NodeId, MeshView> _meshViews = new();
    /// <summary>조인트 노드 ID → JointView.</summary>
    private readonly Dictionary<NodeId, JointView> _jointViews = new();
    /// <summary>라이트 노드 ID → LightView.</summary>
    private readonly Dictionary<NodeId, LightView> _lightViews = new();
    /// <summary>라이트 뷰 읽기 전용 목록(피킹·마키용).</summary>
    public IReadOnlyDictionary<NodeId, LightView> LightViews => _lightViews;

    /// <summary>메시 뷰 읽기 전용 목록.</summary>
    public IReadOnlyDictionary<NodeId, MeshView> MeshViews => _meshViews;
    /// <summary>조인트 뷰 읽기 전용 목록.</summary>
    public IReadOnlyDictionary<NodeId, JointView> JointViews => _jointViews;
    /// <summary>전체 재구성(<c>RebuildAll</c>)이 끝났을 때. ViewportDisplay가 모든 스타일을 다시 적용한다.</summary>
    public event Action? Rebuilt;
    /// <summary>스킨/가중치가 바뀌어 스타일을 다시 적용해야 할 때.</summary>
    public event Action<MeshView>? StyleRefreshRequested;
    /// <summary>새 MeshView가 트리에 들어간 직후(스타일 적용용).</summary>
    public event Action<MeshView>? MeshViewCreated;

    /// <summary>문서에 연결한다(이전 문서 구독 해제 → 새 문서 구독 → 전체 뷰 재구성).</summary>
    public void Bind(Document doc)
    {
        if (_doc != null) _doc.Changed -= OnDocChanged;
        _doc = doc;
        _doc.Changed += OnDocChanged;
        RebuildAll();
    }

    /// <summary>트리에서 빠질 때 문서 이벤트 구독을 해제한다(셸 재생성 시 누수 방지).</summary>
    public override void _ExitTree()
    {
        if (_doc != null) _doc.Changed -= OnDocChanged;
    }

    /// <summary>노드 ID의 MeshView(없으면 null).</summary>
    public MeshView? GetMeshView(NodeId id) => _meshViews.TryGetValue(id, out var v) ? v : null;
    /// <summary>노드 ID의 뷰 노드(종류 무관, 없으면 null).</summary>
    public Node3D? GetView(NodeId id) => _views.TryGetValue(id, out var v) ? v : null;

    /// <summary>
    /// 문서 변경 통지를 종류별로 처리한다: 노드 추가/제거/부모 변경 → 뷰 트리 동기화, 트랜스폼·포즈 → 뷰 트랜스폼 + 조인트 본 + 스킨 변형,
    /// 메시 위상/속성/표시 → MeshView.Rebuild, 메시 위치 → 위치만 갱신(스킨 메시면 변형 재계산), 라이트·머티리얼·스킨 → 해당 뷰 갱신 또는 스타일 재적용 요청.
    /// </summary>
    private void OnDocChanged(DocChange c)
    {
        switch (c.Kind)
        {
            // 문서 교체/전체 리셋: 변형 캐시를 비우고 전부 다시 만든다
            case ChangeKind.Reset: SkinDeformCache.Clear(); RebuildAll(); break;
            case ChangeKind.NodeAdded: AddView(_doc!.Get(c.Node)); RefreshJoints(); UpdateSkins(); break;
            case ChangeKind.NodeRemoved: RemoveView(c.Node); SkinDeformCache.Forget(c.Node); RefreshJoints(); UpdateSkins(); break;
            case ChangeKind.SkinChanged:
                UpdateSkin(c.Node);
                if (_meshViews.TryGetValue(c.Node, out var sv)) StyleRefreshRequested?.Invoke(sv);
                break;
            case ChangeKind.LightChanged:
                if (_lightViews.TryGetValue(c.Node, out var lv)) lv.Refresh();
                break;
            // 머티리얼 변경: 노드가 None이면 머티리얼 정의 자체가 바뀐 것이라 모든 메시 스타일을 다시 적용
            case ChangeKind.MaterialChanged:
                if (c.Node.IsNone) { foreach (var m in _meshViews.Values) StyleRefreshRequested?.Invoke(m); }
                else if (_meshViews.TryGetValue(c.Node, out var mm)) StyleRefreshRequested?.Invoke(mm);
                break;
            // 부모 변경: 뷰를 새 부모 뷰로 옮기고(로컬 트랜스폼 그대로 다시 설정) 조인트/스킨 갱신
            case ChangeKind.NodeReparented:
                {
                    var n = _doc!.Get(c.Node);
                    if (_views.TryGetValue(c.Node, out var v))
                    {
                        var newParent = ParentViewFor(n);
                        if (v.GetParent() != newParent) v.Reparent(newParent, keepGlobalTransform: false);
                        v.Transform = n.Evaluated.ToGodot();
                    }
                    RefreshJoints(); UpdateSkins();
                    break;
                }
            // 트랜스폼 변경: 조인트나 조인트의 자식이면 본 모양이 바뀌므로 조인트를 다시 그리고, 스킨은 항상 다시 변형
            case ChangeKind.TransformChanged:
                if (_views.TryGetValue(c.Node, out var tv)) tv.Transform = _doc!.Get(c.Node).Evaluated.ToGodot();
                if (_doc!.Get(c.Node).IsJoint || _doc.Get(c.Node).Parent?.IsJoint == true) RefreshJoints();
                UpdateSkins();
                break;
            case ChangeKind.PoseChanged:
                // 숨은 패널(단일 뷰의 나머지 3개)은 아무것도 하지 않고 다시 보일 때 한 번에 반영한다
                if (IsShown != null && !IsShown()) { _poseDirty = true; break; }
                ApplyPose();
                break;
            case ChangeKind.VisibilityChanged:
                if (_views.TryGetValue(c.Node, out var vv)) vv.Visible = _doc!.Get(c.Node).Visible;
                break;
            case ChangeKind.NodeRenamed:
                if (_views.TryGetValue(c.Node, out var rv)) rv.Name = _doc!.Get(c.Node).Name;
                break;
            // 위상/속성/스무스 프리뷰 표시 변경: 테셀레이션부터 다시
            case ChangeKind.MeshTopology:
            case ChangeKind.MeshAttributes:
            case ChangeKind.DisplayChanged:
                if (_meshViews.TryGetValue(c.Node, out var mv)) mv.Rebuild();
                break;
            case ChangeKind.MeshGeometry:
                // 스킨 메시는 변형 위치를 다시 계산해야 바뀐 정점이 보인다(캐시가 같은 통지 안에서는 한 번만 계산)
                if (_doc!.Find(c.Node)?.Skin != null) UpdateSkin(c.Node);
                else if (_meshViews.TryGetValue(c.Node, out var gv)) gv.UpdatePositions();
                break;
        }
    }

    /// <summary>노드의 부모 뷰: 부모가 루트가 아니고 뷰가 있으면 그 뷰, 아니면 SceneView 자신.</summary>
    private Node3D ParentViewFor(SceneNode n)
        => n.Parent != null && !n.Parent.IsRoot && _views.TryGetValue(n.Parent.Id, out var p) ? p : this;

    /// <summary>
    /// 노드의 뷰를 셰이프 종류에 맞게 만들어 부모 뷰 아래에 붙이고(이미 있으면 무시), 자식 노드들도 재귀로 추가한다.
    /// MeshView면 트리에 들어간 직후 <see cref="MeshViewCreated"/>로 스타일 적용을 요청한다.
    /// </summary>
    private void AddView(SceneNode n)
    {
        if (_views.ContainsKey(n.Id)) return;
        // 셰이프 종류별 뷰 생성(메시 > 조인트 > 라이트 > 빈 그룹)
        Node3D view;
        if (n.MeshShape != null)
        {
            var mv = new MeshView(n);
            _meshViews[n.Id] = mv;
            view = mv;
        }
        else if (n.IsJoint)
        {
            var jv = new JointView(n);
            _jointViews[n.Id] = jv;
            view = jv;
        }
        else if (n.IsLight)
        {
            var lv = new LightView(n);
            _lightViews[n.Id] = lv;
            view = lv;
        }
        else view = new Node3D { Name = n.Name };
        // 초기 트랜스폼·가시성을 넣고 부모 뷰에 붙인다
        view.Transform = n.Evaluated.ToGodot();
        view.Visible = n.Visible;
        _views[n.Id] = view;
        ParentViewFor(n).AddChild(view);
        if (view is MeshView created) MeshViewCreated?.Invoke(created);
        foreach (var c in n.Children) AddView(c);
    }

    /// <summary>노드 뷰를 제거한다. 하위 뷰는 Godot 트리와 함께 해제되므로 조회 사전에서만 지우고, 마지막에 QueueFree.</summary>
    private void RemoveView(NodeId id)
    {
        if (!_views.TryGetValue(id, out var v)) return;
        // 하위 뷰 사전도 정리
        foreach (var (k, child) in _views.ToArray())
            if (k != id && IsDescendantOf(child, v)) { _views.Remove(k); _meshViews.Remove(k); _jointViews.Remove(k); _lightViews.Remove(k); }
        _views.Remove(id); _meshViews.Remove(id); _jointViews.Remove(id); _lightViews.Remove(id);
        v.QueueFree();
    }

    /// <summary>재생 포즈 반영: 모든 뷰 트랜스폼(Evaluated) + 조인트 본 + 스킨 변형.</summary>
    private void ApplyPose()
    {
        // 모든 뷰 로컬 트랜스폼을 포즈 반영 값으로 → 조인트 본 → 스킨 변형 순(각 단계 AnimPerf 측정)
        _poseDirty = false;
        long t0 = AnimPerf.Begin();
        foreach (var (id, view) in _views)
            if (_doc!.Find(id) is { } pn) view.Transform = pn.Evaluated.ToGodot();
        AnimPerf.End("view.xform", t0);
        long t1 = AnimPerf.Begin();
        RefreshJoints();
        AnimPerf.End("view.joints", t1);
        UpdateSkins();
    }

    /// <summary>모든 조인트의 본(자식 위치)을 다시 그린다(바뀌지 않은 조인트는 JointView가 건너뛴다).</summary>
    /// <summary>씬 라이트(LightView의 Godot 라이트)를 켤지: Use All Lights 모드에서만 켠다. 아이콘은 항상 보인다.</summary>
    /// <remarks>LightView.Refresh가 부모 SceneView의 이 값을 읽어 Godot 라이트의 Visible을 정한다.</remarks>
    public bool SceneLightsOn { get; private set; }
    /// <summary>씬 라이트 켜기/끄기를 설정하고 모든 LightView에 반영한다(패널 라이팅 모드 변경 시 호출).</summary>
    public void SetSceneLights(bool on)
    {
        SceneLightsOn = on;
        foreach (var lv in _lightViews.Values) lv.Refresh();
    }

    /// <summary>모든 조인트의 본(자식 위치)을 다시 그린다(바뀌지 않은 조인트는 JointView가 건너뛴다).</summary>
    public void RefreshJoints() { foreach (var jv in _jointViews.Values) jv.Refresh(); }

    /// <summary>스킨 메시 전체의 변형 위치를 다시 계산한다(조인트 이동 등).</summary>
    public void UpdateSkins()
    /// <remarks>
    /// 패널이 숨어 있으면 dirty만 표시하고 돌아간다. 보이면 스킨 노드/뷰 목록을 모은 뒤 ① 캐시 변형(병렬) ② 렌더 배열 위치 갱신(병렬)
    /// ③ Godot 업로드(메인 스레드 순차) 세 단계로 처리한다.
    /// </remarks>
    {
        if (_doc == null) return;
        if (IsShown != null && !IsShown()) { _skinsDirty = true; return; }
        _skinsDirty = false;
        _skinNodes.Clear(); _skinViews.Clear();
        foreach (var n in _doc.SkinnedNodes())
        {
            if (n.Mesh == null || !_meshViews.TryGetValue(n.Id, out var mv)) continue;
            _skinNodes.Add(n); _skinViews.Add(mv);
        }
        if (_skinNodes.Count == 0) return;
        // 1) 변형(LBS): 통지 일련번호로 패널 4개가 공유, 메시별 병렬
        long t0 = AnimPerf.Begin();
        SkinDeformCache.Update(_doc, _skinNodes);
        AnimPerf.End("skin.deform", t0);
        // 2) 렌더 배열 위치 갱신: 순수 C#이라 메시별 병렬
        long t1 = AnimPerf.Begin();
        for (int i = 0; i < _skinViews.Count; i++) _skinViews[i].SetDeformedNoUpload(SkinDeformCache.Get(_doc, _skinNodes[i])); // 캐시 접근은 순차로
        if (_skinViews.Count == 1) _skinViews[0].PreparePositions();
        else Parallel.For(0, _skinViews.Count, i => _skinViews[i].PreparePositions());
        AnimPerf.End("mesh.updatePositions", t1);
        // 3) 업로드(메인 스레드)
        long t2 = AnimPerf.Begin();
        foreach (var mv in _skinViews) mv.CommitPositions();
        AnimPerf.End("skin.setDeformed", t2);
    }

    /// <summary><see cref="UpdateSkins"/>가 매번 다시 채우는 스킨 노드 목록(할당 재사용).</summary>
    private readonly List<SceneNode> _skinNodes = new();
    /// <summary><see cref="UpdateSkins"/>의 스킨 노드와 같은 순서의 MeshView 목록.</summary>
    private readonly List<MeshView> _skinViews = new();

    /// <summary>이 뷰를 담은 패널이 보이는지. 안 보이면 포즈/스킨 갱신을 미룬다(<see cref="FlushSkins"/>).</summary>
    public Func<bool>? IsShown;
    /// <summary>숨어 있는 동안 미룬 스킨 갱신 / 포즈 반영이 있는지.</summary>
    private bool _skinsDirty, _poseDirty;

    /// <summary>숨어 있는 동안 미룬 포즈·스킨 변형을 반영한다.</summary>
    public void FlushSkins()
    {
        if (_poseDirty) ApplyPose();
        else if (_skinsDirty) UpdateSkins();
    }

    /// <summary>
    /// 스킨 메시 하나의 변형을 갱신한다. 스킨이 떨어졌으면 변형 표시와 캐시를 해제하고, 있으면 캐시에서(필요하면 계산해) 위치를 받아 표시한다.
    /// </summary>
    public void UpdateSkin(NodeId id)
    {
        if (_doc == null || !_meshViews.TryGetValue(id, out var mv)) return;
        var n = _doc.Find(id);
        if (n?.Mesh == null) return;
        if (n.Skin == null) { if (mv.Deformed != null) mv.SetDeformed(null); SkinDeformCache.Forget(id); return; }
        long t0 = AnimPerf.Begin();
        var buf = SkinDeformCache.Get(_doc, n);   // 패널 4개가 같은 통지에서 공유
        AnimPerf.End("skin.deform", t0);
        long t1 = AnimPerf.Begin();
        mv.SetDeformed(buf);
        AnimPerf.End("skin.setDeformed", t1);
    }

    /// <summary>Godot 노드 node가 ancestor의 자손인지(부모를 따라 올라가며 확인).</summary>
    private static bool IsDescendantOf(Node node, Node ancestor)
    {
        for (var p = node.GetParent(); p != null; p = p.GetParent()) if (p == ancestor) return true;
        return false;
    }

    /// <summary>모든 뷰를 버리고 문서 루트의 자식부터 다시 만든 뒤 조인트·스킨을 갱신하고 <see cref="Rebuilt"/>를 알린다.</summary>
    private void RebuildAll()
    {
        foreach (var v in _views.Values) v.QueueFree();
        _views.Clear(); _meshViews.Clear(); _jointViews.Clear(); _lightViews.Clear();
        if (_doc == null) return;
        foreach (var c in _doc.Root.Children) AddView(c);
        RefreshJoints(); UpdateSkins();
        Rebuilt?.Invoke();
    }
}
