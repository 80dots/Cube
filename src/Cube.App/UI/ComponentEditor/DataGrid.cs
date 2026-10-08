using Godot;

namespace Cube.App.UI.ComponentEditor;

/// <summary>표 열의 종류. 그리기 정렬·편집 방식이 달라진다.</summary>
public enum GridColKind { Text, Float, Bool }

/// <remarks>
/// 표는 데이터를 직접 갖지 않고 열마다 "행 번호 → 값" 접근 함수(Text/Value/Bool)와 쓰기 함수(Set/SetBool)를 받는다.
/// 행 번호 → 실제 컴포넌트 ID 매핑은 호출자(ComponentEditorWindow)가 담당한다.
/// </remarks>
/// <summary>표의 열 하나. 값 접근은 행 번호(표 안의 순서)로 한다.</summary>
public sealed class GridColumn
{
    /// <summary>열 머리 제목(복사 TSV 머리에도 쓰인다).</summary>
    public string Title = "";
    /// <summary>열 폭(배율 적용 전 px). 머리 경계를 끌어 바꿀 수 있고 최소 30.</summary>
    public float Width = 70;
    /// <summary>열 종류(기본 Float).</summary>
    public GridColKind Kind = GridColKind.Float;
    /// <summary>표시 문자열(Text 열) — Float 열은 Value를 서식화한다.</summary>
    public Func<int, string>? Text;
    /// <summary>Float 열의 값 읽기(행 번호 → 값). 셀 텍스트는 이 값을 <see cref="Format"/>으로 서식화한다.</summary>
    public Func<int, double>? Value;
    /// <summary>값 쓰기(행들, 값들). null이면 읽기 전용.</summary>
    public Action<int[], double[]>? Set;
    /// <summary>Bool 열의 값 읽기(행 번호 → 체크 여부).</summary>
    public Func<int, bool>? Bool;
    /// <summary>Bool 열의 값 쓰기(행들, 새 값). null이면 읽기 전용.</summary>
    public Action<int[], bool>? SetBool;
    /// <summary>가운데 버튼 드래그 1px당 변화량.</summary>
    public double Step = 0.01;
    /// <summary>Float 값 표시 서식(.NET 숫자 서식 문자열).</summary>
    public string Format = "0.0000";
    /// <summary>열 강조 색(머리 글자색, 셀 배경에 12% 섞음). 값이 0인 셀은 흐리게 표시된다(가중치 열 등).</summary>
    public Color? Tint;
    /// <summary>열 머리 툴팁(null이면 제목).</summary>
    public string? Tip;
    /// <summary>편집 가능 여부: Bool 열은 SetBool, 그 외는 Set이 있어야 한다.</summary>
    public bool Editable => Kind == GridColKind.Bool ? SetBool != null : Set != null;
}

/// <summary>
/// 보이는 행만 그리는 가상 스크롤 표(Maya Component Editor 스타일). 행 수만 개에서도 그리기·스크롤 비용은 화면에 보이는 행 수에 비례한다.
/// 선택: 클릭/Shift 범위/Ctrl 토글/드래그. 편집: 더블클릭·Enter·F2·숫자 입력 → 입력 칸(선택 행 전체에 적용, "+=0.1" "-=" "*=" "/=" 상대값),
/// Bool 셀 클릭 = 토글, 가운데 버튼 좌우 드래그 = 값 조절(Shift ×0.1). Ctrl+C = 선택 행 TSV 복사, Ctrl+A = 전체 선택.
/// 첫 번째 열은 가로 스크롤해도 고정된다. 열 머리 경계를 끌면 폭 조절.
/// 구현 메모: 자식 컨트롤은 스크롤바 2개와 셀 편집용 LineEdit 하나뿐이고 셀은 모두 _Draw에서 직접 그린다.
/// 세로 스크롤 값 = 첫 보이는 행 번호(정수 행 단위), 가로 스크롤 값 = 고정 열(0번) 오른쪽 영역의 px 오프셋.
/// </summary>
public partial class DataGrid : Control
{
    /// <summary>열 정의 목록(0번 = 고정 열). 호출자가 채운 뒤 <see cref="SetRows"/>로 레이아웃을 갱신한다.</summary>
    public readonly List<GridColumn> Columns = new();
    /// <summary>행 개수. <see cref="SetRows"/>로만 바꾼다.</summary>
    public int RowCount { get; private set; }
    /// <summary>선택된 행 번호 집합.</summary>
    public readonly HashSet<int> Selected = new();
    /// <summary>사용자 조작으로 선택이 바뀌었을 때 발생(프로그램이 Selected를 직접 바꾸면 발생하지 않음).</summary>
    public event Action? SelectionChanged;

