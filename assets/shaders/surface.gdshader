// 메시 표면. 양면 렌더링하며 뒷면(안쪽)은 검정으로 그려 뒤집힌 면을 바로 알 수 있게 한다.
shader_type spatial;
render_mode cull_disabled, diffuse_lambert, specular_disabled;

uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_texture = false;

void fragment() {
	if (!FRONT_FACING) {
		ALBEDO = vec3(0.0);
		ROUGHNESS = 1.0;
		METALLIC = 0.0;
	} else {
		vec3 c = albedo.rgb;
		if (use_texture) c = texture(albedo_tex, UV).rgb;
		ALBEDO = c;
		ROUGHNESS = 1.0;
		METALLIC = 0.0;
	}
}
