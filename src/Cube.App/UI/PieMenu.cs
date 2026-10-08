using Cube.App.Bridge;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// 파이 메뉴 항목 하나. 기본은 ActionId(ActionRegistry)로 실행되며, 동적 항목은 <see cref="Run"/>, 하위 메뉴는 <see cref="Sub"/>를 쓴다.
/// </summary>
/// <param name="Label">항목에 표시할 텍스트.</param>
/// <param name="ActionId">고르면 실행할 액션 ID(Run/Sub가 있으면 무시될 수 있음).</param>
/// <param name="Enabled">false면 흐리게 그리고 호버·선택되지 않는다.</param>
public sealed record PieItem(string Label, string ActionId, bool Enabled = true)
{
    /// <summary>하위 파이 항목 생성기. 있으면 이 항목을 고를 때 서브 파이가 열린다.</summary>
    public Func<List<PieItem>>? Sub { get; init; }
    /// <summary>ActionId 대신 직접 실행할 동작(동적 항목용).</summary>
    public Action? Run { get; init; }
    /// <summary>항목 왼쪽에 그릴 아이콘(머티리얼 썸네일 등).</summary>
    public Texture2D? Icon { get; init; }
}

/// <summary>
/// Maya 마킹 메뉴식 파이 메뉴. 마우스 버튼을 누른 채 열리고, 누른 채 항목 방향으로 이동한 뒤 떼면 실행된다.
/// 최대 8개는 방사형(N, NE, E, SE, S, SW, W, NW 순), 나머지는 중심 아래 세로 목록으로 배치한다.
/// </summary>
/// <remarks>
/// 좌표는 모두 이 컨트롤(뷰포트 패널 전체를 덮는 오버레이)의 로컬 px다. 입력은 직접 받지 않고(MouseFilter Ignore)
/// ViewportPanel이 <see cref="UpdatePointer"/>/<see cref="Release"/>를 호출해 구동한다.
/// 호버 판정: ① 항목 사각형(4px 여유) 직접 히트 ② 아니면 중심 데드존 밖에서 방향 각도로 가장 가까운 방사형 항목(±22.5°+여유).
/// </remarks>
public partial class PieMenu : Control
{
    /// <summary>현재 열린 항목 목록(인덱스 0~7 = 방사형 방향, 8 이후 = 오버플로 목록).</summary>
    private readonly List<PieItem> _items = new();
    /// <summary>항목별 화면 사각형(Layout이 계산, 같은 인덱스).</summary>
    private readonly List<Rect2> _rects = new();
    /// <summary>파이 중심(메뉴를 연 마우스 위치, 로컬 px).</summary>
    private Vector2 _center;
    /// <summary>하이라이트된 항목 인덱스(-1 = 없음).</summary>
    private int _hover = -1;
    /// <summary>테마 기본 글꼴(글자 폭 측정과 그리기).</summary>
    private Font _font = null!;
    /// <summary>글꼴 크기(12 × UI 배율, 열 때 계산).</summary>
    private int _fontSize;

    /// <summary>메뉴가 열려 있는지(항목이 하나라도 있을 때만 true).</summary>
    public bool IsOpen { get; private set; }
    /// <summary>버튼을 뗀 뒤에도 열려 있는 서브 파이(LMB로 선택, RMB/Esc로 닫음).</summary>
    public bool Sticky { get; private set; }
    /// <summary>서브 파이 제목(중심 아래 작은 상자로 표시, 없으면 null).</summary>
    public string? Title { get; private set; }
    /// <summary>파이 중심(로컬 px).</summary>
    public Vector2 Center => _center;
    /// <summary>방사형 항목까지의 거리(90px × UI 배율).</summary>
    public float Radius => 90f * CubeApp.Instance.UiScale;
    /// <summary>중심 데드존 반지름. 이 안에서는 방향 선택이 일어나지 않는다(18px × 배율).</summary>
    public float DeadZone => 18f * CubeApp.Instance.UiScale;

