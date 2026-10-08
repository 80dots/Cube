using Cube.App.Bridge;
using Godot;

namespace Cube.App.UI;

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
public partial class PieMenu : Control
{
    private readonly List<PieItem> _items = new();
    private readonly List<Rect2> _rects = new();
    private Vector2 _center;
    private int _hover = -1;
    private Font _font = null!;
    private int _fontSize;

    public bool IsOpen { get; private set; }
    /// <summary>버튼을 뗀 뒤에도 열려 있는 서브 파이(LMB로 선택, RMB/Esc로 닫음).</summary>
    public bool Sticky { get; private set; }
    public string? Title { get; private set; }
    public Vector2 Center => _center;
    public float Radius => 90f * CubeApp.Instance.UiScale;
    public float DeadZone => 18f * CubeApp.Instance.UiScale;

    private static readonly Vector2[] Dirs =
    {
        new(0, -1), new(0.7071f, -0.7071f), new(1, 0), new(0.7071f, 0.7071f),
        new(0, 1), new(-0.7071f, 0.7071f), new(-1, 0), new(-0.7071f, -0.7071f),
    };

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Visible = false;
        _font = GetThemeDefaultFont();
    }

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

    private void Layout()
    {
        _rects.Clear();
        float s = CubeApp.Instance.UiScale;
        float padX = 10 * s, h = (_items.Any(it => it.Icon != null) ? IconPx + 6 : 24) * s;
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
                float blockW = cols * colW + (cols - 1) * gap;
                float left = Math.Clamp(_center.X - blockW / 2, 4 * s, MathF.Max(4 * s, Size.X - blockW - 4 * s));
                pos = new Vector2(left + col * (colW + gap) + (colW - w) / 2, startY + row * (h + 4 * s));
            }
            _rects.Add(new Rect2(pos, new Vector2(w, h)));
        }
    }

    /// <summary>아이콘 크기(UI 배율 1 기준 px).</summary>
    private const float IconPx = 34;

    private float ItemWidth(PieItem it, float padX, float s)
        => _font.GetStringSize(it.Label, HorizontalAlignment.Left, -1, _fontSize).X + padX * 2 + (it.Icon != null ? (IconPx + 6) * s : 0);

    /// <summary>마우스 위치로 하이라이트 갱신.</summary>
    public void UpdatePointer(Vector2 local)
    {
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
                    float ang = MathF.Acos(Math.Clamp(nd.Dot(Dirs[i]), -1f, 1f));
                    if (ang < best && ang < MathF.PI / 8f + 0.2f) { best = ang; _hover = i; }
                }
            }
        }
        if (_hover >= 0 && !_items[_hover].Enabled) _hover = -1;
        if (prev != _hover) QueueRedraw();
    }

    /// <summary>버튼을 뗐을 때: 선택 항목을 반환하고 닫는다(없으면 null).</summary>
    public PieItem? Release()
    {
        var chosen = _hover >= 0 ? _items[_hover] : null;
        Close();
        return chosen;
    }

    public void Close()
    {
        IsOpen = false; Visible = false; _hover = -1; Sticky = false; Title = null;
        QueueRedraw();
    }

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
        for (int i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            bool enabled = _items[i].Enabled;
            bool hover = i == _hover;
            if (i < 8)
            {
                var edge = r.GetCenter() - Dirs[i] * (r.Size.X / 2 * MathF.Abs(Dirs[i].X) + r.Size.Y / 2 * MathF.Abs(Dirs[i].Y));
                DrawLine(_center + Dirs[i] * DeadZone, edge, MathConvert.Rgb(0x777777, 0.8f), 1 * s, true);
            }
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
