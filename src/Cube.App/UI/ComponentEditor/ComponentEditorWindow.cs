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
/// </summary>
public partial class ComponentEditorWindow : FloatingPanel
{
    private enum Tab { Vertices, Uvs, Skin }

    private Shell _shell = null!;
    private Document _doc = null!;
    private DataGrid _grid = null!;
    private TabBar _tabs = null!;
    private Label _target = null!, _status = null!;
    private CheckBox _selOnly = null!, _world = null!, _hideZero = null!;
    private OptionButton _uvSet = null!;
    private Button _selectInView = null!;

    private NodeId _node = NodeId.None;
    private SceneNode? Node => _doc.Find(_node);
    private PolyMesh? Mesh => Node?.Mesh;
    private int[] _verts = Array.Empty<int>();
    private List<int>[] _corners = Array.Empty<List<int>>();
    private List<UvRow> _uvRows = new();
    private List<CornerRow> _cornerRows = new();
    private HashSet<int> _selVerts = new();
    private bool _dirtyRows = true, _dirtyTarget = true, _editing;
    private ComponentEditCommand? _dragCmd;
    private int _lastTopology = -1;

    private Tab CurrentTab => (Tab)_tabs.CurrentTab;
    private bool AllSets => _uvSet.Selected == _uvSet.ItemCount - 1 && _uvSet.ItemCount > 1;
    private int UvSetIndex => Math.Max(0, _uvSet.Selected);

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
        _tabs.TabChanged += _ => { UpdateOptionVisibility(); _grid.ClearSelection(); _dirtyRows = true; };
        Content.AddChild(_tabs);

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