    /// <summary>방사형 8방향 단위 벡터(화면 좌표라 Y가 아래로 양수): N, NE, E, SE, S, SW, W, NW.</summary>
    private static readonly Vector2[] Dirs =
    {
        new(0, -1), new(0.7071f, -0.7071f), new(1, 0), new(0.7071f, 0.7071f),
        new(0, 1), new(-0.7071f, 0.7071f), new(-1, 0), new(-0.7071f, -0.7071f),
    };

    /// <summary>입력을 받지 않는 전체 화면 오버레이로 설정하고 숨긴 상태로 시작한다.</summary>
    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
        _font = GetThemeDefaultFont();
    }

    /// <summary>
    /// 항목과 중심으로 메뉴를 연다. 레이아웃을 계산하고, 항목이 없으면 열리지 않는다.
    /// </summary>
    /// <param name="items">표시할 항목(앞 8개가 방사형).</param>
    /// <param name="centerLocal">중심 위치(이 컨트롤 로컬 px).</param>
    /// <param name="sticky">true면 버튼을 뗀 뒤에도 열린 채로 클릭을 기다린다(서브 파이).</param>
    /// <param name="title">중심 아래에 표시할 제목.</param>
    public void Open(IEnumerable<PieItem> items, Vector2 centerLocal, bool sticky = false, string? title = null)
    {
        _items.Clear(); _items.AddRange(items);
        _center = centerLocal;
        _hover = -1;
        Sticky = sticky; Title = title;
        _fontSize = (int)(12 * CubeApp.Instance.UiScale);
        Layout();
        IsOpen = _items.Count > 0;
        Visible = IsOpen;
        QueueRedraw();
    }

    /// <summary>
    /// 항목 사각형을 계산한다. 높이는 아이콘 항목이 있으면 아이콘+6, 아니면 24(× 배율). 앞 8개는 방향 벡터 × Radius 지점에 놓되
    /// 오른쪽 항목은 왼쪽 끝을, 왼쪽 항목은 오른쪽 끝을, 위/아래 항목은 가운데를 그 지점에 맞춘다.
    /// 9번째부터는 오버플로: 아래(공간이 모자라면 위) 영역에 열당 2~6개씩 세로 목록으로, 열 묶음 전체를 가운데 정렬(화면 안으로 자름)한다.
    /// </summary>
    private void Layout()
    {
        _rects.Clear();
        float s = CubeApp.Instance.UiScale;
        float padX = 10 * s, h = (_items.Any(it => it.Icon != null) ? IconPx + 6 : 24) * s;
        // 각 항목의 폭 = 글자 폭 + 좌우 여백(+ 아이콘).
        for (int i = 0; i < _items.Count; i++)
        {
            float w = ItemWidth(_items[i], padX, s);
            Vector2 pos;
            if (i < 8)
            {
                var d = Dirs[i];
                var p = _center + d * Radius;
                // 좌우 항목은 중심에서 바깥쪽으로 정렬
                float x = d.X > 0.1f ? p.X : d.X < -0.1f ? p.X - w : p.X - w / 2;
                pos = new Vector2(x, p.Y - h / 2);
            }
            else
            {
                // 오버플로: 아래쪽에 열당 최대 6개, 열은 가운데 정렬(열 폭은 그 열의 가장 넓은 항목)
                // 아래 공간에 맞게 열당 개수를 정한다(2~6개). 공간이 모자라면 열을 늘린다
                float startY = _center.Y + Radius + h * 1.6f;
                // 열 수와 열 폭(오버플로 항목 중 가장 넓은 것), 이 항목의 열/행 위치를 계산한다.
                int overflow = _items.Count - 8;
                int perCol = Math.Clamp((int)((Size.Y - startY) / (h + 4 * s)), 2, 6);
                // 아래 공간이 모자라면(열이 너무 많아지면) 위쪽에 둔다
                int colsBelow = (overflow + perCol - 1) / perCol;
                float aboveSpace = _center.Y - Radius - h * 1.6f;
                int perColAbove = Math.Clamp((int)(aboveSpace / (h + 4 * s)), 2, 6);
                if (perCol < 6 && perColAbove > perCol) { perCol = perColAbove; startY = aboveSpace - perCol * (h + 4 * s); if (startY < 4 * s) startY = 4 * s; }
                int cols = (overflow + perCol - 1) / perCol;
                float colW = 0;
                for (int j = 8; j < _items.Count; j++) colW = MathF.Max(colW, ItemWidth(_items[j], padX, s));
                float gap = 8 * s;
                int k = i - 8, col = k / perCol, row = k % perCol;
                // 열 묶음 전체 폭을 중심에 맞추되 화면 좌우 4px 안쪽으로 자른다.
                float blockW = cols * colW + (cols - 1) * gap;
                float left = Math.Clamp(_center.X - blockW / 2, 4 * s, MathF.Max(4 * s, Size.X - blockW - 4 * s));
                pos = new Vector2(left + col * (colW + gap) + (colW - w) / 2, startY + row * (h + 4 * s));
            }
            _rects.Add(new Rect2(pos, new Vector2(w, h)));
        }
    }

    /// <summary>아이콘 크기(UI 배율 1 기준 px).</summary>
    private const float IconPx = 34;

    /// <summary>항목 폭(px): 라벨 글자 폭 + 좌우 여백 + 아이콘이 있으면 아이콘 폭과 간격.</summary>
    private float ItemWidth(PieItem it, float padX, float s)
        => _font.GetStringSize(it.Label, HorizontalAlignment.Left, -1, _fontSize).X + padX * 2 + (it.Icon != null ? (IconPx + 6) * s : 0);

    /// <summary>마우스 위치로 하이라이트 갱신.</summary>
    public void UpdatePointer(Vector2 local)
    {
        // 하이라이트를 다시 계산하고, 바뀌었을 때만 다시 그린다.
        if (!IsOpen) return;
        int prev = _hover;
        _hover = -1;
        // 1) 사각형 직접 히트(목록 항목 포함)
        for (int i = 0; i < _rects.Count; i++) if (_rects[i].Grow(4).HasPoint(local)) { _hover = i; break; }
        // 2) 방사형: 데드존 밖이면 각도로 가장 가까운 항목
        if (_hover < 0)
        {
            var d = local - _center;
            if (d.Length() > DeadZone)
            {
                int n = Math.Min(8, _items.Count);
                float best = float.MaxValue;
                var nd = d.Normalized();
                for (int i = 0; i < n; i++)
                {
                    // 비활성 항목은 고를 수 없다.
                    float ang = MathF.Acos(Math.Clamp(nd.Dot(Dirs[i]), -1f, 1f));
                    if (ang < best && ang < MathF.PI / 8f + 0.2f) { best = ang; _hover = i; }
                }
            }
        }
        if (_hover >= 0 && !_items[_hover].Enabled) _hover = -1;
        if (prev != _hover) QueueRedraw();
    }

    /// <returns>하이라이트된(활성) 항목, 없으면 null. 실행은 호출자(ViewportPanel.ExecutePie)가 한다.</returns>
    /// <summary>버튼을 뗐을 때: 선택 항목을 반환하고 닫는다(없으면 null).</summary>
    public PieItem? Release()
    {
        var chosen = _hover >= 0 ? _items[_hover] : null;
        Close();
        return chosen;
    }

    /// <summary>메뉴를 닫고 상태(하이라이트·sticky·제목)를 초기화한다.</summary>
    public void Close()
    {
        IsOpen = false; Visible = false; _hover = -1; Sticky = false; Title = null;
        QueueRedraw();
    }

    /// <summary>
    /// 메뉴를 그린다: 중심 점과 데드존 원, (있으면) 제목 상자, 방사형 항목은 데드존에서 항목 가장자리까지 안내선,
    /// 각 항목 상자(호버 = 강조색)와 라벨(비활성 = 흐림), 아이콘이 있으면 왼쪽에 아이콘.
    /// </summary>
    public override void _Draw()
    {
        if (!IsOpen) return;
        float s = CubeApp.Instance.UiScale;
        var bg = MathConvert.Rgb(0x2b2b2b, 0.95f);
        var border = MathConvert.Rgb(0x6a6a6a);
        var hi = MayaTheme.Accent;
        var text = MayaTheme.Text;
        var dim = MayaTheme.TextDim;
        // 중심 표시와 안내선
        DrawCircle(_center, 5 * s, MathConvert.Rgb(0xdddddd));
        DrawArc(_center, DeadZone, 0, MathF.Tau, 32, MathConvert.Rgb(0x888888, 0.6f), 1 * s, true);
        if (!string.IsNullOrEmpty(Title))
        {
            // 서브 파이 제목: 중심 아래 작은 상자
            var ts = _font.GetStringSize(Title, HorizontalAlignment.Left, -1, _fontSize);
            var tr = new Rect2(_center.X - ts.X / 2 - 6 * s, _center.Y + DeadZone + 4 * s, ts.X + 12 * s, ts.Y + 4 * s);
            var tstyle = new StyleBoxFlat { BgColor = MathConvert.Rgb(0x1e1e1e, 0.9f), BorderColor = border };
            tstyle.SetBorderWidthAll((int)(1 * s)); tstyle.SetCornerRadiusAll((int)(3 * s));
            DrawStyleBox(tstyle, tr);
            DrawString(_font, new Vector2(tr.Position.X + 6 * s, tr.Position.Y + 2 * s + ts.Y - _font.GetDescent(_fontSize)), Title, HorizontalAlignment.Left, -1, _fontSize, dim);
        }
        // 항목마다 상자와 글자를 그린다.
        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            bool enabled = _items[i].Enabled;
            bool hover = i == _hover;
            if (i < 8)
            {
                // 방사형 항목: 사각형에서 중심을 향하는 쪽 가장자리 점까지 안내선을 긋는다.
                var edge = r.GetCenter() - Dirs[i] * (r.Size.X / 2 * MathF.Abs(Dirs[i].X) + r.Size.Y / 2 * MathF.Abs(Dirs[i].Y));
                DrawLine(_center + Dirs[i] * DeadZone, edge, MathConvert.Rgb(0x777777, 0.8f), 1 * s, true);
            }
            // 상자 배경/테두리(호버 시 강조색), 둥근 모서리.
            var style = new StyleBoxFlat { BgColor = hover ? hi : bg, BorderColor = hover ? hi : border };
            style.SetBorderWidthAll((int)(1 * s)); style.SetCornerRadiusAll((int)(4 * s));
            DrawStyleBox(style, r);
            var col = !enabled ? dim : hover ? Colors.White : text;
            var size = _font.GetStringSize(_items[i].Label, HorizontalAlignment.Left, -1, _fontSize);
            float textX = r.Position.X + (r.Size.X - size.X) / 2;
            if (_items[i].Icon is { } icon)
            {
                // 아이콘은 왼쪽, 글자는 그 오른쪽(남는 폭 가운데)
                float ip = IconPx * s;
                var ir = new Rect2(r.Position.X + 4 * s, r.Position.Y + (r.Size.Y - ip) / 2, ip, ip);
                DrawTextureRect(icon, ir, false, enabled ? Colors.White : new Color(1, 1, 1, 0.4f));
                float rest = r.Size.X - (ip + 8 * s);
                textX = r.Position.X + ip + 8 * s + (rest - size.X) / 2;
            }
            DrawString(_font, new Vector2(textX, r.Position.Y + (r.Size.Y + size.Y) / 2 - _font.GetDescent(_fontSize)), _items[i].Label, HorizontalAlignment.Left, -1, _fontSize, col);
        }
    }
}
