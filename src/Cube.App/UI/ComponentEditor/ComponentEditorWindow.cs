using Cube.App.Bridge;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI.ComponentEditor;

/// <summary>
/// Component Editor(Maya, Edit → Component Editor): 활성 메시 오브젝트의 모든 정점 데이터를 표로 보여 주고 편집한다.
/// 탭: Vertices(위치 로컬/월드, 정점 노멀, 노멀 잠금), UVs(세트 하나 = 정점별 UV 점, All Sets = 코너별로 모든 세트 나란히, 핀),
/// Skin Weights(조인트별 가중치 + 합계; 값을 바꾸면 다른 조인트는 비율을 유지하며 정규화).
/// 편집은 <see cref="ComponentEditCommand"/>로 Undo 한 단계. 표는 보이는 행만 그린다(<see cref="DataGrid"/>).
/// <remarks>
/// 갱신 방식: 문서/선택 이벤트는 더티 플래그(<see cref="_dirtyTarget"/>, <see cref="_dirtyRows"/>)만 세우고 실제 재구성은 보이는 동안 <see cref="_Process"/>에서
/// 프레임당 한 번 한다(이벤트가 연달아 와도 한 번만 다시 만듦). 행 = 정점(또는 UV 점/코너), 열 = 값 접근 람다를 가진 <see cref="GridColumn"/>.
/// 편집 중(<see cref="_editing"/>)에 자기 명령이 일으킨 문서 변경은 무시해 표가 편집 도중 재구성되지 않게 한다.
/// </remarks>
/// </summary>
public partial class ComponentEditorWindow : FloatingPanel
{
    /// <summary>탭 종류(TabBar 인덱스와 같은 순서).</summary>
    private enum Tab { Vertices, Uvs, Skin }

    /// <summary>소유 셸(헬프 라인 접근).</summary>
    private Shell _shell = null!;
    /// <summary>편집 대상 문서.</summary>
    private Document _doc = null!;
    /// <summary>가상 스크롤 표.</summary>
    private DataGrid _grid = null!;
    /// <summary>Vertices / UVs / Skin Weights 탭.</summary>
    private TabBar _tabs = null!;
    /// <summary>_target: 대상 메시 요약(이름, 정점/면/UV 세트/스킨), _status: 행 수·강조 행 수·뷰포트 선택 정점 수.</summary>
    private Label _target = null!, _status = null!;
    /// <summary>옵션: 선택된 컴포넌트의 정점만 / 위치를 월드 공간으로(Vertices 탭) / 모든 표시 정점에서 0인 조인트 열 숨김(Skin 탭).</summary>
    private CheckBox _selOnly = null!, _world = null!, _hideZero = null!;
    /// <summary>UV 세트 선택(UVs 탭). 세트가 2개 이상이면 마지막 항목 "All Sets (per corner)"가 추가된다.</summary>
    private OptionButton _uvSet = null!;
    /// <summary>강조 행의 정점을 뷰포트에서 선택하는 버튼.</summary>
    private Button _selectInView = null!;

    /// <summary>현재 대상 메시 노드 ID(없으면 None).</summary>
    private NodeId _node = NodeId.None;
    /// <summary>대상 노드(문서에서 찾음; 삭제되었으면 null).</summary>
    private SceneNode? Node => _doc.Find(_node);
    /// <summary>대상 노드의 폴리 메시.</summary>
    private PolyMesh? Mesh => Node?.Mesh;
    /// <summary>표시 중인 정점 ID 목록(Vertices/Skin 탭의 행 r → 정점 _verts[r]). 살아 있는 정점만, Selected only면 선택 정점만.</summary>
    private int[] _verts = Array.Empty<int>();
    /// <summary>정점 ID → 그 정점을 시작점으로 하는 하프에지(면 코너) 목록. 노멀/UV/잠금 계산에 쓴다.</summary>
    private List<int>[] _corners = Array.Empty<List<int>>();
    /// <summary>UVs 탭(세트 하나) 행: 정점별로 서로 다른 UV 값마다 한 행(같은 UV를 공유하는 코너 묶음).</summary>
    private List<UvRow> _uvRows = new();
    /// <summary>UVs 탭 All Sets 행: 면 코너(하프에지)마다 한 행.</summary>
    private List<CornerRow> _cornerRows = new();
    /// <summary>뷰포트에서 선택된 컴포넌트를 정점으로 바꾼 집합. 행 머리를 주황으로 강조하고 Selected only 필터에 쓴다.</summary>
    private HashSet<int> _selVerts = new();
    /// <summary>_dirtyRows: 행/열 재구성 필요, _dirtyTarget: 대상 노드 재결정 필요, _editing: 이 창이 문서를 바꾸는 중(자기 변경 이벤트 무시).</summary>
    private bool _dirtyRows = true, _dirtyTarget = true, _editing;
    /// <summary>가운데 버튼 드래그 편집 중인 명령(Capture → 미리보기 → Commit). 드래그 중이 아니면 null.</summary>
    private ComponentEditCommand? _dragCmd;
    /// <summary>마지막으로 본 메시 TopologyVersion. 위상이 바뀌면 행 번호 의미가 달라지므로 표 선택을 지운다.</summary>
    private int _lastTopology = -1;

