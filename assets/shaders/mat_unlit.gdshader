// Unlit: 조명 없이 색 그대로. 뒷면은 검정.
shader_type spatial;
render_mode cull_disabled, unshaded;

uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_texture = false;

void fragment() {
	vec3 c = use_texture ? texture(albedo_tex, UV).rgb : albedo.rgb;
	ALBEDO = FRONT_FACING ? c : vec3(0.0);
}
