// 와이어프레임 선. 뷰 공간에서 카메라 쪽으로 살짝 당겨 z-fighting을 없앤다.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, skip_vertex_transform, vertex_lighting;

uniform float bias = 0.002;

void vertex() {
	vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	if (PROJECTION_MATRIX[3][3] != 0.0) {
		// 직교: 시선축(-Z)을 따라 카메라 쪽으로
		v.z += bias * max(abs(v.z), 1.0) + 0.001;
	} else {
		// 원근: 시선 광선을 따라 → 화면 위치 보존
		v.xyz *= (1.0 - bias);
	}
	VERTEX = v.xyz;
	NORMAL = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	ALBEDO = COLOR.rgb;
}
