// 정점/면 중심 점. MultiMesh 쿼드를 화면 픽셀 크기로 펼친다 (D3D12는 포인트 크기를 지원하지 않음).
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, skip_vertex_transform;

uniform float point_px = 3.0;
uniform float bias = 0.003;

void vertex() {
	vec4 c = MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0);
	if (PROJECTION_MATRIX[3][3] != 0.0) {
		c.z += bias * max(abs(c.z), 1.0) + 0.002;
	} else {
		c.xyz *= (1.0 - bias);
	}
	vec4 clip = PROJECTION_MATRIX * c;
	clip.xy += VERTEX.xy * point_px * 2.0 / VIEWPORT_SIZE * clip.w;
	POSITION = clip;
}

void fragment() {
	ALBEDO = COLOR.rgb;
}
