using Godot;

namespace Cube.App.UI.ComponentEditor;

public enum GridColKind { Text, Float, Bool }

/// <summary>표의 열 하나. 값 접근은 행 번호(표 안의 순서)로 한다.</summary>
public sealed class GridColumn
{
    public string Title = "";
    public float Width = 70;
    public GridColKind Kind = GridColKind.Float;
    /// <summary>표시 문자열(Text 열) — Float 열은 Value를 서식화한다.</summary>
    public Func<int, string>? Text;
    public Func<int, double>? Value;
    /// <summary>값 쓰기(행들, 값들). null이면 읽기 전용.</summary>
    public Action<int[], double[]>? Set;
    public Func<int, bool>? Bool;
    public Action<int[], bool>? SetBool;
    /// <summary>가운데 버튼 드래그 1px당 변화량.</summary>
    public double Step = 0.01;
    public string Format = "0.0000";
    public Color? Tint;
    public string? Tip;
    public bool Editable => Kind == GridColKind.Bool ? SetBool != null : Set != null;
}

/// <summary>
/// 보이는 행만 그리는 가상 스크롤 표(Maya Component Editor 스타일). 행 수만 개에서도 그리기·스크롤 비용은 화면에 보이는 행 수에 비례한다.
/// 선택: 클릭/Shift 범위/Ctrl 토글/드래그. 편집: 더블클릭·Enter·F2·숫자 입력 → 입력 칸(선택 행 전체에 적용, "+=0.1" "-=" "*=" "/=" 상대값),
/// Bool 셀 클릭 = 토글, 가운데 버튼 좌우 드래그 = 값 조절(Shift ×0.1). Ctrl+C = 선택 행 TSV 복사, Ctrl+A = 전체 선택.
/// 첫 번째 열은 가로 스크롤해도 고정된다. 열 머리 경계를 끌면 폭 조절.
/// </summary>
public partial class DataGrid : Control
{
    public readonly List<GridColumn> Columns = new();
    public int RowCount { get; private set; }
    public readonly HashSet<int> Selected = new();
    public event Action? SelectionChanged;

    /// <summary>즉시 편집: (이름, 변경 함수). 호출자가 Undo 명령으로 감싼다.</summary>
    public Action<string, Action>? CommitEdit;
    /// <summary>드래그 편집: 시작(이름) → 미리보기(변경 함수, 여러 번) → 끝(확정 여부).</summary>
    public Action<string>? BeginDrag;
    public Action<Action>? PreviewDrag;
    public Action<bool>? EndDrag;
    /// <summary>행 머리 색(선택된 컴포넌트 강조 등). null = 기본.</summary>
    public Func<int, Color?>? RowTint;

    private VScrollBar _vbar = null!;
    private HScrollBar _hbar = null!;
    private LineEdit _editor = null!;
    private float _s = 1f;
    private float RowH => 22 * _s;
    private float HeadH => 24 * _s;
    private int FontSize => (int)(12 * _s);
    private int _anchor = -1, _focusRow = -1, _focusCol = 1;
    private bool _dragSelect;
    private int _resizeCol = -1; private float _resizeStartX, _resizeStartW;
    // 가운데 버튼 값 드래그
    private int _mdCol = -1; private int[] _mdRows = Array.Empty<int>(); private double[] _mdOrig = Array.Empty<double>(); private float _mdStartX; private bool _mdMoved;
    private int _editRow = -1, _editCol = -1;

