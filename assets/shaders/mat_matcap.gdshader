// Matcap: 뷰 공간 노멀로 구 텍스처를 샘플링하고 albedo를 곱한다. 뒷면은 검정.
shader_type spatial;
render_mode cull_disabled, unshaded;

uniform vec4 albedo : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform sampler2D matcap : source_color, filter_linear;

void fragment() {
	if (!FRONT_FACING) { ALBEDO = vec3(0.0); }
	else {
		vec3 n = normalize(NORMAL); // 뷰 공간
		vec2 uv = n.xy * 0.5 + 0.5;
		uv.y = 1.0 - uv.y;
		ALBEDO = texture(matcap, uv).rgb * albedo.rgb;
	}
}