    /// <summary>즉시 편집: (이름, 변경 함수). 호출자가 Undo 명령으로 감싼다.</summary>
    public Action<string, Action>? CommitEdit;
    /// <summary>드래그 편집: 시작(이름) → 미리보기(변경 함수, 여러 번) → 끝(확정 여부).</summary>
    public Action<string>? BeginDrag;
    /// <summary>드래그 편집 미리보기: 받은 변경 함수를 적용해 문서를 갱신한다(Undo 기록 없이).</summary>
    public Action<Action>? PreviewDrag;
    /// <summary>드래그 편집 끝: true = 실제로 움직였으니 확정(Undo 기록), false = 취소/변화 없음.</summary>
    public Action<bool>? EndDrag;
    /// <summary>행 머리 색(선택된 컴포넌트 강조 등). null = 기본.</summary>
    public Func<int, Color?>? RowTint;

    /// <summary>세로 스크롤바(값 = 첫 보이는 행).</summary>
    private VScrollBar _vbar = null!;
    /// <summary>가로 스크롤바(값 = 고정 열 오른쪽 영역의 px 오프셋).</summary>
    private HScrollBar _hbar = null!;
    /// <summary>셀 편집용 입력 칸. 편집 중에만 보이며 셀 위에 겹쳐 놓인다.</summary>
    private LineEdit _editor = null!;
    /// <summary>UI 배율(_Ready에서 읽음). 모든 px 상수에 곱한다.</summary>
    private float _s = 1f;
    /// <summary>행 높이(px).</summary>
    private float RowH => 22 * _s;
    /// <summary>열 머리 높이(px).</summary>
    private float HeadH => 24 * _s;
    /// <summary>글꼴 크기(px).</summary>
    private int FontSize => (int)(12 * _s);
    /// <summary>_anchor: Shift 범위 선택의 기준 행, _focusRow/_focusCol: 키보드 포커스 셀(흰 테두리; 기본 열 1).</summary>
    private int _anchor = -1, _focusRow = -1, _focusCol = 1;
    /// <summary>좌버튼 드래그로 범위 선택 중인지.</summary>
    private bool _dragSelect;
    /// <summary>열 폭 조절 상태: 조절 중인 열(-1 = 없음), 시작 마우스 X, 시작 폭.</summary>
    private int _resizeCol = -1; private float _resizeStartX, _resizeStartW;
    // 가운데 버튼 값 드래그
    // _mdCol = 드래그 중인 열(-1 = 없음), _mdRows/_mdOrig = 대상 행과 시작 값, _mdStartX = 시작 X, _mdMoved = 1px 이상 움직였는지.
    private int _mdCol = -1; private int[] _mdRows = Array.Empty<int>(); private double[] _mdOrig = Array.Empty<double>(); private float _mdStartX; private bool _mdMoved;
    /// <summary>편집 중인 셀(행, 열). 편집 중이 아니면 -1.</summary>
    private int _editRow = -1, _editCol = -1;

    /// <summary>
    /// 초기화: 배율을 읽고 포커스/클리핑을 설정하며 스크롤바·편집 칸을 자식으로 만든다.
    /// 편집 칸은 Enter = 확정, 포커스 잃음 = 확정, Esc = 취소. 크기가 바뀌면 <see cref="Layout"/>.
    /// </summary>
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

    /// <summary>행 수를 바꾼다. 범위를 벗어난 선택·포커스를 정리하고 스크롤 범위를 다시 계산한 뒤 다시 그린다.</summary>
    /// <param name="count">새 행 수.</param>
    public void SetRows(int count)
    {
        RowCount = count;
        Selected.RemoveWhere(r => r >= count);
        if (_focusRow >= count) _focusRow = count - 1;
        Layout();
        QueueRedraw();
    }

    /// <summary>선택·기준 행·포커스 행을 모두 지운다(SelectionChanged는 발생하지 않음).</summary>
    public void ClearSelection() { Selected.Clear(); _anchor = _focusRow = -1; QueueRedraw(); }

