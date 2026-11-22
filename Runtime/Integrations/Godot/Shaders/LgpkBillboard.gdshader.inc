R"LGPK(
uniform sampler2D particle_texture : source_color, filter_linear, repeat_disable;
uniform bool use_particle_texture = false;
uniform bool texture_premultiplied = false;
uniform bool texture_flip_y = false;
uniform bool use_draw_matrix = false;
uniform bool draw_is_world = false;
uniform sampler2D particle_state : filter_nearest, repeat_disable;
uniform mat4 draw_matrix = mat4(1.0);
uniform float particle_size_scale = 1.0;
varying vec4 particle_color;

void vertex() {
    ivec2 base = ivec2((INSTANCE_ID % 64) * 5, INSTANCE_ID / 64);
    vec4 position_age = texelFetch(particle_state, base, 0);
    vec4 size_rotation = texelFetch(particle_state, base + ivec2(3,0), 0);
    vec4 seed_alive = texelFetch(particle_state, base + ivec2(4,0), 0);
    vec4 sprite_uv=vec4(0.0,0.0,1.0,1.0);vec2 sprite_offset=vec2(0.0),sprite_size=vec2(1.0);float sprite_angle=0.0;
    if(sprite_enabled){sprite_sample(int(UV2.x),position_age.w,sprite_uv,sprite_offset,sprite_size,sprite_angle);}
    UV=sprite_uv.xy+UV*sprite_uv.zw;
    float cs=cos(sprite_angle),ss=sin(sprite_angle);
    vec2 corner=VERTEX.xy*(sprite_enabled ? sprite_size*size_rotation.xy : vec2(1.0));
    corner=mat2(vec2(cs,ss),vec2(-ss,cs))*corner+2.0*sprite_offset*size_rotation.xy;
    float s = sin(size_rotation.z);
    float c = cos(size_rotation.z);
    vec2 offset = mat2(vec2(c, s), vec2(-s, c)) * corner;
    mat4 particle_matrix = draw_is_world ? draw_matrix : MODEL_MATRIX * draw_matrix;
    vec3 center = (VIEW_MATRIX * particle_matrix * vec4(position_age.xyz, 1.0)).xyz;
    vec3 quad = vec3(offset * (sprite_enabled ? vec2(1.0) : size_rotation.xy) * particle_size_scale, 0.0);
    quad.xy *= vec2(length(particle_matrix[0].xyz), length(particle_matrix[1].xyz));
    vec3 position = center + quad;
    bool valid_position = all(lessThan(abs(position), vec3(3.0e38)));
    VERTEX = valid_position ? position : vec3(0.0);
    particle_color = texelFetch(particle_state, base + ivec2(2,0), 0);
    if (seed_alive.y < 0.5 || !valid_position) particle_color = vec4(0.0);
}

void fragment() {
    if (particle_color.a <= 0.0) discard;
    vec4 texel = vec4(1.0);
    if (use_particle_texture) {
        vec2 uv = texture_flip_y ? vec2(UV.x, 1.0 - UV.y) : UV;
        texel = texture(particle_texture, uv);
        if (texture_premultiplied) texel.rgb /= max(texel.a, 0.000001);
    }
    vec4 tint = texel * particle_color;
    ALBEDO = tint.rgb;
    ALPHA = clamp(tint.a, 0.0, 1.0);
}
)LGPK"
