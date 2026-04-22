using DivisionEngine.MathLib;
using DivisionTranslate;

[Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\AdvMathTestShaders.cs")]
public struct ComplexControlFlowShader
{
    [Kernel]
    public void Execute(uint3 id)
    {
        float3 pos = new float3(id.x, id.y, id.z);
        float result = 0.0f;

        // Nested if-else with multiple conditions
        if (pos.x > 0.0f)
        {
            if (pos.y > 0.0f)
            {
                if (pos.z > 0.0f)
                    result = math.length(pos);
                else
                    result = math.length(pos.xy);
            }
            else
            {
                result = math.length(pos.xz);
            }
        }
        else
        {
            result = math.abs(pos.x);
        }

        // Complex for loop with continue and break
        float sum = 0.0f;
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
                continue;

            if (i > 50)
                break;

            sum += i * result;
        }

        // While loop
        float t = 0.0f;
        while (t < 1.0f)
        {
            t += 0.01f;
            sum += math.sin(t * result);
        }

        // Do-while loop (if C# supports it)
        float u = 0.0f;
        do
        {
            u += 0.1f;
            sum *= u;
        } while (u < 5.0f);
    }
}

[Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\AdvMathTestShaders.cs")]
public struct TypeInferenceShader
{
    [Kernel]
    public void Execute(uint3 id)
    {
        float3 pos = new float3(id.x, id.y, id.z);

        // Test 'var' with complex initializers
        var a = pos.xyx;  // Should infer float3
        var b = pos.rgb;  // Should infer float3 (color swizzle)
        var c = pos.xx;   // Should infer float2
        var d = pos.x;    // Should infer float

        // Complex swizzling combinations
        float4 pos4 = new float4(pos, 1.0f);
        var e = pos4.xyz;  // float3
        var f = pos4.xyzw; // float4
        var g = pos4.wzyx; // Reverse order
        var h = pos4.xxzz; // Duplicate components

        // Mixed swizzle and math
        float3 result = a.xyz + b.rgb;
        float2 xy = pos.xy * c.xy;

        // Chain swizzling
        float value = pos.xyx.yzx.x;  // Should be pos.x (insane but valid)

        // Swizzle on left side of assignment (HLSL supports this)
        // result.xy = new float2(1.0f, 2.0f); - math library does not support this yet
        result.z = 3.0f;
    }
}

[Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\AdvMathTestShaders.cs")]
public struct ComplexExpressionsShaderAdv
{
    [Kernel]
    public void Execute(uint3 id)
    {
        float3 pos = new float3(id.x, id.y, id.z);

        // Nested function calls with complex arguments
        float result = math.sin(math.cos(math.tan(pos.x))) *
                       math.sqrt(math.abs(math.pow(pos.y, 2.0f) - pos.z));

        // Complex ternary operations (C# doesn't have ternary? Actually it does)
        float ternary = pos.x > 0.0f ? math.length(pos) : math.length(-pos);
        float nestedTernary = pos.x > 0.0f ? (pos.y > 0.0f ? 1.0f : 2.0f) : 3.0f;
        float a = 1, b = 2, c = 3, d = 4, e = 5, f = 6, g = 7, h = 8;

        // Operator precedence stress test
        float precedence = a + b * c - d / e % f;
        float withParens = (a + b) * (c - d) / (e + f);
        float complex = a + (b * (c - d) / e) % f;

        // Compound assignments
        a += b;
        a -= c;
        a *= d;
        a /= e;
        a %= f;

        // Multiple operators in one line
        float multiOp = (a + b) * (c - d) + (e / f) - (g % h);

        // Bitwise operations (if needed for integer work)
        int2 intPos = new int2((int)id.x, (int)id.y);
        // int2 bitwise = (intPos & 0xFF) | ((intPos >> 8) & 0xFF); - math library does not support this yet
    }
}
