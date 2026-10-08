namespace Cube.App.UI;

/// <summary>Edit → Component Editor(정점 데이터 표: 위치·노멀·UV 세트별 UV·조인트별 스킨 가중치).</summary>
public partial class Shell
{
    public ComponentEditor.ComponentEditorWindow? ComponentEditor { get; private set; }

    private void RegisterComponentEditorActions()
    {
        Actions.Register("edit.componentEditor", "Component Editor", () => TogglePanel(EnsureComponentEditor()), isChecked: () => ComponentEditor?.IsOpen ?? false);
    }

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