    public override void _Ready()
    {
        _s = CubeApp.Instance.UiScale;
        ClipContents = true;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        _vbar = new VScrollBar(); AddChild(_vbar);
        _hbar = new HScrollBar(); AddChild(_hbar);
        _vbar.ValueChanged += _ => QueueRedraw();
        _hbar.ValueChanged += _ => QueueRedraw();
        _editor = new LineEdit { Visible = false, SelectAllOnFocus = true };
        _editor.TextSubmitted += _ => FinishEdit(true);
        _editor.FocusExited += () => { if (_editor.Visible) FinishEdit(true); };
        _editor.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { FinishEdit(false); AcceptEvent(); } };
        AddChild(_editor);
        Resized += Layout;
        Layout();
    }

    public void SetRows(int count)
    {
        RowCount = count;
        Selected.RemoveWhere(r => r >= count);
        if (_focusRow >= count) _focusRow = count - 1;
        Layout();
        QueueRedraw();
    }

    public void ClearSelection() { Selected.Clear(); _anchor = _focusRow = -1; QueueRedraw(); }

    private float TotalWidth => Columns.Sum(c => c.Width * _s);
    private float BodyH => Math.Max(0, Size.Y - HeadH - (_hbar.Visible ? _hbar.Size.Y : 0));
    private float BodyW => Math.Max(0, Size.X - (_vbar.Visible ? _vbar.Size.X : 0));
    private int FirstRow => (int)_vbar.Value;

    private void Layout()
    {
        if (_vbar == null) return;
        float sb = 12 * _s;
        _hbar.Visible = TotalWidth > Size.X - sb;
        _vbar.Visible = RowCount * RowH > Size.Y - HeadH - (_hbar.Visible ? sb : 0);
        _vbar.Position = new Vector2(Size.X - sb, HeadH); _vbar.Size = new Vector2(sb, Math.Max(0, Size.Y - HeadH - (_hbar.Visible ? sb : 0)));
        _hbar.Position = new Vector2(0, Size.Y - sb); _hbar.Size = new Vector2(Math.Max(0, Size.X - (_vbar.Visible ? sb : 0)), sb);
        int visRows = Math.Max(1, (int)(BodyH / RowH));
        _vbar.MaxValue = RowCount; _vbar.Page = visRows; _vbar.Step = 1;
        if (_vbar.Value > Math.Max(0, RowCount - visRows)) _vbar.Value = Math.Max(0, RowCount - visRows);
        float fixedW = Columns.Count > 0 ? Columns[0].Width * _s : 0;
        _hbar.MaxValue = Math.Max(0, TotalWidth - fixedW); _hbar.Page = Math.Max(1, BodyW - fixedW);
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 좌표

    /// <summary>열의 화면 x 범위(고정 열 = 0번).</summary>
    private (float x0, float x1) ColRange(int c)
    {
        if (c == 0) return (0, Columns[0].Width * _s);
        float x = Columns[0].Width * _s - (float)_hbar.Value;
        for (int i = 1; i < c; i++) x += Columns[i].Width * _s;
        return (x, x + Columns[c].Width * _s);
    }

    private int ColAt(float x)
    {
        if (Columns.Count == 0) return -1;
        if (x < Columns[0].Width * _s) return 0;
        for (int c = 1; c < Columns.Count; c++) { var (a, b) = ColRange(c); if (x >= a && x < b) return c; }
        return -1;
    }

    private int RowAt(float y) => y < HeadH ? -1 : FirstRow + (int)((y - HeadH) / RowH);

    // ---------------------------------------------------------------- 그리기

    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        int fs = FontSize;
        DrawRect(new Rect2(Vector2.Zero, Size), MayaTheme.Field);
        if (Columns.Count == 0) return;
        int first = FirstRow, vis = (int)(BodyH / RowH) + 1;
        float fixedW = Columns[0].Width * _s;
        float textY(float top) => top + (RowH + font.GetAscent(fs) - font.GetDescent(fs)) / 2;
        for (int pass = 0; pass < 2; pass++) // 0 = 스크롤 열, 1 = 고정 열(위에 덮음)
        {
            for (int c = pass == 0 ? 1 : 0; c < (pass == 0 ? Columns.Count : 1); c++)
            {
                var (x0, x1) = ColRange(c);
                if (pass == 0 && (x1 < fixedW || x0 > BodyW)) continue;
                var col = Columns[c];
                for (int i = 0; i < vis; i++)
                {
                    int r = first + i; if (r >= RowCount) break;
                    float y = HeadH + i * RowH;
                    var cell = new Rect2(x0, y, x1 - x0, RowH);
                    bool sel = Selected.Contains(r);
                    Color bg = sel ? new Color(0.32f, 0.52f, 0.65f) : (r % 2 == 0 ? MayaTheme.Field : MayaTheme.Field.Lightened(0.04f));
                    if (c == 0) { var t = RowTint?.Invoke(r); bg = sel ? bg : (t ?? MayaTheme.PanelDark); }
                    else if (col.Tint is { } tint && !sel) bg = bg.Lerp(tint, 0.12f);
                    DrawRect(cell, bg);
                    if (r == _focusRow && c == _focusCol && HasFocus()) DrawRect(cell.Grow(-1), Colors.White, false, 1);
                    string text = CellText(col, r);
                    var tc = col.Editable || c == 0 ? MayaTheme.Text : MayaTheme.TextDim;
                    if (col.Kind == GridColKind.Float && col.Value != null && Math.Abs(col.Value(r)) < 1e-9 && col.Tint != null) tc = MayaTheme.TextDim;
                    DrawString(font, new Vector2(x0 + 4 * _s, textY(y)), text, col.Kind switch { GridColKind.Text => HorizontalAlignment.Left, GridColKind.Bool => HorizontalAlignment.Center, _ => HorizontalAlignment.Right }, x1 - x0 - 8 * _s, fs, tc);
                    DrawLine(new Vector2(x1, y), new Vector2(x1, y + RowH), MayaTheme.PanelDark, 1);
                }
                // 머리
                var head = new Rect2(x0, 0, x1 - x0, HeadH);
                DrawRect(head, MayaTheme.PanelDark);
                DrawString(font, new Vector2(x0 + 4 * _s, (HeadH + font.GetAscent(fs) - font.GetDescent(fs)) / 2), col.Title, HorizontalAlignment.Center, x1 - x0 - 8 * _s, fs, col.Tint ?? MayaTheme.Text);
                DrawLine(new Vector2(x1, 0), new Vector2(x1, HeadH), MayaTheme.Field, 1);
            }
        }
        if (RowCount == 0) DrawString(font, new Vector2(8 * _s, HeadH + RowH), "(no rows)", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
    }

    private static string CellText(GridColumn col, int r) => col.Kind switch
    {
        GridColKind.Bool => col.Bool?.Invoke(r) == true ? "■" : "□",
        GridColKind.Float => col.Value != null ? col.Value(r).ToString(col.Format) : col.Text?.Invoke(r) ?? "",
        _ => col.Text?.Invoke(r) ?? "",
    };

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (atPosition.Y >= HeadH) return "";
        int c = ColAt(atPosition.X);
        return c >= 0 ? Columns[c].Tip ?? Columns[c].Title : "";
    }

    // ---------------------------------------------------------------- 입력

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wb:
                {
                    int d = wb.ButtonIndex == MouseButton.WheelUp ? -1 : 1;
                    if (wb.ShiftPressed) _hbar.Value += d * 40 * _s; else _vbar.Value += d * 3;
                    AcceptEvent();
                    break;
                }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                OnLeft(mb); AcceptEvent(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mm:
                OnMiddle(mm); AcceptEvent(); break;
            case InputEventMouseMotion mo:
                OnMotion(mo); break;
            case InputEventKey { Pressed: true } k:
                if (OnKey(k)) AcceptEvent();
                break;
        }
    }

    private void OnLeft(InputEventMouseButton mb)
    {
        if (!mb.Pressed) { _dragSelect = false; if (_resizeCol >= 0) { _resizeCol = -1; Layout(); } return; }
        GrabFocus();
        var p = mb.Position;
        if (p.Y < HeadH)
        {
            // 열 경계 근처면 폭 조절
            for (int c = 0; c < Columns.Count; c++)
            {
                var (_, x1) = ColRange(c);
                if (Math.Abs(p.X - x1) < 5 * _s) { _resizeCol = c; _resizeStartX = p.X; _resizeStartW = Columns[c].Width; return; }
            }
            return;
        }
        int r = RowAt(p.Y), col = ColAt(p.X);
        if (r < 0 || r >= RowCount || col < 0) return;
        var column = Columns[col];
        if (mb.DoubleClick && column.Editable && column.Kind != GridColKind.Bool) { _focusRow = r; _focusCol = col; if (!Selected.Contains(r)) SelectOnly(r); StartEdit(r, col, null); return; }
        _focusCol = col;
        if (column.Kind == GridColKind.Bool && column.Editable && !mb.ShiftPressed && !mb.CtrlPressed)
        {
            if (!Selected.Contains(r)) SelectOnly(r);
            _focusRow = r;
            bool v = !(column.Bool?.Invoke(r) ?? false);
            var rows = Selected.OrderBy(x => x).ToArray();
            CommitEdit?.Invoke($"{column.Title} = {v}", () => column.SetBool!(rows, v));
            QueueRedraw();
            return;
        }
        if (mb.ShiftPressed && _anchor >= 0)
        {
            if (!mb.CtrlPressed) Selected.Clear();
            for (int i = Math.Min(_anchor, r); i <= Math.Max(_anchor, r); i++) Selected.Add(i);
        }
        else if (mb.CtrlPressed) { if (!Selected.Remove(r)) Selected.Add(r); _anchor = r; }
        else { Selected.Clear(); Selected.Add(r); _anchor = r; _dragSelect = true; }
        _focusRow = r;
        SelectionChanged?.Invoke();
        QueueRedraw();
    }

    private void SelectOnly(int r) { Selected.Clear(); Selected.Add(r); _anchor = r; SelectionChanged?.Invoke(); }

    private void OnMiddle(InputEventMouseButton mm)
    {
        if (mm.Pressed)
        {
            int r = RowAt(mm.Position.Y), c = ColAt(mm.Position.X);
            if (r < 0 || r >= RowCount || c < 0) return;
            var col = Columns[c];
            if (col.Kind != GridColKind.Float || col.Set == null || col.Value == null) return;
            if (!Selected.Contains(r)) SelectOnly(r);
            _focusRow = r; _focusCol = c;
            _mdCol = c; _mdRows = Selected.OrderBy(x => x).ToArray(); _mdOrig = _mdRows.Select(x => col.Value(x)).ToArray();
            _mdStartX = mm.Position.X; _mdMoved = false;
            BeginDrag?.Invoke($"Drag {col.Title}");
        }
        else if (_mdCol >= 0)
        {
            EndDrag?.Invoke(_mdMoved);
            _mdCol = -1;
            QueueRedraw();
        }
    }

    private void OnMotion(InputEventMouseMotion mo)
    {
        if (_resizeCol >= 0)
        {
            Columns[_resizeCol].Width = Math.Max(30, _resizeStartW + (mo.Position.X - _resizeStartX) / _s);
            Layout();
            return;
        }
        if (_mdCol >= 0)
        {
            var col = Columns[_mdCol];
            double delta = (mo.Position.X - _mdStartX) * col.Step * (mo.ShiftPressed ? 0.1 : 1);
            if (Math.Abs(mo.Position.X - _mdStartX) >= 1) _mdMoved = true;
            var vals = _mdOrig.Select(v => v + delta).ToArray();
            var rows = _mdRows;
            PreviewDrag?.Invoke(() => col.Set!(rows, vals));
            QueueRedraw();
            return;
        }
        if (_dragSelect && (mo.ButtonMask & MouseButtonMask.Left) != 0)
        {
            int r = Math.Clamp(RowAt(Math.Max(mo.Position.Y, HeadH)), 0, RowCount - 1);
            if (mo.Position.Y > Size.Y - RowH) _vbar.Value += 1; else if (mo.Position.Y < HeadH + 2) _vbar.Value -= 1;
            if (_anchor < 0 || r == _focusRow) return;
            Selected.Clear();
            for (int i = Math.Min(_anchor, r); i <= Math.Max(_anchor, r); i++) Selected.Add(i);
            _focusRow = r;
            SelectionChanged?.Invoke();
            QueueRedraw();
            return;
        }
        if (mo.Position.Y < HeadH)
        {
            bool edge = false;
            for (int c = 0; c < Columns.Count && !edge; c++) edge = Math.Abs(mo.Position.X - ColRange(c).x1) < 5 * _s;
            MouseDefaultCursorShape = edge ? CursorShape.Hsize : CursorShape.Arrow;
        }
        else MouseDefaultCursorShape = CursorShape.Arrow;
    }

    private bool OnKey(InputEventKey k)
    {
        if (_editor.Visible) return false;
        if (k.CtrlPressed && k.Keycode == Key.C) { CopySelection(); return true; }
        if (k.CtrlPressed && k.Keycode == Key.A) { Selected.Clear(); for (int i = 0; i < RowCount; i++) Selected.Add(i); SelectionChanged?.Invoke(); QueueRedraw(); return true; }
        if (k.CtrlPressed && (k.Keycode == Key.Z || k.Keycode == Key.Y))
        {
            Shell.Instance.Actions.Invoke(k.Keycode == Key.Y || k.ShiftPressed ? "edit.redo" : "edit.undo");
            return true;
        }
        switch (k.Keycode)
        {
            case Key.Up: case Key.Down:
                {
                    int r = Math.Clamp((_focusRow < 0 ? 0 : _focusRow) + (k.Keycode == Key.Up ? -1 : 1), 0, RowCount - 1);
                    if (k.ShiftPressed && _anchor >= 0) { Selected.Clear(); for (int i = Math.Min(_anchor, r); i <= Math.Max(_anchor, r); i++) Selected.Add(i); }
                    else { Selected.Clear(); Selected.Add(r); _anchor = r; }
                    _focusRow = r; EnsureVisible(r); SelectionChanged?.Invoke(); QueueRedraw();
                    return true;
                }
            case Key.Left: case Key.Right:
                _focusCol = Math.Clamp(_focusCol + (k.Keycode == Key.Left ? -1 : 1), 0, Columns.Count - 1); QueueRedraw(); return true;
            case Key.Enter: case Key.KpEnter: case Key.F2:
                if (_focusRow >= 0 && _focusCol >= 0 && _focusCol < Columns.Count && Columns[_focusCol].Editable && Columns[_focusCol].Kind != GridColKind.Bool) { StartEdit(_focusRow, _focusCol, null); return true; }
                return false;
        }
        // 숫자·부호를 누르면 바로 입력 시작
        if (k.Unicode != 0 && !k.CtrlPressed && !k.AltPressed && "0123456789.-+*/=".Contains((char)k.Unicode)
            && _focusRow >= 0 && _focusCol >= 0 && _focusCol < Columns.Count && Columns[_focusCol].Editable && Columns[_focusCol].Kind == GridColKind.Float)
        {
            StartEdit(_focusRow, _focusCol, ((char)k.Unicode).ToString());
            return true;
        }
        return false;
    }

    private void EnsureVisible(int r)
    {
        int vis = Math.Max(1, (int)(BodyH / RowH));
        if (r < _vbar.Value) _vbar.Value = r; else if (r >= _vbar.Value + vis) _vbar.Value = r - vis + 1;
    }

    // ---------------------------------------------------------------- 셀 편집

    private void StartEdit(int r, int c, string? initial)
    {
        EnsureVisible(r);
        var (x0, x1) = ColRange(c);
        if (c > 0 && x0 < Columns[0].Width * _s) { _hbar.Value -= Columns[0].Width * _s - x0; (x0, x1) = ColRange(c); }
        _editRow = r; _editCol = c;
        _editor.Position = new Vector2(x0, HeadH + (r - FirstRow) * RowH);
        _editor.Size = new Vector2(Math.Max(x1 - x0, 80 * _s), RowH);
        _editor.Text = initial ?? CellText(Columns[c], r);
        _editor.Visible = true;
        _editor.GrabFocus();
        if (initial != null) { _editor.Deselect(); _editor.CaretColumn = _editor.Text.Length; }
        else _editor.SelectAll();
    }

    private void FinishEdit(bool apply)
    {
        if (!_editor.Visible) return;
        _editor.Visible = false;
        int r = _editRow, c = _editCol;
        _editRow = _editCol = -1;
        GrabFocus();
        if (!apply || c < 0 || c >= Columns.Count || r < 0 || r >= RowCount) return;
        var col = Columns[c];
        if (col.Set == null || col.Value == null) return;
        var rows = (Selected.Contains(r) ? Selected.OrderBy(x => x) : new[] { r }.AsEnumerable()).ToArray();
        var vals = new double[rows.Length];
        string t = _editor.Text.Trim().Replace(',', '.');
        for (int i = 0; i < rows.Length; i++)
        {
            double cur = col.Value(rows[i]);
            if (!TryEval(t, cur, out vals[i])) return;
        }
        CommitEdit?.Invoke($"{col.Title} = {t}", () => col.Set(rows, vals));
        QueueRedraw();
    }

    /// <summary>"0.5", "+=0.1", "-=0.1", "*=2", "/=2"(현재 값 기준).</summary>
    public static bool TryEval(string t, double cur, out double v)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;
        v = cur;
        if (t.Length >= 3 && t[1] == '=' && "+-*/".Contains(t[0]))
        {
            if (!double.TryParse(t[2..], ns, inv, out double d)) return false;
            v = t[0] switch { '+' => cur + d, '-' => cur - d, '*' => cur * d, _ => d == 0 ? cur : cur / d };
            return true;
        }
        if (t.StartsWith('=')) t = t[1..];
        return double.TryParse(t, ns, inv, out v);
    }

    /// <summary>DebugDriver용: 행들을 선택하고 열 c에 문자열을 입력한 것처럼 적용한다.</summary>
    public void DriveEdit(int[] rows, int c, string text)
    {
        Selected.Clear(); foreach (int r in rows) Selected.Add(r);
        var col = Columns[c];
        if (col.Kind == GridColKind.Bool) { bool v = text is "1" or "true"; CommitEdit?.Invoke($"{col.Title} = {v}", () => col.SetBool!(rows, v)); return; }
        var vals = new double[rows.Length];
        for (int i = 0; i < rows.Length; i++) if (!TryEval(text, col.Value!(rows[i]), out vals[i])) return;
        CommitEdit?.Invoke($"{col.Title} = {text}", () => col.Set!(rows, vals));
    }

    /// <summary>DebugDriver용: 머리와 행 r의 셀 문자열.</summary>
    public string RowText(int r) => string.Join(" | ", Columns.Select(c => r < 0 ? c.Title : CellText(c, r)));

    /// <summary>선택 행(없으면 전체)을 머리 포함 TSV로 클립보드에 복사한다.</summary>
    public void CopySelection()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendJoin('\t', Columns.Select(c => c.Title)).Append('\n');
        IEnumerable<int> rows = Selected.Count > 0 ? Selected.OrderBy(x => x) : Enumerable.Range(0, RowCount);
        foreach (int r in rows) sb.AppendJoin('\t', Columns.Select(c => CellText(c, r))).Append('\n');
        DisplayServer.ClipboardSet(sb.ToString());
    }
}