    /// <summary>현재 탭.</summary>
    private Tab CurrentTab => (Tab)_tabs.CurrentTab;
    /// <summary>UVs 탭에서 "All Sets"(마지막 항목)를 골랐는지.</summary>
    private bool AllSets => _uvSet.Selected == _uvSet.ItemCount - 1 && _uvSet.ItemCount > 1;
    /// <summary>선택된 UV 세트 인덱스(선택 없으면 0).</summary>
    private int UvSetIndex => Math.Max(0, _uvSet.Selected);

    /// <summary>
    /// 패널 UI를 구성하고(대상 라벨 → 탭 → 옵션 줄 → 표 → 도움말 → 상태) 표의 편집 콜백을 이 창의 Undo 처리에 연결한다.
    /// 문서·선택·모드 변경과 보이기 전환은 더티 플래그만 세운다.
    /// </summary>
    /// <param name="shell">소유 셸.</param>
    public void Setup(Shell shell)
    {
        _shell = shell; _doc = shell.Document;
        float s = CubeApp.Instance.UiScale;
        Title = "Component Editor";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(820 * s, host.X * 0.8f), MathF.Min(480 * s, host.Y * 0.7f));
        MinPanelSize = new Vector2(360 * s, 220 * s);

        _target = new Label { Text = "No mesh selected", ClipText = true };
        Content.AddChild(_target);
        _tabs = new TabBar { ClipTabs = false };
        _tabs.AddTab("Vertices"); _tabs.AddTab("UVs"); _tabs.AddTab("Skin Weights");
        // 탭을 바꾸면 옵션 표시를 바꾸고 표 선택을 지운 뒤 행을 다시 만든다.
        _tabs.TabChanged += _ => { UpdateOptionVisibility(); _grid.ClearSelection(); _dirtyRows = true; };
        Content.AddChild(_tabs);

        // 옵션 줄(좁으면 줄바꿈).
        var bar = new HFlowContainer();
        _selOnly = new CheckBox { Text = "Selected only", TooltipText = "Show only the vertices of the current component selection (vertex/edge/face/UV)" };
        _selOnly.Toggled += _ => _dirtyRows = true;
        bar.AddChild(_selOnly);
        _world = new CheckBox { Text = "World space", TooltipText = "Show and edit positions in world space (otherwise object space)" };
        _world.Toggled += _ => _grid.QueueRedraw();
        bar.AddChild(_world);
        _uvSet = new OptionButton { TooltipText = "UV set to show. 'All Sets' lists every face corner with the U/V of every set side by side." };
        _uvSet.ItemSelected += _ => { _grid.ClearSelection(); _dirtyRows = true; };
        bar.AddChild(_uvSet);
        _hideZero = new CheckBox { Text = "Hide zero columns", ButtonPressed = true, TooltipText = "Hide joints whose weight is zero for every shown vertex" };
        _hideZero.Toggled += _ => _dirtyRows = true;
        bar.AddChild(_hideZero);
        _selectInView = new Button { Text = "Select in Viewport", TooltipText = "Select the vertices of the highlighted rows in the viewport" };
        _selectInView.Pressed += SelectRowsInViewport;
        bar.AddChild(_selectInView);
        var copy = new Button { Text = "Copy", TooltipText = "Copy the highlighted rows (or all rows) as tab-separated text (Ctrl+C in the table)" };
        copy.Pressed += () => _grid.CopySelection();
        bar.AddChild(copy);
        Content.AddChild(bar);

