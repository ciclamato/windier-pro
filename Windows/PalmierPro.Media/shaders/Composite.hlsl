// Direct3D 11 baseline compositor. Effect order is source -> crop -> transform -> opacity.
// The color-management stage is kept explicit so SDR/HDR transfer functions can be added
// without changing timeline semantics.
cbuffer CompositeConstants : register(b0)
{
    float4x4 transform;
    float4 crop;       // left, top, right, bottom in normalized source space
    float opacity;
    float2 viewport;
    float3 _padding;
};

Texture2D sourceTexture : register(t0);
SamplerState linearSampler : register(s0);

struct VertexInput { float3 position : POSITION; float2 uv : TEXCOORD0; };
struct VertexOutput { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };

VertexOutput vs_main(VertexInput input)
{
    VertexOutput output;
    output.position = mul(transform, float4(input.position, 1));
    output.uv = float2(lerp(crop.x, 1 - crop.z, input.uv.x), lerp(crop.y, 1 - crop.w, input.uv.y));
    return output;
}

float4 ps_main(VertexOutput input) : SV_TARGET
{
    float4 color = sourceTexture.Sample(linearSampler, input.uv);
    color.a *= opacity;
    return color;
}
