using Cube.App.Anim;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI.Anim;

/// <summary>
/// Animation Data(읽기 전용 키 보기): 클립 목록·정보, 트랙(노드 → Translate/Rotate/Scale), 선택한 채널의 키 표(프레임, 시간, 값).
/// 키 행을 누르면 그 시간으로 이동한다. Cube는 키를 만들거나 고치지 않는다(클립 삭제만 가능, Undo 지원).
/// 구성: 왼쪽 = 클립 목록 + 정보 + 삭제 버튼, 가운데 = 노드별 트랙 트리(자식 = T/R/S 채널과 키 개수), 오른쪽 = 채널 키 표.
/// 재생 상태(<see cref="AnimationPlayback"/>)가 단일 출처이며, 클립 선택은 재생기에 위임하고 재생기의 StateChanged로 다시 그린다.
/// 회전 키는 쿼터니언을 XYZ 오일러 각(도)으로 바꿔 보여 준다.
/// </summary>
public partial class AnimationDataWindow : FloatingPanel
{
    /// <summary>소유 셸(문서·Undo 접근).</summary>
    private Shell _shell = null!;
    /// <summary>재생 상태(현재 클립/시간/선택 노드). 이 창은 재생기의 상태를 보여 주고 클립 선택·시간 이동만 요청한다.</summary>
    private AnimationPlayback _pb = null!;
    /// <summary>문서의 클립 이름 목록(인덱스 = <c>Document.Animations</c> 인덱스).</summary>
    private ItemList _clipList = null!;
    /// <summary>현재 클립의 길이·프레임 수·fps·루프·트랙/키 수 요약.</summary>
    private Label _clipInfo = null!;
    /// <summary>
    /// _tracks: 노드 항목(이름, 키 수) 아래 채널 항목. 채널 항목 메타데이터 열0 = 트랙 인덱스, 열1 = <see cref="Channel"/>.
    /// _keys: 선택한 채널의 키 행(Frame, Time, X, Y, Z). 행 메타데이터 열0 = 키 시간(초).
    /// </summary>
    private Tree _tracks = null!, _keys = null!;
    /// <summary>켜면 선택된 오브젝트/조인트의 트랙만 보여 준다.</summary>
    private CheckBox _selOnly = null!;
    /// <summary>현재 클립 삭제 버튼(클립이 없으면 비활성).</summary>
    private Button _delete = null!;
    /// <summary>프로그램에서 목록을 채우는 중 표시. true인 동안 ItemSelected 이벤트를 사용자 조작으로 취급하지 않는다.</summary>
    private bool _sync;

    /// <summary>트랙의 채널 종류. 트리 메타데이터에 정수로 저장된다.</summary>
    private enum Channel { Translate, Rotate, Scale }

    /// <summary>
    /// 패널 UI를 만들고 재생기·문서·선택 이벤트를 구독한다. 패널 크기는 화면의 80%×70%와 기본 크기 중 작은 값.
    /// </summary>
    /// <param name="shell">소유 셸.</param>
    /// <param name="pb">애니메이션 재생 상태.</param>
    public void Setup(Shell shell, AnimationPlayback pb)
    {
        _shell = shell; _pb = pb;
        // UI 배율과 호스트 창 크기로 초기 크기를 정한다.
        float s = CubeApp.Instance.UiScale;
        Title = "Animation Data";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(820 * s, host.X * 0.8f), MathF.Min(460 * s, host.Y * 0.7f));
        MinPanelSize = new Vector2(520 * s, 280 * s);

        // 루트 = 가로 분할(클립 | (트랙 | 키)).
        var root = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Content.AddChild(root);

