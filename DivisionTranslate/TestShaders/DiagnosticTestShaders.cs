using DivisionEngine.MathLib;

namespace DivisionTranslate.TestShaders
{
    [Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\DiagnosticTestShaders.cs")]
    public struct DiagnosticTest1
    {
        [Kernel]
        public void Execute(uint3 id)
        {
            float3 pos = new float3(id.x, id.y, id.z);

            // WARNING: Unused variable
            float unusedValue = math.length(pos);

            // WARNING: Variable assigned but never used
            float computed = math.sin(pos.x) * math.cos(pos.y);
            computed = math.tan(pos.z);

            // WARNING: Implicit truncation (float4 to float3)
            float4 extra = new float4(1, 2, 3, 4);
            float3 truncated = extra;  // Should warn about truncation

            // WARNING: Implicit conversion (double to float)
            double doubleValue = 1.5;
            float floatValue = (float)doubleValue;  // Should warn about precision loss

            // WARNING: Unreachable code after return
            float test = 5.0f;
            if (test > 10.0f)
                return;
            float unreachable = test * 2.0f;  // Warning: unreachable code

            float result = truncated.x + floatValue;  // Using the variable to avoid unused warning
        }

        // WARNING: Unused inline function
        float UnusedFunction(float x)
        {
            return x * x;
        }

        // WARNING: Function with no return statement on all paths
        float MissingReturn(bool condition)
        {
            if (condition)
                return 1.0f;
            return 0f;
            // Missing return when condition is false
        }
    }
}