        // 표: 편집 콜백 = 즉시 편집/드래그 편집(Undo 처리는 이 창), 행 머리 색 = 뷰포트에서 선택된 정점이면 주황.
        _grid = new DataGrid { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _grid.CommitEdit = CommitEdit;
        _grid.BeginDrag = BeginDrag;
        _grid.PreviewDrag = PreviewDrag;
        _grid.EndDrag = EndDrag;
        _grid.RowTint = r => _selVerts.Contains(VertexOfRow(r)) ? new Color(0.55f, 0.36f, 0.1f) : null;
        _grid.SelectionChanged += UpdateStatus;
        Content.AddChild(_grid);

        _status = new Label();
        _status.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        Content.AddChild(new Label { Text = "Double-click / type a number to edit (applies to all highlighted rows; +=, -=, *=, /= for relative). Middle-drag a cell to scrub. Bool cells toggle on click.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.6f) });
        Content.AddChild(_status);

        // 이벤트는 모두 지연 갱신(더티 플래그). 다시 보이게 되면 대상을 다시 정한다.
        _doc.Changed += OnDocChanged;
        _doc.Selection.Changed += OnSelectionChanged;
        _doc.Selection.ModeChanged += OnSelectionChanged;
        VisibilityChanged += () => { if (IsVisibleInTree()) { _dirtyTarget = true; } };
        UpdateOptionVisibility();
    }

    /// <summary>노드가 실제로 해제될 때만 문서 이벤트 구독을 푼다(도킹 이동 시 _ExitTree는 무시).</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationPredelete && _doc != null)
        {
            _doc.Changed -= OnDocChanged;
            _doc.Selection.Changed -= OnSelectionChanged;
            _doc.Selection.ModeChanged -= OnSelectionChanged;
        }
    }

    /// <summary>
    /// 문서 변경 처리. 자기 편집 중이면 무시. 노드 추가/삭제/리셋 = 대상 재결정, 대상 노드의 위상·속성·스킨·이름 변경 = 행 재구성,
    /// 그 외(지오메트리·트랜스폼 등) = 값만 바뀌므로 다시 그리기만 한다(열 람다가 매번 메시에서 읽음).
    /// </summary>
    private void OnDocChanged(DocChange c)
    {
        if (_editing) return;
        if (c.Kind is ChangeKind.Reset or ChangeKind.NodeAdded or ChangeKind.NodeRemoved) { _dirtyTarget = true; return; }
        if (c.Node != _node && c.Node != NodeId.None) return;
        switch (c.Kind)
        {
            case ChangeKind.MeshTopology: case ChangeKind.MeshAttributes: case ChangeKind.SkinChanged: case ChangeKind.NodeRenamed:
                _dirtyRows = true; break;
            default:
                _grid?.QueueRedraw(); break;
        }
    }

    /// <summary>선택/모드 변경: 대상과 선택 정점이 바뀔 수 있으므로 대상 재결정 표시.</summary>
    private void OnSelectionChanged() { _dirtyTarget = true; }

    /// <summary>보이는 동안 프레임마다 더티 플래그를 처리한다(대상 재결정 → 행 재구성 순서).</summary>
    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        if (_dirtyTarget) { _dirtyTarget = false; Retarget(); }
        if (_dirtyRows) { _dirtyRows = false; RebuildRows(); }
    }

    // ---------------------------------------------------------------- 대상·행

    /// <summary>
    /// 표시할 메시 노드를 정한다. 우선순위: 컴포넌트 모드의 hilite 대상 / 활성 오브젝트 → 컴포넌트가 선택된 메시 →
    /// (메시가 아닌 그룹·스켈레톤을 골랐으면) 그 아래 첫 메시, 없으면 최상위 조상 아래 첫 메시 → 그래도 없으면 이전 대상 유지.
    /// 대상이 바뀌면 표 선택을 지운다.
    /// </summary>
    private void Retarget()
    {
        var sel = _doc.Selection;
        var id = sel.IsComponentMode && sel.ComponentTarget != NodeId.None ? sel.ComponentTarget : sel.ActiveObject;
        if (_doc.Find(id)?.Mesh == null)
        {
            // 컴포넌트가 선택된 메시라도 대상으로
            id = sel.NodesWithComponents(sel.Mode).FirstOrDefault(n => _doc.Find(n)?.Mesh != null);
        }
        // 그룹·스켈레톤 등 메시가 아닌 노드면 그 아래 첫 메시(가져온 모델의 최상위 노드를 골랐을 때)
        if (_doc.Find(id) is { Mesh: null } grp)
        {
            var top = grp; while (top.Parent != null && !top.Parent.IsRoot) top = top.Parent;
            id = (FirstMesh(grp) ?? FirstMesh(top))?.Id ?? NodeId.None; // 스켈레톤처럼 메시의 형제를 골랐으면 최상위 조상 아래에서
        }
        if (_doc.Find(id)?.Mesh == null) id = _doc.Find(_node)?.Mesh != null ? _node : NodeId.None; // 메시가 없으면 이전 대상 유지
        if (id != _node) { _node = id; _grid.ClearSelection(); }
        UpdateSelectedVerts();
        _dirtyRows = true;
    }

    /// <summary>깊이 우선으로 n의 자손 중 처음 만나는 메시 노드를 찾는다(n 자신은 제외).</summary>
    private static SceneNode? FirstMesh(SceneNode n)
    {
        foreach (var c in n.Children) { if (c.Mesh != null) return c; if (FirstMesh(c) is { } d) return d; }
        return null;
    }

    /// <summary>
    /// 현재 컴포넌트 선택을 대상 메시의 정점 집합으로 바꿔 <see cref="_selVerts"/>에 넣는다.
    /// UV 모드는 UvTopology로 UV 점 → 정점, 그 외 컴포넌트 모드는 SelectionOps.Convert로 정점 변환. 오브젝트 모드면 비운다.
    /// </summary>
    private void UpdateSelectedVerts()
    {
        _selVerts = new HashSet<int>();
        var m = Mesh; if (m == null) return;
        var sel = _doc.Selection;
        if (!sel.IsComponentMode) return;
        var comps = sel.GetComponents(_node);
        if (sel.Mode == SelectMode.Uv)
        {
            if (comps.Uvs.Count > 0)
            {
                var topo = Core.Uv.UvTopology.Build(m);
                foreach (int u in comps.Uvs) if (u >= 0 && u < topo.Points.Count) _selVerts.Add(topo.Points[u].Vertex);
            }
        }
        else _selVerts = SelectionOps.Convert(m, comps, sel.Mode, SelectMode.Vertex);
    }