    /// <summary>모든 열 폭의 합(px).</summary>
    private float TotalWidth => Columns.Sum(c => c.Width * _s);
    /// <summary>행 영역 높이(머리와 가로 스크롤바 제외, px).</summary>
    private float BodyH => Math.Max(0, Size.Y - HeadH - (_hbar.Visible ? _hbar.Size.Y : 0));
    /// <summary>행 영역 폭(세로 스크롤바 제외, px).</summary>
    private float BodyW => Math.Max(0, Size.X - (_vbar.Visible ? _vbar.Size.X : 0));
    /// <summary>첫 보이는 행 번호(= 세로 스크롤 값).</summary>
    private int FirstRow => (int)_vbar.Value;

    /// <summary>
    /// 스크롤바 배치와 범위를 계산한다. 가로 스크롤바는 전체 폭이 넘칠 때, 세로 스크롤바는 행들이 넘칠 때만 보인다.
    /// 세로 범위 = 행 단위(Page = 보이는 행 수), 가로 범위 = 고정 열을 뺀 폭. 스크롤 값이 범위를 넘으면 끌어내린다.
    /// </summary>
    private void Layout()
    {
        // _Ready 전에 Resized 등으로 불리면 무시.
        if (_vbar == null) return;
        float sb = 12 * _s;
        // 가로 바를 먼저 정해야 세로 바 판단에 그 높이를 반영할 수 있다.
        _hbar.Visible = TotalWidth > Size.X - sb;
        _vbar.Visible = RowCount * RowH > Size.Y - HeadH - (_hbar.Visible ? sb : 0);
        _vbar.Position = new Vector2(Size.X - sb, HeadH); _vbar.Size = new Vector2(sb, Math.Max(0, Size.Y - HeadH - (_hbar.Visible ? sb : 0)));
        _hbar.Position = new Vector2(0, Size.Y - sb); _hbar.Size = new Vector2(Math.Max(0, Size.X - (_vbar.Visible ? sb : 0)), sb);
        int visRows = Math.Max(1, (int)(BodyH / RowH));
        _vbar.MaxValue = RowCount; _vbar.Page = visRows; _vbar.Step = 1;
        if (_vbar.Value > Math.Max(0, RowCount - visRows)) _vbar.Value = Math.Max(0, RowCount - visRows);
        // 고정 열은 가로 스크롤 대상이 아니다.
        float fixedW = Columns.Count > 0 ? Columns[0].Width * _s : 0;
        _hbar.MaxValue = Math.Max(0, TotalWidth - fixedW); _hbar.Page = Math.Max(1, BodyW - fixedW);
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 좌표

    /// <summary>열의 화면 x 범위(고정 열 = 0번).</summary>
    private (float x0, float x1) ColRange(int c)
    {
        // 고정 열은 항상 왼쪽 끝, 나머지는 고정 열 오른쪽에서 가로 스크롤만큼 왼쪽으로 밀린다.
        if (c == 0) return (0, Columns[0].Width * _s);
        float x = Columns[0].Width * _s - (float)_hbar.Value;
        for (int i = 1; i < c; i++) x += Columns[i].Width * _s;
        return (x, x + Columns[c].Width * _s);
    }

    /// <summary>로컬 X(px)에 있는 열 번호. 고정 열 위면 0, 아무 열도 아니면 -1.</summary>
    private int ColAt(float x)
    {
        if (Columns.Count == 0) return -1;
        if (x < Columns[0].Width * _s) return 0;
        for (int c = 1; c < Columns.Count; c++) { var (a, b) = ColRange(c); if (x >= a && x < b) return c; }
        return -1;
    }

    /// <summary>로컬 Y(px)에 있는 행 번호. 머리 위면 -1(행 수를 넘을 수 있으므로 호출자가 범위를 검사한다).</summary>
    private int RowAt(float y) => y < HeadH ? -1 : FirstRow + (int)((y - HeadH) / RowH);

    // ---------------------------------------------------------------- 그리기

    /// <summary>
    /// 보이는 행·열만 그린다. 스크롤 열을 먼저 그린 뒤 고정 열을 그 위에 덮어 가로 스크롤 시 고정 열이 가려지지 않게 한다.
    /// 셀 배경: 선택 = 파랑, 아니면 줄무늬; 고정 열은 RowTint(행 강조) 또는 어두운 패널색; 강조 열은 Tint를 12% 섞음.
    /// 편집 불가 열은 흐린 글자, 강조 열의 0 값도 흐리게. 정렬: Text 왼쪽, Bool 가운데, Float 오른쪽.
    /// </summary>
    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        int fs = FontSize;
        DrawRect(new Rect2(Vector2.Zero, Size), MayaTheme.Field);
        if (Columns.Count == 0) return;
        // 보이는 행 범위(마지막 부분 행 포함).
        int first = FirstRow, vis = (int)(BodyH / RowH) + 1;
        float fixedW = Columns[0].Width * _s;
        // 행 높이 안에서 글자를 세로 가운데 정렬하는 기준선 Y.
        float textY(float top) => top + (RowH + font.GetAscent(fs) - font.GetDescent(fs)) / 2;
        for (int pass = 0; pass < 2; pass++) // 0 = 스크롤 열, 1 = 고정 열(위에 덮음)
        {
            for (int c = pass == 0 ? 1 : 0; c < (pass == 0 ? Columns.Count : 1); c++)
            {
                var (x0, x1) = ColRange(c);
                // 화면 밖이거나 고정 열 아래로 완전히 숨은 스크롤 열은 건너뛴다.
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
                    // 키보드 포커스 셀은 흰 테두리.
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
        // 행이 없으면 안내 문구.
        if (RowCount == 0) DrawString(font, new Vector2(8 * _s, HeadH + RowH), "(no rows)", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
    }

    /// <summary>셀 표시 문자열: Bool = ■/□, Float = Value 서식화(Value가 없으면 Text), Text = Text 함수 결과.</summary>
    private static string CellText(GridColumn col, int r) => col.Kind switch
    {
        GridColKind.Bool => col.Bool?.Invoke(r) == true ? "■" : "□",
        GridColKind.Float => col.Value != null ? col.Value(r).ToString(col.Format) : col.Text?.Invoke(r) ?? "",
        _ => col.Text?.Invoke(r) ?? "",
    };

    /// <summary>열 머리 위에서만 툴팁(열 Tip, 없으면 제목)을 돌려준다.</summary>
    public override string _GetTooltip(Vector2 atPosition)
    {
        if (atPosition.Y >= HeadH) return "";
        int c = ColAt(atPosition.X);
        return c >= 0 ? Columns[c].Tip ?? Columns[c].Title : "";
    }

    // ---------------------------------------------------------------- 입력

    /// <summary>
    /// 입력 분배: 휠 = 세로 3행 스크롤(Shift = 가로 40px), 좌버튼 = 선택/편집/열 폭, 가운데 버튼 = 값 드래그, 이동 = 드래그 처리/커서, 키 = 탐색·편집 단축키.
    /// </summary>
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

    /// <summary>
    /// 좌버튼 처리. 뗌 = 드래그 선택·열 폭 조절 종료. 누름: 머리 = 열 경계 근처(±5px)면 폭 조절 시작,
    /// 셀 더블클릭 = 편집 시작, 편집 가능한 Bool 셀 단일 클릭 = 선택 행 전체 토글 커밋,
    /// Shift = 기준 행부터 범위 선택(Ctrl과 함께면 추가), Ctrl = 행 토글, 그 외 = 단일 선택 후 드래그 선택 시작.
    /// </summary>
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
        // Bool 셀: 클릭한 행의 반대 값을 선택 행 모두에 적용(한 Undo 항목).
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
        // 일반 행 선택 규칙(Maya/탐색기와 같음).
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

    /// <summary>행 하나만 선택하고 기준 행으로 삼은 뒤 SelectionChanged를 발생시킨다.</summary>
    private void SelectOnly(int r) { Selected.Clear(); Selected.Add(r); _anchor = r; SelectionChanged?.Invoke(); }

    /// <summary>
    /// 가운데 버튼 값 드래그. 누름: 편집 가능한 Float 셀이면(선택 밖이면 그 행만 선택) 대상 행과 시작 값을 기억하고 BeginDrag.
    /// 뗌: EndDrag(움직였는지)로 확정/취소를 호출자에게 알린다.
    /// </summary>
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

    /// <summary>
    /// 마우스 이동 처리(우선순위): 열 폭 조절 → 가운데 버튼 값 드래그(시작 값 + 이동 px × Step, Shift ×0.1을 PreviewDrag로 미리보기)
    /// → 좌버튼 드래그 범위 선택(위/아래 가장자리에서 자동 스크롤) → 머리 경계 위면 좌우 크기 조절 커서.
    /// </summary>
    private void OnMotion(InputEventMouseMotion mo)
    {
        if (_resizeCol >= 0)
        {
            // 폭은 배율 전 단위로 저장하므로 마우스 이동량을 배율로 나눈다.
            Columns[_resizeCol].Width = Math.Max(30, _resizeStartW + (mo.Position.X - _resizeStartX) / _s);
            Layout();
            return;
        }
        if (_mdCol >= 0)
        {
            var col = Columns[_mdCol];
            // 절대 이동량 기반이라 미리보기를 반복해도 오차가 쌓이지 않는다(항상 시작 값에서 계산).
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
            // 머리 위로 올라가도 첫 보이는 행으로 고정.
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
        // 버튼 없이 이동: 열 경계 위 커서 모양.
        if (mo.Position.Y < HeadH)
        {
            bool edge = false;
            for (int c = 0; c < Columns.Count && !edge; c++) edge = Math.Abs(mo.Position.X - ColRange(c).x1) < 5 * _s;
            MouseDefaultCursorShape = edge ? CursorShape.Hsize : CursorShape.Arrow;
        }
        else MouseDefaultCursorShape = CursorShape.Arrow;
    }

    /// <summary>
    /// 키 처리(편집 칸이 열려 있으면 처리하지 않음). Ctrl+C 복사, Ctrl+A 전체 선택, Ctrl+Z/Y(Shift+Ctrl+Z) = 셸 Undo/Redo 액션,
    /// 위/아래 = 포커스 행 이동(Shift 범위), 좌/우 = 포커스 열 이동, Enter/F2 = 편집 시작, 숫자·부호 = 그 글자로 편집 시작.
    /// </summary>
    /// <returns>처리했으면 true(호출자가 이벤트를 소비).</returns>
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

    /// <summary>행 r이 보이도록 세로 스크롤 값을 최소한으로 조정한다.</summary>
    private void EnsureVisible(int r)
    {
        int vis = Math.Max(1, (int)(BodyH / RowH));
        if (r < _vbar.Value) _vbar.Value = r; else if (r >= _vbar.Value + vis) _vbar.Value = r - vis + 1;
    }

    // ---------------------------------------------------------------- 셀 편집

    /// <summary>
    /// 셀 편집 시작: 행이 보이게 스크롤하고, 열이 고정 열 아래로 숨어 있으면 가로 스크롤을 당긴 뒤 편집 칸을 셀 위치·크기(최소 80px)로 놓는다.
    /// </summary>
    /// <param name="r">행.</param>
    /// <param name="c">열.</param>
    /// <param name="initial">처음 입력된 글자(숫자 키로 시작한 경우) — 있으면 그 글자로 시작하고 커서를 끝에, 없으면 현재 값을 전체 선택.</param>
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

    /// <summary>
    /// 셀 편집 끝. apply가 true면 입력을 해석해 편집 셀이 선택에 포함될 때 선택 행 전체에(아니면 그 행만) 적용한다.
    /// 상대식("+=0.1" 등)은 행마다 자기 현재 값 기준이다. 하나라도 해석에 실패하면 아무것도 적용하지 않는다. 쉼표는 소수점으로 바꾼다.
    /// </summary>
    /// <param name="apply">true = 확정, false = 취소(Esc).</param>
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
    /// <param name="t">입력 문자열(앞뒤 공백 제거된 상태).</param>
    /// <param name="cur">현재 값(상대 연산의 기준).</param>
    /// <param name="v">결과 값(실패 시 의미 없음).</param>
    /// <returns>해석 성공 여부.</returns>
    public static bool TryEval(string t, double cur, out double v)
    {
        // 현재 문화권과 무관하게 '.' 소수점으로 해석한다.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;
        v = cur;
        // "op=숫자" 형식의 상대 연산. 0으로 나누기는 현재 값 유지.
        if (t.Length >= 3 && t[1] == '=' && "+-*/".Contains(t[0]))
        {
            if (!double.TryParse(t[2..], ns, inv, out double d)) return false;
            v = t[0] switch { '+' => cur + d, '-' => cur - d, '*' => cur * d, _ => d == 0 ? cur : cur / d };
            return true;
        }
        // "=0.5"처럼 앞에 '='가 붙은 절대값도 허용.
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
