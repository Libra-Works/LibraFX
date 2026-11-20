param([string]$RuntimeRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $RuntimeRoot 'Integrations/Unity/Runtime/Resources/LibraProcedural.compute'
$source = [IO.File]::ReadAllText($sourcePath)
$functions = $source.Substring($source.IndexOf('uint phash'))
$glsl = $functions.Replace('[unroll]', '').Replace('[loop]', '')
$glsl = $glsl.Replace('[numthreads(8,8,1)]', 'layout(local_size_x=8,local_size_y=8,local_size_z=1) in;')
$glsl = $glsl.Replace('void Generate(uint3 id : SV_DispatchThreadID)', 'void main()')
$glsl = $glsl.Replace('if(any(id.xy>=_Resolution)) return;', 'uvec3 id=gl_GlobalInvocationID; if(any(greaterThanEqual(id.xy,uvec2(_Resolution)))) return;')
foreach ($type in @{float2='vec2';float4='vec4';int2='ivec2';uint3='uvec3';uint4='uvec4'}.GetEnumerator()) {
    $glsl = $glsl.Replace($type.Key, $type.Value)
}
$glsl = $glsl.Replace('lerp(', 'mix(').Replace('frac(', 'fract(').Replace('fmod(', 'mod(')
$glsl = $glsl.Replace('asuint(', 'floatBitsToUint(')
$glsl = $glsl.Replace('_LocalTime.xxxx', 'vec4(_LocalTime)')
$glsl = $glsl.Replace('all(isfinite(v))', '(!any(isnan(v)) && !any(isinf(v)))')
$glsl = $glsl.Replace('clamp(v,-65504.0,65504.0)', 'clamp(v,vec4(-65504.0),vec4(65504.0))')
$glsl = $glsl.Replace('max(a,1e-6)', 'max(a,vec4(1e-6))').Replace('clamp(op.p.x,-32,32)', 'clamp(op.p.x,-32.0,32.0)')
$glsl = $glsl.Replace('pow(max(a,vec4(1e-6)),clamp(op.p.x,-32.0,32.0))', 'pow(max(a,vec4(1e-6)),vec4(clamp(op.p.x,-32.0,32.0)))')
$glsl = [regex]::Replace($glsl, '(case (?:4|5|6|7|8|9|10|11|12|13|14|28): v=)(.*?);', '$1vec4($2);')
$glsl = [regex]::Replace($glsl, '(_Texture[0-3])\.SampleLevel\(sampler_linear_repeat,a.xy,0\)', 'textureLod($1,a.xy,0.0)')
$glsl = $glsl.Replace('_Output[id.xy]=regs[min(_OutputRegister,_OperationCount)];', 'imageStore(_Output,ivec2(id.xy),regs[min(_OutputRegister,_OperationCount)]);')
$header = @'
#version 450
struct PixelOperation { uint kind, inputA, inputB, seed; vec4 p, q, r; };
layout(set=0,binding=0,std430) readonly buffer Operations { PixelOperation _Operations[]; };
layout(set=0,binding=1,rgba16f) uniform writeonly image2D _Output;
layout(set=0,binding=2) uniform sampler2D _Texture0;
layout(set=0,binding=3) uniform sampler2D _Texture1;
layout(set=0,binding=4) uniform sampler2D _Texture2;
layout(set=0,binding=5) uniform sampler2D _Texture3;
layout(push_constant,std430) uniform Parameters { uint count, outputRegister, resolution; float time; } pc;
#define _OperationCount pc.count
#define _OutputRegister pc.outputRegister
#define _Resolution pc.resolution
#define _LocalTime pc.time
float saturate(float x) { return clamp(x,0.0,1.0); }
vec4 saturate(vec4 x) { return clamp(x,vec4(0),vec4(1)); }
'@
$glsl = $header + "`n" + $glsl
$godotPath = Join-Path $RuntimeRoot 'Integrations/Godot/LibraProceduralShaderSource.h'
[IO.File]::WriteAllText($godotPath, "#pragma once`nnamespace liberafx::godot_adapter::procedural_shader {`ninline constexpr auto source = R`"PFXGLSL(`n$glsl`n)PFXGLSL`";`n}`n", [Text.UTF8Encoding]::new($false))
$unreal = $source.Replace('#pragma kernel Generate', '#include "/Engine/Public/Platform.ush"')
# UE's stable constant-buffer parser requires parameter names to start with a letter.
$unreal = [regex]::Replace($unreal, '\b_(OperationCount|OutputRegister|Resolution|LocalTime|Operations|Output|Texture[0-3])\b', 'LibraProcedural$1')
$unrealPath = Join-Path $RuntimeRoot 'Integrations/Unreal/LibraRuntimeForUnreal/Shaders/Private/LibraProcedural.usf'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($unrealPath)) | Out-Null
[IO.File]::WriteAllText($unrealPath, $unreal, [Text.UTF8Encoding]::new($false))