        _doc.Changed += OnDocChanged;
        _doc.Selection.Changed += OnSelectionChanged;
        _doc.Selection.ModeChanged += OnSelectionChanged;
        VisibilityChanged += () => { if (IsVisibleInTree()) { _dirtyTarget = true; } };
        UpdateOptionVisibility();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete && _doc != null)
        {
            _doc.Changed -= OnDocChanged;
            _doc.Selection.Changed -= OnSelectionChanged;
            _doc.Selection.ModeChanged -= OnSelectionChanged;
        }
    }

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

    private void OnSelectionChanged() { _dirtyTarget = true; }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        if (_dirtyTarget) { _dirtyTarget = false; Retarget(); }
        if (_dirtyRows) { _dirtyRows = false; RebuildRows(); }
    }

    // ---------------------------------------------------------------- 대상·행

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

    private static SceneNode? FirstMesh(SceneNode n)
    {
        foreach (var c in n.Children) { if (c.Mesh != null) return c; if (FirstMesh(c) is { } d) return d; }
        return null;
    }

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
        _corners = ComponentData.CornersByVertex(m);
        var all = new List<int>(m.VertexCount);
        for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive && (!_selOnly.ButtonPressed || _selVerts.Contains(v))) all.Add(v);
        _verts = all.ToArray();
        RefreshUvSetList(m);
        var skin = n.MeshShape?.Skin;
        _target.Text = $"{n.Name}   —   {m.AliveVertexCount} vertices, {m.AliveFaceCount} faces, {ComponentData.SetCount(m)} UV set(s){(skin != null ? $", skin: {skin.Joints.Count} joint(s)" : ", no skin")}";
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

    private void RefreshUvSetList(PolyMesh m)
    {
        int count = ComponentData.SetCount(m);
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

    private void UpdateOptionVisibility()
    {
        var t = CurrentTab;
        _world.Visible = t == Tab.Vertices;
        _uvSet.Visible = t == Tab.Uvs;
        _hideZero.Visible = t == Tab.Skin;
    }

    private int VertexOfRow(int r) => CurrentTab switch
    {
        Tab.Uvs => AllSets ? (r < _cornerRows.Count ? _cornerRows[r].Vertex : -1) : (r < _uvRows.Count ? _uvRows[r].Vertex : -1),
        _ => r < _verts.Length ? _verts[r] : -1,
    };

    private void UpdateStatus()
    {
        if (_status == null) return;
        _status.Text = $"{_grid.RowCount} row(s), {_grid.Selected.Count} highlighted" + (_selVerts.Count > 0 ? $", {_selVerts.Count} vertex(es) selected in the viewport (orange)" : "");
    }

    // ---------------------------------------------------------------- 열

    private static readonly Color AxisX = new(1f, 0.4f, 0.4f), AxisY = new(0.5f, 1f, 0.45f), AxisZ = new(0.45f, 0.6f, 1f), UvTint = new(0.8f, 0.6f, 1f), WeightTint = new(1f, 0.75f, 0.4f);

    private GridColumn IdColumn(string title, Func<int, string> text) => new() { Title = title, Kind = GridColKind.Text, Width = 110, Text = text };

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
            Text = r =>
            {
                var cs = _corners[_verts[r]]; if (cs == null) return "";
                return string.Join("  ", cs.Select(h => m.Hes[h].Uv0).Distinct().Take(3).Select(u => $"{u.X:0.###},{u.Y:0.###}")) + (cs.Select(h => m.Hes[h].Uv0).Distinct().Count() > 3 ? " …" : "");
            },
        });
        var skin = Node?.MeshShape?.Skin;
        if (skin != null)
            cols.Add(new GridColumn
            {
                Title = "Influences", Kind = GridColKind.Text, Width = 220, Tip = "Skin weights of this vertex (see the Skin Weights tab to edit)",
                Text = r => { int v = _verts[r]; var w = v < skin.Weights.Length ? skin.Weights[v] : null; return w == null ? "" : string.Join("  ", w.OrderByDescending(x => x.weight).Select(x => $"{JointName(skin, x.joint)} {x.weight:0.###}")); },
            });
    }

    private NVec3 PositionOf(PolyMesh m, int v)
    {
        var p = m.Verts[v].Position;
        return _world.ButtonPressed ? NVec3.Transform(p, Node!.WorldMatrix) : p;
    }

    private static float Get(NVec3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    private static NVec3 With(NVec3 v, int a, float x) { if (a == 0) v.X = x; else if (a == 1) v.Y = x; else v.Z = x; return v; }

    private void BuildUvColumns(PolyMesh m)
    {
        int set = UvSetIndex;
        var cols = _grid.Columns;
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

    private GridColumn PinColumn(PolyMesh m, Func<int, int[]> corners) => new()
    {
        Title = "Pinned", Kind = GridColKind.Bool, Width = 70, Tip = "UV pin (UV Editor → Pin): pinned UVs do not move in Unfold/Optimize",
        Bool = r => corners(r).Any(h => m.Hes[h].PinUv),
        SetBool = (rows, on) => { foreach (int r in rows) foreach (int h in corners(r)) { var he = m.Hes[h]; he.PinUv = on; m.Hes[h] = he; } },
    };

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

    private string JointName(SkinCluster skin, int j) => j >= 0 && j < skin.Joints.Count ? _doc.Find(skin.Joints[j])?.Name ?? $"joint{j}" : $"joint{j}";

    private void BuildSkinColumns(SceneNode n, PolyMesh m)
    {
        var cols = _grid.Columns;
        cols.Add(IdColumn("Vertex", r => $"vtx[{_verts[r]}]"));
        var skin = n.MeshShape?.Skin;
        if (skin == null) { cols.Add(new GridColumn { Title = "No skin — bind the mesh first (Skin → Bind Skin)", Kind = GridColKind.Text, Width = 400, Text = _ => "" }); return; }
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

    private void CommitEdit(string name, Action change)
    {
        var n = Node; if (n == null) return;
        _editing = true;
        try { _doc.Undo.Push(new ComponentEditCommand($"Component Editor: {name}", _node, (_, _) => change())); }
        finally { _editing = false; }
        AfterEdit();
    }

    private void BeginDrag(string name)
    {
        if (Node == null) return;
        _dragCmd = new ComponentEditCommand($"Component Editor: {name}", _node);
        _dragCmd.Capture(_doc);
    }

    private void PreviewDrag(Action change)
    {
        var n = Node; if (n == null || _dragCmd == null) return;
        _editing = true;
        try { change(); ComponentEditCommand.NotifyAll(_doc, _node, n.MeshShape?.Skin != null); }
        finally { _editing = false; }
    }

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
        if (CurrentTab == Tab.Uvs && !AllSets || CurrentTab == Tab.Skin) { var keep = _grid.Selected.ToArray(); RebuildRows(); foreach (int r in keep) if (r < _grid.RowCount) _grid.Selected.Add(r); }
        _grid.QueueRedraw();
    }

    /// <summary>DebugDriver: comped tab T [set S] | edit ROW[,ROW..] COL VALUE | rows N | select | sel 0/1 | world 0/1</summary>
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

    private void _process() { if (_dirtyTarget) { _dirtyTarget = false; Retarget(); } if (_dirtyRows) { _dirtyRows = false; RebuildRows(); } }

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
