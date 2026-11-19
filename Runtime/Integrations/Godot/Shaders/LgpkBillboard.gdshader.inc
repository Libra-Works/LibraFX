R"LGPK(
uniform sampler2D particle_texture : source_color, filter_linear, repeat_disable;
uniform bool use_particle_texture = false;
uniform bool use_draw_matrix = false;
varying vec4 particle_color;

void vertex() {
    float s = sin(INSTANCE_CUSTOM.z);
    float c = cos(INSTANCE_CUSTOM.z);
    vec2 offset = mat2(vec2(c, s), vec2(-s, c)) * VERTEX.xy;
    vec3 center = (VIEW_MATRIX * MODEL_MATRIX[3]).xyz;
    vec3 quad = vec3(offset * INSTANCE_CUSTOM.xy, 0.0);
    if (use_draw_matrix) {
        quad = mat3(VIEW_MATRIX) * mat3(MODEL_MATRIX) * mat3(INV_VIEW_MATRIX) * quad;
    }
    VERTEX = center + quad;
    particle_color = COLOR;
}

void fragment() {
    vec4 texel = vec4(1.0);
    if (use_particle_texture) {
        texel = texture(particle_texture, UV);
    }
    vec4 tint = texel * particle_color;
    ALBEDO = tint.rgb;
    ALPHA = clamp(tint.a, 0.0, 1.0);
}
)LGPK"
