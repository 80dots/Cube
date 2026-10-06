// 정점/면 중심 점. MultiMesh 쿼드를 화면 픽셀 크기로 펼친다 (D3D12는 포인트 크기를 지원하지 않음).
// 깊이는 점의 월드 반지름만큼 카메라 쪽으로 당겨, 꼭짓점에 인접한 면에 가려지지 않게 한다.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, skip_vertex_transform;

uniform float point_px = 4.0;

void vertex() {
	vec4 c = MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0);
	bool ortho = PROJECTION_MATRIX[3][3] != 0.0;
	// 이 깊이에서 1픽셀의 월드 길이: P[1][1] = 1/tan(fov/2) (persp), 2/height (ortho)
	float wpp = ortho ? 2.0 / PROJECTION_MATRIX[1][1] / VIEWPORT_SIZE.y
	                  : 2.0 * abs(c.z) / PROJECTION_MATRIX[1][1] / VIEWPORT_SIZE.y;
	float pull = max(0.03 * abs(c.z), wpp * point_px * 3.0) + 0.002;
	if (ortho) {
		c.z += pull;
	} else {
		c.xyz *= max(1.0 - pull / max(abs(c.z), 1e-4), 0.0);
	}
	vec4 clip = PROJECTION_MATRIX * c;
	clip.xy += VERTEX.xy * point_px * 2.0 / VIEWPORT_SIZE * clip.w;
	POSITION = clip;
}

void fragment() {
	ALBEDO = COLOR.rgb;
}
