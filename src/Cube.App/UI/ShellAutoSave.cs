using Cube.Core.IO;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// 자동 저장(v0.0.67, Maya Preferences → Files/Projects → AutoSave). 기본 ON, 주기(분)·보관 개수·폴더는 Edit → Preferences의 Auto Save 그룹.
/// 주기가 지나면 문서가 마지막 자동 저장 이후 바뀌었을 때만 <c>&lt;폴더&gt;/&lt;씬 이름&gt;.autosave.&lt;yyyyMMdd-HHmmss&gt;.cube</c>로 쓴다
/// (문서의 FilePath·Dirty는 건드리지 않음 — 실제 저장이 아니라 백업). 씬 이름별로 보관 개수를 넘는 오래된 파일은 지운다.
/// 헬프 라인 오른쪽 버튼이 다음 자동 저장까지 남은 초를 보여 주고, 누르면 바로 저장한다. 수동 저장(Ctrl+S) 뒤에는 타이머가 처음부터 다시 센다.
/// 기본 폴더는 <c>user://autosave</c>(File → Open Auto Save Folder).
/// </summary>
public partial class Shell
{
    private Button _autoSaveButton = null!;
    /// <summary>다음 자동 저장까지 남은 초.</summary>
    private double _autoSaveRemaining = -1;
    /// <summary>마지막 자동 저장(또는 수동 저장) 시점의 문서 변경 일련번호.</summary>
    private int _autoSavedSerial = -1;
    private bool _autoWasDirty;
    private int _autoSaveShownSeconds = int.MinValue;
    private bool _autoSaveShownOn;

    /// <summary>다음 자동 저장까지 남은 초(DebugDriver print용).</summary>
    public double AutoSaveRemaining => _autoSaveRemaining;

    /// <summary>자동 저장 폴더(절대 경로). 설정이 비어 있으면 user://autosave.</summary>
    public string AutoSaveFolder
    {
        get
        {
            var f = Settings.AutoSaveFolder;
            return string.IsNullOrWhiteSpace(f) ? ProjectSettings.GlobalizePath("user://autosave") : f;
        }
    }

    /// <summary>주기(초). 설정은 분 단위(최소 0.05분 = 3초).</summary>
    private double AutoSaveIntervalSeconds => Math.Max(Settings.AutoSaveIntervalMinutes, 0.05f) * 60.0;

    /// <summary>헬프 라인 오른쪽의 남은 시간 버튼(누르면 지금 저장).</summary>
    private void BuildAutoSaveStatus(HBoxContainer row, float s)
    {
        _autoSaveButton = new Button { Name = "AutoSaveButton", Text = "Auto Save: --", Flat = true, FocusMode = FocusModeEnum.None,
            TooltipText = "Seconds until the next auto save (Edit → Preferences → Auto Save). Click to auto save now." };
        _autoSaveButton.AddThemeFontSizeOverride("font_size", (int)(12 * s));
        _autoSaveButton.Pressed += () => Actions.Invoke("file.autoSaveNow");
        row.AddChild(_autoSaveButton);
        ResetAutoSaveTimer();
    }

    /// <summary>타이머를 주기 처음으로 되돌린다(설정 변경·수동 저장·자동 저장 직후).</summary>
    public void ResetAutoSaveTimer()
    {
        _autoSaveRemaining = AutoSaveIntervalSeconds;
        _autoSavedSerial = Document.ChangeSerial;
        _autoWasDirty = Document.IsDirty;
        RefreshAutoSaveLabel();
    }

    /// <summary>매 프레임: 남은 시간을 줄이고 0이 되면 (바뀐 것이 있을 때만) 자동 저장. 수동 저장으로 Dirty가 풀리면 타이머를 다시 센다.</summary>
    private void UpdateAutoSave(double delta)
    {
        if (_autoSaveButton == null) return;
        bool dirty = Document.IsDirty;
        if (_autoWasDirty && !dirty) { ResetAutoSaveTimer(); return; }
        _autoWasDirty = dirty;
        if (!Settings.AutoSave) { RefreshAutoSaveLabel(); return; }
        _autoSaveRemaining -= delta;
        if (_autoSaveRemaining <= 0)
        {
            if (dirty && Document.ChangeSerial != _autoSavedSerial) AutoSaveNow(quiet: false);
            _autoSaveRemaining = AutoSaveIntervalSeconds;
            _autoSavedSerial = Document.ChangeSerial;
        }
        RefreshAutoSaveLabel();
    }

