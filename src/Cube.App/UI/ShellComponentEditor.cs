namespace Cube.App.UI;

/// <summary>Edit → Component Editor(정점 데이터 표: 위치·노멀·UV 세트별 UV·조인트별 스킨 가중치).</summary>
public partial class Shell
{
    /// <summary>Component Editor 패널(처음 열 때 생성). 선택 정점의 데이터를 표로 보여 주고 편집한다.</summary>
    public ComponentEditor.ComponentEditorWindow? ComponentEditor { get; private set; }

    /// <summary>edit.componentEditor 액션(패널 열기/닫기 토글, 체크 = 열림)을 등록한다.</summary>
    private void RegisterComponentEditorActions()
    {
        Actions.Register("edit.componentEditor", "Component Editor", () => TogglePanel(EnsureComponentEditor()), isChecked: () => ComponentEditor?.IsOpen ?? false);
    }

    /// <summary>
    /// Component Editor 패널을 지연 생성한다(숨김 상태로 추가 → Setup → 닫힐 때 셸프 갱신 → DockManager 등록).
    /// 레이아웃 복원(EnsurePanel "componentEditor")에서도 호출된다.
    /// </summary>
    private ComponentEditor.ComponentEditorWindow EnsureComponentEditor()
    {
        if (ComponentEditor == null)
        {
            ComponentEditor = new ComponentEditor.ComponentEditorWindow { Name = "ComponentEditor", Visible = false, PanelId = "componentEditor" };
            AddChild(ComponentEditor);
            ComponentEditor.Setup(this);
            ComponentEditor.Closed += RefreshShelf;
            Dock.Register(ComponentEditor);
        }
        return ComponentEditor;
    }
}
