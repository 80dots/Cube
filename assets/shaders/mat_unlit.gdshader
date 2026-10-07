// Unlit: 조명 없이 색 그대로. 뒷면은 검정.
shader_type spatial;
render_mode cull_disabled, unshaded;

uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);

void fragment() {
	ALBEDO = FRONT_FACING ? albedo.rgb : vec3(0.0);
}
