// Native static-instance convention: four vectors per instance at VS b11.
// Camera-relative projection follows the installed static VS 815B86B7.
cbuffer Instances : register(b11) { float4 instance_rows[64]; };
cbuffer View : register(b12) { float4 view_rows[14]; };
cbuffer Material : register(b0) { float4 base_color; };

struct VertexOutput {
    float4 position : SV_POSITION;
    float3 normal : TEXCOORD0;
};

float4 project_position(float3 position, uint instance) {
    uint row = instance * 4;
    float3 relative = float3(
        dot(instance_rows[row].xyz, position) + (instance_rows[row].w - view_rows[7].x),
        dot(instance_rows[row+1].xyz, position) + (instance_rows[row+1].w - view_rows[7].y),
        dot(instance_rows[row+2].xyz, position) + (instance_rows[row+2].w - view_rows[7].z));
    return relative.y * view_rows[1] + relative.x * view_rows[0]
         + relative.z * view_rows[2] + view_rows[13];
}

float4 DepthMain(float4 position : POSITION, uint instance : SV_InstanceID) : SV_POSITION {
    return project_position(position.xyz, instance);
}

VertexOutput VSMain(float4 position : POSITION, float2 uv : TEXCOORD0,
                    float4 normal : NORMAL0, uint instance : SV_InstanceID) {
    VertexOutput output;
    output.position = project_position(position.xyz, instance);
    uint row = instance * 4;
    output.normal = normalize(float3(dot(instance_rows[row].xyz, normal.xyz),
        dot(instance_rows[row+1].xyz, normal.xyz), dot(instance_rows[row+2].xyz, normal.xyz)));
    return output;
}

void PSMain(VertexOutput input, out float4 color : SV_Target0,
            out float4 normal : SV_Target1, out float4 material : SV_Target2) {
    color = float4(base_color.rgb, 0.2);
    normal = float4(saturate(normalize(input.normal) * 0.375 + 0.5), 0);
    material = float4(0, 0.5, 0, 1);
}