    /// <summary>버튼 텍스트를 남은 초(또는 Off)로 갱신한다(값이 바뀔 때만).</summary>
    private void RefreshAutoSaveLabel()
    {
        bool on = Settings.AutoSave;
        int secs = on ? (int)Math.Ceiling(Math.Max(_autoSaveRemaining, 0)) : -1;
        if (secs == _autoSaveShownSeconds && on == _autoSaveShownOn) return;
        _autoSaveShownSeconds = secs; _autoSaveShownOn = on;
        _autoSaveButton.Text = on ? $"Auto Save: {secs}s" : "Auto Save: Off";
        if (on) _autoSaveButton.RemoveThemeColorOverride("font_color"); else _autoSaveButton.AddThemeColorOverride("font_color", MayaTheme.TextDim);
    }

    /// <summary>지금 자동 저장 파일을 쓴다. 성공하면 경로, 실패하면 null(헬프 라인·로그에 기록).</summary>
    public string? AutoSaveNow(bool quiet = false)
    {
        try
        {
            string folder = AutoSaveFolder;
            System.IO.Directory.CreateDirectory(folder);
            string scene = Document.FilePath != null ? System.IO.Path.GetFileNameWithoutExtension(Document.FilePath) : "untitled";
            string path = System.IO.Path.Combine(folder, $"{scene}.autosave.{DateTime.Now:yyyyMMdd-HHmmss}{CubeFileFormat.Extension}");
            // 실제 저장이 아니므로 FilePath/IsDirty는 그대로 두고 직렬화 결과만 쓴다(Serialize는 rest 포즈로 기록).
            System.IO.File.WriteAllText(path, CubeFileFormat.Serialize(Document), new System.Text.UTF8Encoding(false));
            PruneAutoSaves(folder, scene);
            _autoSavedSerial = Document.ChangeSerial;
            _autoSaveRemaining = AutoSaveIntervalSeconds;
            LogCapture.Write(LogLevel.Status, $"Auto saved: {path}");
            if (!quiet) HelpLine.Text = $"Auto saved: {path}";
            return path;
        }
        catch (Exception ex)
        {
            LogCapture.Write(LogLevel.Error, $"Auto save failed: {ex.Message}");
            HelpLine.Text = $"Auto save failed: {ex.Message}";
            return null;
        }
    }

    /// <summary>씬 이름별 자동 저장 파일을 보관 개수만큼만 남기고 오래된 것부터 지운다.</summary>
    private void PruneAutoSaves(string folder, string scene)
    {
        int limit = Math.Max(Settings.AutoSaveLimit, 1);
        var files = System.IO.Directory.GetFiles(folder, $"{scene}.autosave.*{CubeFileFormat.Extension}").OrderBy(f => f, StringComparer.Ordinal).ToList();
        while (files.Count > limit) { try { System.IO.File.Delete(files[0]); } catch { } files.RemoveAt(0); }
    }

    /// <summary>file.autoSaveToggle(켜기/끄기), file.autoSaveNow(지금 저장), file.openAutoSaveFolder(탐색기로 열기).</summary>
    private void RegisterAutoSaveActions()
    {
        Actions.Register("file.autoSaveToggle", "Auto Save", () => { Settings.AutoSave = !Settings.AutoSave; Settings.Save(); ResetAutoSaveTimer(); HelpLine.Text = Settings.AutoSave ? $"Auto Save on: every {Settings.AutoSaveIntervalMinutes:0.##} min." : "Auto Save off."; }, isChecked: () => Settings.AutoSave);
        Actions.Register("file.autoSaveNow", "Auto Save Now", () => AutoSaveNow());
        Actions.Register("file.openAutoSaveFolder", "Open Auto Save Folder", () => { System.IO.Directory.CreateDirectory(AutoSaveFolder); OS.ShellOpen(AutoSaveFolder); });
    }
}
