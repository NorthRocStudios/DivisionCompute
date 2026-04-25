using DivisionEngine.MathLib;
using DivisionTranslate;
using DivisionTranslate.Graphics;

[Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestMathShader.cs")]
public struct BasicMathShader
{
    [Kernel]
    public void Execute(uint3 id)
    {
        // Basic arithmetic
        float a = 5.0f;
        float b = 3.0f;
        float sum = a + b;
        float diff = a - b;
        float product = a * b;
        float quotient = a / b;

        // Vector operations
        float3 pos = new float3(id.x, id.y, id.z);
        float3 offset = new float3(1.0f, 2.0f, 3.0f);
        float3 result = pos + offset;
        float3 scaled = result * 2.0f;

        // Component access
        float xComp = pos.x;
        float yComp = pos.y;
        float zComp = pos.z;
    }
}

[Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestMathShader.cs")]
public struct AdvancedMathShader
{
    [Kernel]
    public void Execute(uint3 id)
    {
        float3 pos = new float3(id.x, id.y, id.z);

        // Trigonometric functions
        float3 sinVal = math.sin(pos);
        float3 cosVal = math.cos(pos);
        float3 tanVal = math.tan(pos);
        float3 asinVal = math.asin(sinVal);
        float3 acosVal = math.acos(cosVal);
        float3 atanVal = math.atan(pos);

        // Exponential and logarithmic
        float3 expVal = math.exp(pos);
        float3 exp2Val = math.exp2(pos);
        float3 logVal = math.log(pos + 1.0f);
        float3 log2Val = math.log2(pos + 1.0f);
        float3 log10Val = math.log10(pos + 1.0f);

        // Power and roots
        float3 powVal = math.pow(pos, 2.0f);
        float3 sqrtVal = math.sqrt(pos);
        float3 rsqrtVal = math.rsqrt(pos + 1.0f);

        // Rounding
        float3 ceilVal = math.ceil(pos);
        float3 floorVal = math.floor(pos);
        float3 roundVal = math.round(pos);
        float3 fracVal = math.frac(pos);

        // Min/Max/Clamp
        float3 minVal = math.min(pos, 10.0f);
        float3 maxVal = math.max(pos, 0.0f);
        float3 clampVal = math.clamp(pos, 0.0f, 10.0f);
        float3 saturateVal = math.saturate(pos);
    }
}

[Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestMathShader.cs")]
public struct SDFOperationsShader
{
    [Kernel(8, 8, 1)]
    public void Execute(uint3 id)
    {
        float3 p = new float3(id.x, id.y, id.z);

        // Basic SDF primitives
        float sphere = math.length(p) - 1.0f;
        float box = math.length(math.max(math.abs(p) - new float3(1.0f, 1.0f, 1.0f), 0.0f));
        float torus = math.length(new float2(math.length(p.xy) - 0.5f, p.z)) - 0.2f;

        // SDF operations
        float unionOp = math.min(sphere, box);
        float intersectOp = math.max(sphere, box);
        float subtractOp = math.max(sphere, -box);

        // Smooth union (smin)
        float k = 0.2f;
        float smoothUnion = math.min(sphere, box);
        float h = math.max(k - math.abs(sphere - box), 0.0f);
        smoothUnion = math.min(sphere, box) - h * h * 0.25f / k;

        // Transformations
        float3 rotated = new float3(p.x, p.z, p.y);
        float3 translated = p - new float3(0.0f, 1.0f, 0.0f);
        float scaled = math.length(p / 2.0f) - 1.0f;

        // Repetition
        float3 repeated = p;
        repeated.x = math.abs(repeated.x) % 2.0f - 1.0f;
        float repeatedSphere = math.length(repeated) - 0.5f;
    }
}

[Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestMathShader.cs")]
public struct ComplexExpressionsShader
{
    //int4 testVar4;
    //float3 testVar3;
    //int testVar2;
    //float testVar;

    //[ShaderResource]
    //Buffer<uint> testConstBuffer;
    //[ShaderResource]
    //RWBuffer<int> testRWConstBuffer;
    //[ShaderResource]
    //StructuredBuffer<SomeGPUFriendlyStruct> testStructBuffer;
    //[ShaderResource]
    //RWStructuredBuffer<float4> testRWStructBuffer;
    //[ShaderResource]
    //Texture2D<float4> testTexture;
    //[ShaderResource]
    //RWTexture2D<float4> testRWTexture;

    [Kernel]
    public void Execute(uint3 id)
    {
        float3 pos = new float3(id.x, id.y, id.z);

        // Mandelbulb-style SDF
        float3 z = pos;
        float dr = 1.0f;
        float r = 0.0f;

        for (int i = 0; i < 5; i++)
        {
            r = math.length(z);
            if (r > 2.0f) break;

            float theta = math.acos(z.z / r);
            float phi = math.atan2(z.y, z.x);
            dr = math.pow(r, 2.0f - 1.0f) * 2.0f * dr + 1.0f;

            float zr = math.pow(r, 2.0f);
            theta = theta * 2.0f;
            phi = phi * 2.0f;

            z = new float3(
                zr * math.sin(theta) * math.cos(phi),
                zr * math.sin(theta) * math.sin(phi),
                zr * math.cos(theta)
            );
            z += pos;
        }

        float mandelbulb = 0.5f * math.log(r) * r / dr;

        // Combining multiple operations
        float3 color = math.saturate(new float3(
            math.sin(mandelbulb * 1.0f),
            math.sin(mandelbulb * 2.0f + 2.0f),
            math.sin(mandelbulb * 3.0f + 4.0f)
        ));

        float final = math.length(color) * mandelbulb;
    }
}