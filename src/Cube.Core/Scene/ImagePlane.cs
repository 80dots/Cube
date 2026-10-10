namespace Cube.Core.Scene;

/// <summary>
/// 이미지 플레인(v0.0.71, Maya View → Image Plane → Import Image): 트레이싱용 참조 이미지를 띄우는 사각형 셰이프.
/// 노드 트랜스폼의 XY 평면에 Width×Height 크기로 놓이며(+Z가 앞), 뷰포트는 <c>ImagePlaneView</c>가 텍스처 쿼드로 그린다.
/// 내보내기(glTF/FBX/OBJ)에는 포함되지 않고 .cube에만 저장된다.
/// </summary>
public sealed class ImagePlaneShape : Shape
{
    /// <summary>이미지 파일 경로(절대 경로).</summary>
    public string ImagePath = "";
    /// <summary>월드 단위 너비·높이(기본은 이미지 비율에 맞춰 긴 변 2).</summary>
    public float Width = 2f, Height = 2f;
    /// <summary>불투명도 0..1.</summary>
    public float Opacity = 1f;
    /// <summary>이 뷰(카메라 라벨: front/side/top/persp…)에서만 보임. null = 모든 뷰.</summary>
    public string? OnlyView;
    /// <summary>잠금: 뷰포트에서 클릭/마키로 선택되지 않음(Outliner에서는 선택 가능).</summary>
    public bool Locked;

    /// <summary>모든 필드를 복사한 새 인스턴스(SetImagePlaneCommand 스냅샷·복제용).</summary>
    public ImagePlaneShape Clone() => new() { ImagePath = ImagePath, Width = Width, Height = Height, Opacity = Opacity, OnlyView = OnlyView, Locked = Locked };
}
