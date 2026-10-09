// 메시 표면. 양면 렌더링하며 뒷면(안쪽)은 검정으로 그려 뒤집힌 면을 바로 알 수 있게 한다.
shader_type spatial;
render_mode cull_disabled, diffuse_lambert, specular_disabled;

uniform vec4 albedo : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform bool use_texture = false;
uniform bool use_vertex_color = false;

void fragment() {
	// 음수 스케일(행렬식 < 0) 인스턴스는 화면 감기 방향이 뒤집혀 Godot의 FRONT_FACING과 양면 노멀 뒤집기가 반대로 나온다.
	// 거울 변환이면 앞/뒤 판정과 노멀을 되돌려 Maya처럼 정상 표시한다(전에는 Scale -1 오브젝트가 통째로 검게 보였다).
	bool mirrored = determinant(mat3(MODEL_MATRIX)) < 0.0;
	if (mirrored) NORMAL = -NORMAL;
	if (FRONT_FACING == mirrored) {
		ALBEDO = vec3(0.0);
		ROUGHNESS = 1.0;
		METALLIC = 0.0;
	} else {
		vec3 c = albedo.rgb;
		if (use_texture) c = texture(albedo_tex, UV).rgb;
		if (use_vertex_color) c = COLOR.rgb;
		ALBEDO = c;
		ROUGHNESS = 1.0;
		METALLIC = 0.0;
	}
}
