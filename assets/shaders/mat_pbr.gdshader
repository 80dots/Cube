// PBR(metallic/roughness). 뒷면은 검정.
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float roughness : hint_range(0.0, 1.0) = 0.5;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_texture = false;

void fragment() {
	if (!FRONT_FACING) { ALBEDO = vec3(0.0); ROUGHNESS = 1.0; METALLIC = 0.0; }
	else { ALBEDO = use_texture ? texture(albedo_tex, UV).rgb : albedo.rgb; METALLIC = metallic; ROUGHNESS = roughness; }
}
