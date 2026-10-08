using Cube.App.Bridge;
using Godot;

namespace Cube.App.Viewport;

/// <summary>뷰포트 위 2D 오버레이: 좌하단 축 기즈모, 카메라 이름, 마키 사각형.</summary>
/// <remarks>
/// 패널의 SubViewport 위를 꽉 채우는 Control이며 마우스를 무시한다(입력은 아래 패널로). 매 프레임 다시 그린다.
/// 그리기 순서: 카메라 이름(하단 중앙) → Poly Count(좌상단) → 축 기즈모(좌하단) → 페인트 브러시 원 → Create Polygon 폴리라인 → 마키 사각형.
/// 표시할 값(필드)은 패널과 툴이 바깥에서 설정한다.
/// </remarks>
public partial class ViewportOverlay : Control
{
    /// <summary>축 기즈모 방향을 계산할 카메라(null이면 축 기즈모를 그리지 않음).</summary>
    public Camera3D? Camera;
    /// <summary>하단 중앙에 표시할 뷰 이름(ViewportCamera.Label).</summary>
    public string CameraLabel = "persp";
    /// <summary>드래그 중인 마키 사각형(뷰포트 로컬 픽셀). null이면 없음.</summary>
    public Rect2? Marquee;
    /// <summary>페인트 브러시 원(중심 픽셀, 반지름 픽셀).</summary>
    public (Vector2 center, float radiusPx)? Brush;
    /// <summary>Create Polygon Tool 미리보기(화면 점 목록).</summary>
    public List<Vector2>? Polyline;
    /// <summary>좌상단 Poly Count HUD 수치(Shell이 갱신). null이면 그리지 않는다.</summary>
    public PolyCount? Stats;

    /// <summary>마우스를 통과시키고 부모를 꽉 채우도록 앵커를 설정한다.</summary>
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    /// <summary>오버레이 값이 바깥에서 바뀌므로 매 프레임 다시 그린다.</summary>
    public override void _Process(double delta) => QueueRedraw();

    /// <summary>
    /// Poly Count 표를 그린다: 열 = 라벨 / Scene / Object / Selected(컴포넌트 모드에서만), 행 = Verts/Edges/Faces/Tris/Objects.
    /// 숫자는 오른쪽 정렬·천 단위 구분, 선택 수는 현재 모드에 해당하는 행만 노란색으로 표시한다(UV 모드는 Verts 행에 UV 점 수).
    /// </summary>
    private void DrawPolyCount(PolyCount st, Font font, int fs, float s)
    {
        float x0 = 10 * s, y = 18 * s, line = fs + 4 * s;
        float colW = 62 * s, labelW = 54 * s;
        var dim = MathConvert.Rgb(0x9a9a9a); var txt = MathConvert.Rgb(0xdcdcdc); var hi = MathConvert.Rgb(0xffd54a);
        bool comp = st.Mode != Core.Selection.SelectMode.Object;
        // 텍스트 한 칸: right면 x를 오른쪽 끝으로 보고 문자열 폭만큼 왼쪽에서 시작
        void Cell(string text, float x, float yy, Color c, bool right)
        {
            float w = right ? font.GetStringSize(text, HorizontalAlignment.Left, -1, fs).X : 0;
            DrawString(font, new Vector2(x - w, yy), text, HorizontalAlignment.Left, -1, fs, c);
        }
        // 헤더
        Cell("Scene", x0 + labelW + colW, y, dim, true);
        Cell("Object", x0 + labelW + colW * 2, y, dim, true);
        if (comp) Cell("Selected", x0 + labelW + colW * 3, y, dim, true);
        y += line;
        // 행 정의: (라벨, Scene 값, Object 값, Selected 값, Selected 표시 여부)
        (string label, int scene, int obj, int sel, bool showSel)[] rows =
        {
            ("Verts", st.SceneVerts, st.ObjVerts, st.Mode == Core.Selection.SelectMode.Uv ? st.SelUvs : st.SelVerts, st.Mode is Core.Selection.SelectMode.Vertex or Core.Selection.SelectMode.Uv),
            ("Edges", st.SceneEdges, st.ObjEdges, st.SelEdges, st.Mode == Core.Selection.SelectMode.Edge),
            ("Faces", st.SceneFaces, st.ObjFaces, st.SelFaces, st.Mode == Core.Selection.SelectMode.Face),
            ("Tris", st.SceneTris, st.ObjTris, 0, false),
            ("Objects", st.SceneObjects, st.ObjObjects, 0, false),
        };
        // 각 행: 라벨(회색) + Scene + Object(0이면 '-') + Selected(해당 모드만)
        foreach (var (label, scene, obj, sel, showSel) in rows)
        {
            Cell(label, x0, y, dim, false);
            Cell(scene.ToString("N0"), x0 + labelW + colW, y, txt, true);
            Cell(obj > 0 ? obj.ToString("N0") : "-", x0 + labelW + colW * 2, y, txt, true);
            if (comp && showSel) Cell(sel.ToString("N0"), x0 + labelW + colW * 3, y, hi, true);
            y += line;
        }
    }