        // 클립
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(190 * s, 0) };
        left.AddChild(new Label { Text = "Clips" });
        _clipList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        // 사용자가 클립을 고르면 재생기에 위임(재생기가 StateChanged → Refresh로 되돌아온다).
        _clipList.ItemSelected += i => { if (!_sync) _pb.SelectClip((int)i); };
        left.AddChild(_clipList);
        _clipInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _clipInfo.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        left.AddChild(_clipInfo);
        _delete = new Button { Text = "Delete Clip", TooltipText = "Remove this clip from the scene (undoable). Key data cannot be edited in Cube." };
        _delete.Pressed += DeleteClip;
        left.AddChild(_delete);
        root.AddChild(left);

        var right = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddChild(right);

        // 트랙
        var mid = new VBoxContainer { CustomMinimumSize = new Vector2(220 * s, 0) };
        var mh = new HBoxContainer();
        mh.AddChild(new Label { Text = "Tracks", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _selOnly = new CheckBox { Text = "Selected only", TooltipText = "Show only tracks of selected objects/joints" };
        // "Selected only"를 바꾸면 트랙 목록만 다시 만든다.
        _selOnly.Toggled += _ => RebuildTracks();
        mh.AddChild(_selOnly);
        mid.AddChild(mh);
        // 열0 = 이름, 열1 = 키 개수(고정 폭).
        _tracks = new Tree { HideRoot = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Columns = 2 };
        _tracks.SetColumnExpand(1, false);
        _tracks.SetColumnCustomMinimumWidth(1, (int)(50 * s));
        // 채널 항목을 고르면 키 표를 그 채널로 다시 채운다.
        _tracks.ItemSelected += RebuildKeys;
        mid.AddChild(_tracks);
        right.AddChild(mid);

        // 키 표
        var keysBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        keysBox.AddChild(new Label { Text = "Keys (click a row to go to that time)" });
        _keys = new Tree { HideRoot = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Columns = 5, ColumnTitlesVisible = true, SelectMode = Tree.SelectModeEnum.Row };
        string[] titles = { "Frame", "Time (s)", "X", "Y", "Z" };
        for (int i = 0; i < titles.Length; i++) _keys.SetColumnTitle(i, titles[i]);
        // 키 행을 누르면 재생을 멈추고 그 키 시간으로 이동한다(메타데이터 = 시간 초).
        _keys.ItemSelected += () => { if (!_sync && _keys.GetSelected() is { } it) { _pb.Pause(); _pb.SetTime((float)it.GetMetadata(0).AsDouble()); } };
        keysBox.AddChild(_keys);
        right.AddChild(keysBox);

        // 재생 상태·문서·선택 변경 구독(해제는 _Notification Predelete에서).
        _pb.StateChanged += Refresh;
        VisibilityChanged += () => { if (Visible) Refresh(); }; // 도크 탭으로 전환될 때
        shell.Document.Changed += OnDocChanged;
        shell.Document.Selection.Changed += OnSelChanged;
        Refresh();
    }

    /// <summary>도킹/떼어 내기로 트리를 옮겨도 구독을 유지하고, 실제로 지워질 때만 해제한다.</summary>
    public override void _Notification(int what)
    {
        if (what != (int)NotificationPredelete) return;
        if (_pb == null) return;
        _pb.StateChanged -= Refresh;
        _shell.Document.Changed -= OnDocChanged;
        _shell.Document.Selection.Changed -= OnSelChanged;
    }

    /// <summary>문서 변경 처리: 애니메이션 목록·리셋·이름 변경(트랙 노드 이름 표시)일 때만 갱신한다.</summary>
    private void OnDocChanged(DocChange c) { if (c.Kind is ChangeKind.AnimationsChanged or ChangeKind.Reset or ChangeKind.NodeRenamed) Refresh(); }
    /// <summary>선택 변경 처리: "Selected only"가 켜져 있을 때만 트랙 목록을 다시 만든다.</summary>
    private void OnSelChanged() { if (_selOnly.ButtonPressed) RebuildTracks(); }

    /// <summary>
    /// 클립 목록·정보·삭제 버튼을 다시 채우고 트랙 트리를 재구성한다. 보이지 않을 때는 비용을 아끼려고 건너뛴다
    /// (도크 탭 전환으로 보이게 되면 VisibilityChanged에서 다시 부른다).
    /// </summary>
    public void Refresh()
    {
        if (!Visible) return;
        var doc = _shell.Document;
        // 목록을 채우는 동안 ItemSelected가 SelectClip을 다시 부르지 않도록 _sync를 켠다.
        _sync = true;
        _clipList.Clear();
        foreach (var c in doc.Animations) _clipList.AddItem(c.Name);
        if (_pb.ClipIndex >= 0 && _pb.ClipIndex < _clipList.ItemCount) _clipList.Select(_pb.ClipIndex);
        var clip = _pb.Clip;
        _clipInfo.Text = clip == null ? "No animation in the scene.\nImport a glTF/FBX file that contains animation."
            : $"Length: {clip.Length:0.###} s ({_pb.EndFrame} frames)\nFrame rate: {clip.FrameRate:0.##} fps\nLoop: {(clip.Loop ? "on" : "off")}\nTracks: {clip.Tracks.Count}\nKeys: {clip.KeyCount}";
        _delete.Disabled = clip == null;
        _sync = false;
        RebuildTracks();
    }

    /// <summary>패널 열기/닫기 토글(Windows 메뉴 액션). 열 때 내용을 갱신한다.</summary>
    public void Toggle()
    {
        if (Visible) Close();
        else { Open(); Refresh(); }
    }

    /// <summary>
    /// 현재 클립의 트랙 트리를 다시 만든다. 노드 항목은 선택 불가(채널 항목만 고를 수 있음)이며,
    /// 선택된 노드의 항목만 펼친다. 문서에 노드가 없으면 "(missing)"으로 표시한다. 키 표도 비운다.
    /// </summary>
    private void RebuildTracks()
    {
        _tracks.Clear();
        _keys.Clear();
        var clip = _pb.Clip;
        var root = _tracks.CreateItem();
        if (clip == null) return;
        var doc = _shell.Document;
        // 재생기가 아는 선택 노드 집합(오브젝트/조인트).
        var sel = _pb.SelectedNodes();
        foreach (var t in clip.Tracks)
        {
            if (_selOnly.ButtonPressed && !sel.Contains(t.Node)) continue;
            var n = doc.Find(t.Node);
            var item = _tracks.CreateItem(root);
            item.SetText(0, n?.Name ?? t.NodeName + " (missing)");
            item.SetText(1, t.KeyCount.ToString());
            item.SetSelectable(0, false); item.SetSelectable(1, false);
            item.Collapsed = !sel.Contains(t.Node);
            // 키가 있는 채널만 자식 항목으로 추가하는 로컬 함수. 메타데이터에 트랙 인덱스와 채널을 넣는다.
            void Ch(Channel ch, int count)
            {
                if (count == 0) return;
                var ci = _tracks.CreateItem(item);
                ci.SetText(0, ch.ToString());
                ci.SetText(1, count.ToString());
                ci.SetMetadata(0, clip.Tracks.IndexOf(t));
                ci.SetMetadata(1, (int)ch);
            }
            Ch(Channel.Translate, t.Position.Count);
            Ch(Channel.Rotate, t.Rotation.Count);
            Ch(Channel.Scale, t.Scale.Count);
        }
    }

    /// <summary>
    /// 선택한 채널 항목의 키 표를 채운다. 노드 항목처럼 메타데이터가 없으면 비운 채 끝낸다.
    /// 회전 채널은 열 제목을 X°/Y°/Z°로 바꾸고 쿼터니언을 XYZ 오일러(도)로 변환해 표시한다.
    /// </summary>
    private void RebuildKeys()
    {
        _keys.Clear();
        var clip = _pb.Clip;
        var it = _tracks.GetSelected();
        // 채널 항목이 아니거나(메타데이터 없음) 트랙 인덱스가 범위 밖이면 표를 비운 채로 둔다.
        if (clip == null || it == null || it.GetMetadata(0).VariantType == Variant.Type.Nil) return;
        int ti = it.GetMetadata(0).AsInt32();
        if (ti < 0 || ti >= clip.Tracks.Count) return;
        var t = clip.Tracks[ti];
        var ch = (Channel)it.GetMetadata(1).AsInt32();
        // 회전은 각도 단위 표시.
        _keys.SetColumnTitle(2, ch == Channel.Rotate ? "X°" : "X");
        _keys.SetColumnTitle(3, ch == Channel.Rotate ? "Y°" : "Y");
        _keys.SetColumnTitle(4, ch == Channel.Rotate ? "Z°" : "Z");
        var root = _keys.CreateItem();
        // 키 한 행 추가: 프레임 = 시간 × fps, 메타데이터 = 시간(행 클릭 시 이동용).
        void Row(float time, System.Numerics.Vector3 v, string fmt)
        {
            var r = _keys.CreateItem(root);
            r.SetText(0, (time * clip.FrameRate).ToString("0.##"));
            r.SetText(1, time.ToString("0.###"));
            r.SetText(2, v.X.ToString(fmt)); r.SetText(3, v.Y.ToString(fmt)); r.SetText(4, v.Z.ToString(fmt));
            r.SetMetadata(0, time);
        }
        switch (ch)
        {
            case Channel.Translate: foreach (var k in t.Position) Row(k.Time, k.Value, "0.####"); break;
            case Channel.Scale: foreach (var k in t.Scale) Row(k.Time, k.Value, "0.####"); break;
            case Channel.Rotate: foreach (var k in t.Rotation) Row(k.Time, Transform3.QuaternionToEulerXYZDegrees(k.Value), "0.##"); break;
        }
    }

    /// <summary>
    /// 현재 클립을 문서에서 지운다(Undo 가능한 <see cref="SetAnimationsCommand"/>). 먼저 재생 포즈를 rest로 되돌려
    /// 지워진 클립의 포즈가 노드에 남지 않게 한다.
    /// </summary>
    private void DeleteClip()
    {
        var clip = _pb.Clip;
        if (clip == null) return;
        _pb.Rest();
        _shell.Document.Undo.Push(new SetAnimationsCommand("Delete Animation Clip", Array.Empty<AnimationClip>(), new[] { clip }));
    }
}
