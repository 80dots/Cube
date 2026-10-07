// Blinn-Phong 근사: Godot 4의 schlick-ggx 스펙큘러에 광택(shininess)을 거칠기로 바꿔 넣는다. 뒷면은 검정.
shader_type spatial;
render_mode cull_disabled, diffuse_lambert, specular_schlick_ggx;

uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform vec4 specular_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform float shininess : hint_range(1.0, 128.0) = 32.0;

void fragment() {
	if (!FRONT_FACING) { ALBEDO = vec3(0.0); ROUGHNESS = 1.0; METALLIC = 0.0; SPECULAR = 0.0; }
	else {
		ALBEDO = albedo.rgb;
		float r = clamp(sqrt(2.0 / (shininess + 2.0)), 0.05, 1.0);
		ROUGHNESS = r;
		METALLIC = 0.0;
		SPECULAR = clamp(dot(specular_color.rgb, vec3(0.333)), 0.0, 1.0);
	}
}
