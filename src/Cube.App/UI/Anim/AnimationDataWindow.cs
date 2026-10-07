using Cube.App.Anim;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI.Anim;

/// <summary>
/// Animation Data(읽기 전용 키 보기): 클립 목록·정보, 트랙(노드 → Translate/Rotate/Scale), 선택한 채널의 키 표(프레임, 시간, 값).
/// 키 행을 누르면 그 시간으로 이동한다. Cube는 키를 만들거나 고치지 않는다(클립 삭제만 가능, Undo 지원).
/// </summary>
public partial class AnimationDataWindow : FloatingPanel
{
    private Shell _shell = null!;
    private AnimationPlayback _pb = null!;
    private ItemList _clipList = null!;
    private Label _clipInfo = null!;
    private Tree _tracks = null!, _keys = null!;
    private CheckBox _selOnly = null!;
    private Button _delete = null!;
    private bool _sync;

    private enum Channel { Translate, Rotate, Scale }

    public void Setup(Shell shell, AnimationPlayback pb)
    {
        _shell = shell; _pb = pb;
        float s = CubeApp.Instance.UiScale;
        Title = "Animation Data";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(820 * s, host.X * 0.8f), MathF.Min(460 * s, host.Y * 0.7f));
        MinPanelSize = new Vector2(520 * s, 280 * s);

        var root = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Content.AddChild(root);

        // 클립
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(190 * s, 0) };
        left.AddChild(new Label { Text = "Clips" });
        _clipList = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
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
        _selOnly.Toggled += _ => RebuildTracks();
        mh.AddChild(_selOnly);
        mid.AddChild(mh);
        _tracks = new Tree { HideRoot = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Columns = 2 };
        _tracks.SetColumnExpand(1, false);
        _tracks.SetColumnCustomMinimumWidth(1, (int)(50 * s));
        _tracks.ItemSelected += RebuildKeys;
        mid.AddChild(_tracks);
        right.AddChild(mid);

        // 키 표
        var keysBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        keysBox.AddChild(new Label { Text = "Keys (click a row to go to that time)" });
        _keys = new Tree { HideRoot = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Columns = 5, ColumnTitlesVisible = true, SelectMode = Tree.SelectModeEnum.Row };
        string[] titles = { "Frame", "Time (s)", "X", "Y", "Z" };
        for (int i = 0; i < titles.Length; i++) _keys.SetColumnTitle(i, titles[i]);
        _keys.ItemSelected += () => { if (!_sync && _keys.GetSelected() is { } it) { _pb.Pause(); _pb.SetTime((float)it.GetMetadata(0).AsDouble()); } };
        keysBox.AddChild(_keys);
        right.AddChild(keysBox);

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

    private void OnDocChanged(DocChange c) { if (c.Kind is ChangeKind.AnimationsChanged or ChangeKind.Reset or ChangeKind.NodeRenamed) Refresh(); }
    private void OnSelChanged() { if (_selOnly.ButtonPressed) RebuildTracks(); }

    public void Refresh()
    {
        if (!Visible) return;
        var doc = _shell.Document;
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

    public void Toggle()
    {
        if (Visible) Close();
        else { Open(); Refresh(); }
    }

    private void RebuildTracks()
    {
        _tracks.Clear();
        _keys.Clear();
        var clip = _pb.Clip;
        var root = _tracks.CreateItem();
        if (clip == null) return;
        var doc = _shell.Document;
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

    private void RebuildKeys()
    {
        _keys.Clear();
        var clip = _pb.Clip;
        var it = _tracks.GetSelected();
        if (clip == null || it == null || it.GetMetadata(0).VariantType == Variant.Type.Nil) return;
        int ti = it.GetMetadata(0).AsInt32();
        if (ti < 0 || ti >= clip.Tracks.Count) return;
        var t = clip.Tracks[ti];
        var ch = (Channel)it.GetMetadata(1).AsInt32();
        _keys.SetColumnTitle(2, ch == Channel.Rotate ? "X°" : "X");
        _keys.SetColumnTitle(3, ch == Channel.Rotate ? "Y°" : "Y");
        _keys.SetColumnTitle(4, ch == Channel.Rotate ? "Z°" : "Z");
        var root = _keys.CreateItem();
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

    private void DeleteClip()
    {
        var clip = _pb.Clip;
        if (clip == null) return;
        _pb.Rest();
        _shell.Document.Undo.Push(new SetAnimationsCommand("Delete Animation Clip", Array.Empty<AnimationClip>(), new[] { clip }));
    }
}
