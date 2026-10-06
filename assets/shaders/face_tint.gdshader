// 선택된 면 반투명 틴트.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, skip_vertex_transform;

uniform vec4 tint : source_color = vec4(1.0, 0.55, 0.0, 0.35);
uniform float bias = 0.001;

void vertex() {
	vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	if (PROJECTION_MATRIX[3][3] != 0.0) {
		v.z += bias * max(abs(v.z), 1.0) + 0.0005;
	} else {
		v.xyz *= (1.0 - bias);
	}
	VERTEX = v.xyz;
	NORMAL = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	ALBEDO = tint.rgb;
	ALPHA = tint.a;
}