    /// <summary>오버레이 요소를 모두 그린다(픽셀 크기는 UI 배율을 곱함).</summary>
    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        var font = GetThemeDefaultFont();
        int fs = (int)(12 * s);

        // 카메라 이름 (하단 중앙)
        var label = CameraLabel;
        var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
        DrawString(font, new Vector2((Size.X - textSize.X) / 2, Size.Y - 8 * s), label, HorizontalAlignment.Left, -1, fs, MathConvert.Rgb(0xdcdcdc));

        // Poly Count HUD (좌상단, Maya Heads Up Display)
        if (Stats != null && CubeApp.Instance.Settings.ShowPolyCount) DrawPolyCount(Stats, font, fs, s);

        // 축 기즈모 (좌하단)
        if (Camera != null)
        {
            float len = 36 * s;
            var origin = new Vector2(44 * s, Size.Y - 44 * s);
            // 카메라 기저의 역(전치)으로 월드 축을 카메라 공간으로 바꾸고 화면 xy로 그린다(y는 화면 아래가 +라 뒤집음)
            var b = Camera.GlobalTransform.Basis.Inverse(); // 월드 → 카메라
            (Vector3 axis, Color color, string name)[] axes =
            {
                (Vector3.Right, MathConvert.Rgb(0xff2a2a), "x"),
                (Vector3.Up, MathConvert.Rgb(0x5aff2a), "y"),
                (Vector3.Back, MathConvert.Rgb(0x2a6aff), "z"),
            };
            // 깊이(카메라 z) 순으로 뒤→앞
            var order = axes.Select(a => (a, depth: (b * a.axis).Z)).OrderBy(t => t.depth).ToArray();
            foreach (var (a, _) in order)
            {
                var v = b * a.axis;
                var p2 = origin + new Vector2(v.X, -v.Y) * len;
                DrawLine(origin, p2, a.color, 2 * s, true);
                DrawCircle(p2, 5 * s, a.color);
                DrawString(font, p2 + new Vector2(6 * s, 4 * s), a.name, HorizontalAlignment.Left, -1, fs, a.color);
            }
        }

        // 가중치 페인트 브러시: 반지름 원 + 중심 점(빨강 계열)
        if (Brush is { } br)
        {
            DrawArc(br.center, Mathf.Max(br.radiusPx, 2f), 0, Mathf.Tau, 48, new Color(1f, 0.35f, 0.35f, 0.9f), 1.5f * s, true);
            DrawCircle(br.center, 2 * s, new Color(1f, 0.35f, 0.35f, 0.9f));
        }
        // Create Polygon Tool: 찍은 점을 잇는 선, 3점 이상이면 닫는 선(반투명), 각 점에 원
        if (Polyline is { Count: > 0 } pl)
        {
            for (int i = 0; i + 1 < pl.Count; i++) DrawLine(pl[i], pl[i + 1], MathConvert.Rgb(0xffe034), 1.5f * s, true);
            if (pl.Count > 2) DrawLine(pl[^1], pl[0], new Color(1f, 0.88f, 0.2f, 0.4f), 1f * s, true);
            foreach (var p in pl) DrawCircle(p, 3.5f * s, MathConvert.Rgb(0xffe034));
        }
        // 마키
        if (Marquee is { } r)
        {
            DrawRect(r, new Color(1, 1, 1, 0.08f), true);
            DrawRect(r, new Color(1, 1, 1, 0.9f), false, 1 * s);
        }
    }
}