    /// <summary>
    /// 현재 탭과 옵션에 맞게 행 목록과 열을 처음부터 다시 만든다. 위상이 바뀌었으면 표 선택을 지운다.
    /// 행 데이터(정점 목록·코너 맵·UV 행)는 <c>ComponentData</c> 헬퍼가 만든다.
    /// </summary>
    private void RebuildRows()
    {
        var n = Node; var m = Mesh;
        if (n == null || m == null)
        {
            _target.Text = "No mesh selected — select a polygon object (or its components).";
            _verts = Array.Empty<int>(); _uvRows.Clear(); _cornerRows.Clear(); _grid.Columns.Clear(); _grid.SetRows(0); UpdateStatus();
            return;
        }
        if (_lastTopology != m.TopologyVersion) { _lastTopology = m.TopologyVersion; _grid.ClearSelection(); }
        UpdateSelectedVerts();
        // 정점별 코너 목록과 표시 정점 목록(살아 있는 정점 + 선택 필터).
        _corners = ComponentData.CornersByVertex(m);
        var all = new List<int>(m.VertexCount);
        for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive && (!_selOnly.ButtonPressed || _selVerts.Contains(v))) all.Add(v);
        _verts = all.ToArray();
        RefreshUvSetList(m);
        var skin = n.MeshShape?.Skin;
        _target.Text = $"{n.Name}   —   {m.AliveVertexCount} vertices, {m.AliveFaceCount} faces, {ComponentData.SetCount(m)} UV set(s){(skin != null ? $", skin: {skin.Joints.Count} joint(s)" : ", no skin")}";
        // 탭별 열 구성과 행 수.
        _grid.Columns.Clear();
        int rows;
        switch (CurrentTab)
        {
            case Tab.Uvs:
                if (AllSets) { _cornerRows = ComponentData.CornerRows(m, _verts, _corners); BuildCornerColumns(m); rows = _cornerRows.Count; }
                else { _uvRows = ComponentData.UvRows(m, UvSetIndex, _verts, _corners); BuildUvColumns(m); rows = _uvRows.Count; }
                break;
            case Tab.Skin:
                BuildSkinColumns(n, m); rows = skin == null ? 0 : _verts.Length; break;
            default:
                BuildVertexColumns(m); rows = _verts.Length; break;
        }
        _grid.SetRows(rows);
        UpdateStatus();
    }

    /// <summary>
    /// UV 세트 드롭다운을 메시 세트에 맞춘다. 항목 수와 이름이 그대로면 손대지 않아 사용자 선택을 유지하고,
    /// 달라졌으면 다시 채운 뒤 이전 선택(없으면 현재 세트)을 범위 안으로 맞춰 선택한다.
    /// </summary>
    private void RefreshUvSetList(PolyMesh m)
    {
        int count = ComponentData.SetCount(m);
        // 세트가 2개 이상이면 "All Sets" 항목이 하나 더 붙는다.
        int want = count + (count > 1 ? 1 : 0);
        int keep = _uvSet.Selected < 0 ? m.CurrentUvSet : _uvSet.Selected;
        bool same = _uvSet.ItemCount == want;
        for (int i = 0; same && i < count; i++) same = _uvSet.GetItemText(i).StartsWith(ComponentData.SetName(m, i));
        if (same) return;
        _uvSet.Clear();
        for (int i = 0; i < count; i++) _uvSet.AddItem(ComponentData.SetName(m, i) + (i == m.CurrentUvSet || m.UvSets.Count == 0 ? " (current)" : ""));
        if (count > 1) _uvSet.AddItem("All Sets (per corner)");
        _uvSet.Select(Math.Clamp(keep, 0, _uvSet.ItemCount - 1));
    }

    /// <summary>탭에 맞는 옵션만 보이게 한다(World space = Vertices, UV 세트 = UVs, Hide zero = Skin).</summary>
    private void UpdateOptionVisibility()
    {
        var t = CurrentTab;
        _world.Visible = t == Tab.Vertices;
        _uvSet.Visible = t == Tab.Uvs;
        _hideZero.Visible = t == Tab.Skin;
    }

    /// <summary>표 행 번호 → 정점 ID(탭·All Sets 여부에 따라 다른 행 목록을 참조). 범위 밖이면 -1.</summary>
    private int VertexOfRow(int r) => CurrentTab switch
    {
        Tab.Uvs => AllSets ? (r < _cornerRows.Count ? _cornerRows[r].Vertex : -1) : (r < _uvRows.Count ? _uvRows[r].Vertex : -1),
        _ => r < _verts.Length ? _verts[r] : -1,
    };

    /// <summary>하단 상태 라벨 갱신(행 수, 강조 행 수, 뷰포트 선택 정점 수).</summary>
    private void UpdateStatus()
    {
        if (_status == null) return;
        _status.Text = $"{_grid.RowCount} row(s), {_grid.Selected.Count} highlighted" + (_selVerts.Count > 0 ? $", {_selVerts.Count} vertex(es) selected in the viewport (orange)" : "");
    }

    // ---------------------------------------------------------------- 열

    /// <summary>열 강조 색: 위치 X/Y/Z(빨강/초록/파랑), UV, 스킨 가중치.</summary>
    private static readonly Color AxisX = new(1f, 0.4f, 0.4f), AxisY = new(0.5f, 1f, 0.45f), AxisZ = new(0.45f, 0.6f, 1f), UvTint = new(0.8f, 0.6f, 1f), WeightTint = new(1f, 0.75f, 0.4f);

    /// <summary>고정 0번 열(행 식별 텍스트) 정의를 만든다.</summary>
    private GridColumn IdColumn(string title, Func<int, string> text) => new() { Title = title, Kind = GridColKind.Text, Width = 110, Text = text };

    /// <summary>
    /// Vertices 탭 열: 정점 ID | Position X/Y/Z(편집 가능, 월드 옵션) | Normal X/Y/Z(편집 = 노멀 잠금) | Normal Locked(토글) | Faces 수 | 현재 세트 UV 요약 | (스킨이면) 영향 요약.
    /// </summary>
    private void BuildVertexColumns(PolyMesh m)
    {
        var cols = _grid.Columns;
        cols.Add(IdColumn("Vertex", r => $"vtx[{_verts[r]}]"));
        string[] axes = { "X", "Y", "Z" }; Color[] tints = { AxisX, AxisY, AxisZ };
        for (int a = 0; a < 3; a++)
        {
            int ax = a;
            cols.Add(new GridColumn
            {
                Title = "Position " + axes[a], Width = 90, Tint = tints[a], Step = 0.01, Tip = "Vertex position (object space, or world space when 'World space' is on)",
                Value = r => Get(PositionOf(m, _verts[r]), ax),
                // 위치 쓰기: 월드 표시 중이면 입력 값을 월드 → 로컬로 되돌려 저장하고 노멀을 다시 계산한다.
                Set = (rows, vals) =>
                {
                    var world = Node!.WorldMatrix; System.Numerics.Matrix4x4.Invert(world, out var inv);
                    for (int i = 0; i < rows.Length; i++)
                    {
                        int v = _verts[rows[i]];
                        var p = With(PositionOf(m, v), ax, (float)vals[i]);
                        if (_world.ButtonPressed) p = NVec3.Transform(p, inv);
                        var vert = m.Verts[v]; vert.Position = p; m.Verts[v] = vert;
                    }
                    MeshNormals.Recompute(m);
                },
            });
        }
        for (int a = 0; a < 3; a++)
        {
            int ax = a;
            cols.Add(new GridColumn
            {
                Title = "Normal " + axes[a], Width = 85, Step = 0.01, Tip = "Vertex normal (average of the face-corner normals). Editing locks the vertex normal (Mesh Display → Lock Normals).",
                Value = r => Get(ComponentData.VertexNormal(m, _verts[r], _corners), ax),
                // 노멀 쓰기: 한 성분을 바꾼 벡터를 정규화해 정점 노멀 잠금(LockedNormals)에 넣는다 — Recompute가 잠긴 정점의 코너를 고정한다.
                Set = (rows, vals) =>
                {
                    for (int i = 0; i < rows.Length; i++)
                    {
                        int v = _verts[rows[i]];
                        var nrm = With(ComponentData.VertexNormal(m, v, _corners), ax, (float)vals[i]);
                        if (nrm.LengthSquared() > 1e-12f) m.LockedNormals[v] = NVec3.Normalize(nrm);
                    }
                    MeshNormals.Recompute(m);
                },
            });
        }
        cols.Add(new GridColumn
        {
            Title = "Normal Locked", Kind = GridColKind.Bool, Width = 100, Tip = "Locked vertex/corner normal. Click to lock the current normal or unlock.",
            Bool = r => ComponentData.NormalLocked(m, _verts[r], _corners),
            // 잠금 토글: 켜면 현재 노멀로 잠그고, 끄면 해제.
            SetBool = (rows, on) =>
            {
                var vs = rows.Select(r => _verts[r]).ToArray();
                if (on) MeshOps.LockNormals(m, vs); else MeshOps.UnlockNormals(m, vs);
                MeshNormals.Recompute(m);
            },
        });
        cols.Add(new GridColumn { Title = "Faces", Kind = GridColKind.Text, Width = 60, Text = r => (_corners[_verts[r]]?.Count ?? 0).ToString(), Tip = "Number of faces using this vertex" });
        cols.Add(new GridColumn
        {
            Title = "UVs (" + ComponentData.SetName(m, m.UvSets.Count == 0 ? 0 : m.CurrentUvSet) + ")", Kind = GridColKind.Text, Width = 150, Tip = "Distinct UVs of this vertex in the current set (see the UVs tab to edit)",
            // 서로 다른 UV 값을 최대 3개까지 보여 주고 더 있으면 "…".
            Text = r =>
            {
                var cs = _corners[_verts[r]]; if (cs == null) return "";
                return string.Join("  ", cs.Select(h => m.Hes[h].Uv0).Distinct().Take(3).Select(u => $"{u.X:0.###},{u.Y:0.###}")) + (cs.Select(h => m.Hes[h].Uv0).Distinct().Count() > 3 ? " …" : "");
            },
        });
        // 스킨이 있으면 가중치 큰 순서로 "조인트 가중치" 요약 열을 추가.
        var skin = Node?.MeshShape?.Skin;
        if (skin != null)
            cols.Add(new GridColumn
            {
                Title = "Influences", Kind = GridColKind.Text, Width = 220, Tip = "Skin weights of this vertex (see the Skin Weights tab to edit)",
                Text = r => { int v = _verts[r]; var w = v < skin.Weights.Length ? skin.Weights[v] : null; return w == null ? "" : string.Join("  ", w.OrderByDescending(x => x.weight).Select(x => $"{JointName(skin, x.joint)} {x.weight:0.###}")); },
            });
    }

    /// <summary>정점 위치(월드 옵션이 켜져 있으면 노드 월드 행렬로 변환한 값).</summary>
    private NVec3 PositionOf(PolyMesh m, int v)
    {
        var p = m.Verts[v].Position;
        return _world.ButtonPressed ? NVec3.Transform(p, Node!.WorldMatrix) : p;
    }

    /// <summary>벡터의 a번 성분(0 X, 1 Y, 2 Z).</summary>
    private static float Get(NVec3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    /// <summary>벡터의 a번 성분만 x로 바꾼 복사본.</summary>
    private static NVec3 With(NVec3 v, int a, float x) { if (a == 0) v.X = x; else if (a == 1) v.Y = x; else v.Z = x; return v; }

    /// <summary>
    /// UVs 탭(세트 하나) 열: 정점/UV 식별 | 그 UV를 쓰는 면 | U | V | Pinned.
    /// U/V를 바꾸면 그 UV 행이 묶은 모든 면 코너에 같은 값을 쓴다(UV 점 단위 편집).
    /// </summary>
    private void BuildUvColumns(PolyMesh m)
    {
        int set = UvSetIndex;
        var cols = _grid.Columns;
        // 정점에 UV가 하나뿐이면 "vtx[n]", 여러 개면 "vtx[n] #k"로 구분.
        cols.Add(IdColumn("Vertex / UV", r => { var u = _uvRows[r]; return u.Index == 0 && (r + 1 >= _uvRows.Count || _uvRows[r + 1].Vertex != u.Vertex) ? $"vtx[{u.Vertex}]" : $"vtx[{u.Vertex}] #{u.Index + 1}"; }));
        cols.Add(new GridColumn { Title = "Faces", Kind = GridColKind.Text, Width = 110, Text = r => string.Join(",", _uvRows[r].Corners.Select(h => m.Hes[h].Face).OrderBy(f => f)), Tip = "Faces sharing this UV at the vertex" });
        for (int a = 0; a < 2; a++)
        {
            int ax = a;
            cols.Add(new GridColumn
            {
                Title = ax == 0 ? "U" : "V", Width = 90, Tint = UvTint, Step = 0.002, Tip = $"UV in set '{ComponentData.SetName(m, set)}' (applies to every face corner of this UV)",
                Value = r => { var uv = ComponentData.GetUv(m, set, _uvRows[r].Corners[0]); return ax == 0 ? uv.X : uv.Y; },
                Set = (rows, vals) =>
                {
                    for (int i = 0; i < rows.Length; i++)
                        foreach (int h in _uvRows[rows[i]].Corners)
                        {
                            var uv = ComponentData.GetUv(m, set, h);
                            if (ax == 0) uv.X = (float)vals[i]; else uv.Y = (float)vals[i];
                            ComponentData.SetUv(m, set, h, uv);
                        }
                },
            });
        }
        cols.Add(PinColumn(m, r => _uvRows[r].Corners));
    }

    /// <summary>핀 열: 행의 코너 중 하나라도 핀이면 체크, 토글하면 행의 모든 코너 PinUv를 설정한다.</summary>
    /// <param name="m">대상 메시.</param>
    /// <param name="corners">행 번호 → 하프에지(코너) 배열.</param>
    private GridColumn PinColumn(PolyMesh m, Func<int, int[]> corners) => new()
    {
        Title = "Pinned", Kind = GridColKind.Bool, Width = 70, Tip = "UV pin (UV Editor → Pin): pinned UVs do not move in Unfold/Optimize",
        Bool = r => corners(r).Any(h => m.Hes[h].PinUv),
        SetBool = (rows, on) => { foreach (int r in rows) foreach (int h in corners(r)) { var he = m.Hes[h]; he.PinUv = on; m.Hes[h] = he; } },
    };

    /// <summary>
    /// UVs 탭 All Sets 열: 정점 | 면 | 세트마다 U/V 열 쌍(세트별로 색을 번갈아) | Pinned. 행 = 면 코너 하나.
    /// </summary>
    private void BuildCornerColumns(PolyMesh m)
    {
        var cols = _grid.Columns;
        cols.Add(IdColumn("Vertex", r => $"vtx[{_cornerRows[r].Vertex}]"));
        cols.Add(new GridColumn { Title = "Face", Kind = GridColKind.Text, Width = 60, Text = r => _cornerRows[r].Face.ToString() });
        for (int s = 0; s < ComponentData.SetCount(m); s++)
            for (int a = 0; a < 2; a++)
            {
                int set = s, ax = a;
                cols.Add(new GridColumn
                {
                    Title = $"{ComponentData.SetName(m, s)} {(ax == 0 ? "U" : "V")}", Width = 90, Tint = s % 2 == 0 ? UvTint : new Color(0.5f, 0.9f, 0.9f), Step = 0.002,
                    Tip = $"UV of this face corner in set '{ComponentData.SetName(m, s)}'",
                    Value = r => { var uv = ComponentData.GetUv(m, set, _cornerRows[r].HalfEdge); return ax == 0 ? uv.X : uv.Y; },
                    Set = (rows, vals) =>
                    {
                        for (int i = 0; i < rows.Length; i++)
                        {
                            int h = _cornerRows[rows[i]].HalfEdge;
                            var uv = ComponentData.GetUv(m, set, h);
                            if (ax == 0) uv.X = (float)vals[i]; else uv.Y = (float)vals[i];
                            ComponentData.SetUv(m, set, h, uv);
                        }
                    },
                });
            }
        cols.Add(PinColumn(m, r => new[] { _cornerRows[r].HalfEdge }));
    }

    /// <summary>스킨 조인트 인덱스 → 조인트 노드 이름(찾지 못하면 "jointN").</summary>
    private string JointName(SkinCluster skin, int j) => j >= 0 && j < skin.Joints.Count ? _doc.Find(skin.Joints[j])?.Name ?? $"joint{j}" : $"joint{j}";

    /// <summary>
    /// Skin Weights 탭 열: 정점 | 조인트별 가중치(편집 시 다른 영향은 비율 유지 정규화, 0~1로 고정) | Total(읽기 전용).
    /// Hide zero columns가 켜져 있으면 표시 정점 전체에서 가중치가 0인 조인트 열은 만들지 않는다.
    /// </summary>
    private void BuildSkinColumns(SceneNode n, PolyMesh m)
    {
        var cols = _grid.Columns;
        cols.Add(IdColumn("Vertex", r => $"vtx[{_verts[r]}]"));
        var skin = n.MeshShape?.Skin;
        if (skin == null) { cols.Add(new GridColumn { Title = "No skin — bind the mesh first (Skin → Bind Skin)", Kind = GridColKind.Text, Width = 400, Text = _ => "" }); return; }
        // 표시 정점 중 하나라도 가중치가 있는 조인트를 표시한다.
        var used = new bool[skin.Joints.Count];
        if (_hideZero.ButtonPressed)
        {
            foreach (int v in _verts)
                if (v < skin.Weights.Length && skin.Weights[v] is { } ws) foreach (var (j, w) in ws) if (j >= 0 && j < used.Length && w > 0) used[j] = true;
        }
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            if (_hideZero.ButtonPressed && !used[j]) continue;
            int jj = j;
            cols.Add(new GridColumn
            {
                Title = JointName(skin, j), Width = 95, Tint = WeightTint, Step = 0.005, Format = "0.000",
                Tip = $"Weight of {JointName(skin, j)}. Editing normalizes the other influences (keeping their ratios); at most {SkinCluster.MaxInfluences} influences per vertex.",
                Value = r => skin.GetWeight(_verts[r], jj),
                Set = (rows, vals) => { for (int i = 0; i < rows.Length; i++) SkinOps.SetWeightNormalized(skin, _verts[rows[i]], jj, (float)Math.Clamp(vals[i], 0, 1)); },
            });
        }
        cols.Add(new GridColumn
        {
            Title = "Total", Kind = GridColKind.Float, Width = 70, Format = "0.000",
            Value = r => { int v = _verts[r]; return v < skin.Weights.Length && skin.Weights[v] is { } ws ? ws.Sum(x => x.weight) : 0; },
        });
    }

    // ---------------------------------------------------------------- 편집(Undo)

    /// <summary>
    /// 즉시 편집 확정: 변경 함수를 <see cref="ComponentEditCommand"/>로 감싸 Undo 스택에 넣는다(명령이 앞뒤 스냅샷을 잡고 실행).
    /// 넣는 동안 _editing을 켜 자기 변경 이벤트로 표가 재구성되지 않게 한다.
    /// </summary>
    /// <param name="name">Undo 항목 이름 접미사(열 제목 = 값).</param>
    /// <param name="change">메시/스킨을 바꾸는 함수.</param>
    private void CommitEdit(string name, Action change)
    {
        var n = Node; if (n == null) return;
        _editing = true;
        try { _doc.Undo.Push(new ComponentEditCommand($"Component Editor: {name}", _node, (_, _) => change())); }
        finally { _editing = false; }
        AfterEdit();
    }

    /// <summary>드래그 편집 시작: 명령을 만들고 현재 상태를 캡처한다.</summary>
    private void BeginDrag(string name)
    {
        if (Node == null) return;
        _dragCmd = new ComponentEditCommand($"Component Editor: {name}", _node);
        _dragCmd.Capture(_doc);
    }

    /// <summary>드래그 미리보기: 변경을 문서에 바로 적용하고 변경 통지(메시, 스킨이면 스킨도)를 보낸다. Undo 기록은 아직 하지 않는다.</summary>
    private void PreviewDrag(Action change)
    {
        var n = Node; if (n == null || _dragCmd == null) return;
        _editing = true;
        try { change(); ComponentEditCommand.NotifyAll(_doc, _node, n.MeshShape?.Skin != null); }
        finally { _editing = false; }
    }

    /// <summary>
    /// 드래그 끝: 명령에 최종 상태를 기록(Commit)하고, 실제로 움직였고 변화가 있으면 이미 적용된 명령으로 Undo 스택에 넣는다.
    /// </summary>
    private void EndDrag(bool moved)
    {
        if (_dragCmd == null) return;
        _dragCmd.Commit(_doc);
        if (moved && !_dragCmd.IsNoop) { _editing = true; try { _doc.Undo.Push(_dragCmd, alreadyApplied: true); } finally { _editing = false; } }
        _dragCmd = null;
        AfterEdit();
    }

    /// <summary>편집 뒤: UV 행은 값이 같아진 코너가 합쳐질 수 있어 다시 묶고, 가중치 열은 새 영향이 생길 수 있어 다시 만든다.</summary>
    private void AfterEdit()
    {
        // 재구성해도 강조 행 번호는 유지한다(범위 안인 것만).
        if (CurrentTab == Tab.Uvs && !AllSets || CurrentTab == Tab.Skin) { var keep = _grid.Selected.ToArray(); RebuildRows(); foreach (int r in keep) if (r < _grid.RowCount) _grid.Selected.Add(r); }
        _grid.QueueRedraw();
    }

    /// <summary>DebugDriver: comped tab T [set S] | edit ROW[,ROW..] COL VALUE | rows N | select | sel 0/1 | world 0/1</summary>
    // a[0] = 하위 명령, 나머지 = 인자. 결과 문자열은 DebugDriver가 출력한다.
    public string Drive(string[] a)
    {
        switch (a[0])
        {
            case "tab": _tabs.CurrentTab = int.Parse(a[1]); UpdateOptionVisibility(); if (a.Length > 2) _uvSet.Select(int.Parse(a[2])); _dirtyRows = true; break;
            case "sel": _selOnly.ButtonPressed = a[1] == "1"; break;
            case "world": _world.ButtonPressed = a[1] == "1"; break;
            case "edit": _grid.DriveEdit(a[1].Split(',').Select(int.Parse).ToArray(), int.Parse(a[2]), a[3]); break;
            case "select": _grid.Selected.Clear(); foreach (var r in a[1].Split(',')) _grid.Selected.Add(int.Parse(r)); SelectRowsInViewport(); break;
            case "rows":
                {
                    _process();
                    int n = a.Length > 1 ? int.Parse(a[1]) : 3;
                    var sb = new System.Text.StringBuilder($"{_target.Text} || {_grid.RowCount} rows\n  {_grid.RowText(-1)}");
                    for (int r = 0; r < Math.Min(n, _grid.RowCount); r++) sb.Append($"\n  {_grid.RowText(r)}");
                    return sb.ToString();
                }
        }
        return "ok";
    }

    /// <summary>DebugDriver용 즉시 갱신: _Process와 같은 더티 처리를 바로 실행한다(보이지 않을 때도).</summary>
    private void _process() { if (_dirtyTarget) { _dirtyTarget = false; Retarget(); } if (_dirtyRows) { _dirtyRows = false; RebuildRows(); } }

    /// <summary>
    /// 강조 행(없으면 모든 행)의 정점을 뷰포트에서 정점 모드로 선택한다. 컴포넌트 대상을 이 메시로 바꾸고
    /// 선택 전후 스냅샷으로 <see cref="SelectionCommand"/>를 넣어 Undo 가능하게 한다.
    /// </summary>
    private void SelectRowsInViewport()
    {
        var m = Mesh; if (m == null) return;
        var verts = (_grid.Selected.Count > 0 ? _grid.Selected : Enumerable.Range(0, _grid.RowCount)).Select(VertexOfRow).Where(v => v >= 0).Distinct().ToList();
        var sel = _doc.Selection;
        var before = sel.Capture();
        if (sel.Mode != SelectMode.Vertex) sel.Mode = SelectMode.Vertex;
        sel.SetComponentTarget(_node);
        sel.SelectComponents(_node, SelectMode.Vertex, verts);
        _doc.Undo.Push(new SelectionCommand(before, sel.Capture()), alreadyApplied: true);
        _shell.HelpLine.Text = $"Component Editor: selected {verts.Count} vertex(es).";
    }
}
