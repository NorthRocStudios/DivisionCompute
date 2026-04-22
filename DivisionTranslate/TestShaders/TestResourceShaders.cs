using DivisionEngine.MathLib;
using DivisionTranslate.Graphics;

namespace DivisionTranslate.TestShaders
{
    [Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestResourceShaders.cs")]
    public struct ResourceTestShader
    {
        // Regular variables (allowed)
        float3 _cameraPosition;
        int4 _someData;
        float _time;

        // GPU resources
        [ShaderResource]
        Buffer<float> InputBuffer;

        [ShaderResource]
        RWBuffer<float> OutputBuffer;

        [ShaderResource]
        StructuredBuffer<MyDataStruct> DataBuffer;

        [ShaderResource]
        Texture2D<float4> InputTexture;

        [ShaderResource]
        RWTexture2D<float4> OutputTexture;

        [Kernel(ThreadsX = 8, ThreadsY = 8)]
        public void Execute(uint3 id)
        {
            float4 color = InputTexture[id.xy];
            float processed = color.r * _time;
            OutputBuffer[id.x] = processed;
            OutputTexture[id.xy] = new float4(processed, processed, processed, 1.0f);
        }
    }

    public struct MyDataStruct
    {
        public float3 position;
        public float value;
    }

    [Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestResourceShaders.cs")]
    public struct ResourceTestShader2
    {
        float3 _cameraPosition;
        int4 _someData;
        float _time;

        [ShaderResource] Buffer<float> InputBuffer;
        [ShaderResource] RWBuffer<float> OutputBuffer;
        [ShaderResource] StructuredBuffer<MyDataStruct> DataBuffer;
        [ShaderResource] Texture2D<float4> InputTexture;
        [ShaderResource] RWTexture2D<float4> OutputTexture;

        [Kernel(ThreadsX = 8, ThreadsY = 8)]
        public readonly void Execute(uint3 id)
        {
            // These all compile now!
            float inputValue = InputBuffer[id.x];
            float4 color = InputTexture[id.xy];
            float processed = color.r * _time;
            OutputBuffer[id.x] = processed;
            OutputTexture[id.xy] = new float4(processed, processed, processed, 1.0f);

            // Structured buffer access
            MyDataStruct data = DataBuffer[id.x];
            float positionLength = math.length(data.position);
        }
    }

    [Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestResourceShaders.cs")]
    public struct StructTest2Shader
    {
        // Nested struct definitions
        public struct MaterialData
        {
            public float3 albedo;
            public float metallic;
            public float roughness;
            public float3 emissive;
        }

        public struct MeshData
        {
            public float3 boundsMin;
            public float3 boundsMax;
            public MaterialData material;
            public int indexCount;
        }

        // GPU resources
        [ShaderResource]
        StructuredBuffer<MeshData> Meshes;

        [ShaderResource]
        RWBuffer<float3> OutputColors;

        float _time;

        [Kernel(ThreadsX = 8, ThreadsY = 8)]
        public void ProcessMeshes(uint3 id)
        {
            MeshData mesh = Meshes[id.x];
            MaterialData mat = mesh.material;

            float3 color = mat.albedo;
            color += mat.emissive * _time;
            color = math.saturate(color);

            OutputColors[id.x] = color;
        }

        [Kernel(ThreadsX = 8, ThreadsY = 8)]
        public void ClearColors(uint3 id)
        {
            OutputColors[id.x] = new float3(0, 0, 0);
        }
    }

    [Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestResourceShaders.cs")]
    public struct MultiKernelTestShader
    {
        // Shared struct
        public struct Particle
        {
            public float3 position;
            public float3 velocity;
            public float lifetime;
            public float4 color;
        }

        // Shared resources
        [ShaderResource]
        RWStructuredBuffer<Particle> Particles;

        [ShaderResource]
        Buffer<float> TimeDelta;

        float3 _gravity;
        float _maxLifetime;
        int _particleCount;

        // Kernel 1: Update physics
        [Kernel(ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1)]
        public void UpdatePhysics(uint3 id)
        {
            if (id.x >= _particleCount) return;

            Particle p = Particles[id.x];

            // Apply gravity
            p.velocity += _gravity * TimeDelta[0];
            p.position += p.velocity * TimeDelta[0];
            p.lifetime -= TimeDelta[0];

            // Reset if dead
            if (p.lifetime <= 0)
            {
                p.position = new float3(0, 0, 0);
                p.velocity = new float3(0, 1, 0) * 5;
                p.lifetime = _maxLifetime;
                p.color = new float4(1, 1, 1, 1);
            }

            Particles[id.x] = p;
        }

        // Kernel 2: Update colors based on lifetime
        [Kernel(ThreadsX = 32, ThreadsY = 32, ThreadsZ = 1)]
        public void UpdateColors(uint3 id)
        {
            if (id.x >= _particleCount) return;

            Particle p = Particles[id.x];
            float t = 1.0f - (p.lifetime / _maxLifetime);
            p.color = new float4(t, 1.0f - t, 0, 1);
            Particles[id.x] = p;
        }

        // Kernel 3: Reset all particles
        [Kernel(ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1)]
        public void ResetParticles(uint3 id)
        {
            if (id.x >= _particleCount) return;

            Particle p;
            p.position = new float3(0, 0, 0);
            p.velocity = new float3(0, 1, 0) * 5;
            p.lifetime = _maxLifetime;
            p.color = new float4(1, 1, 1, 1);
            Particles[id.x] = p;
        }
    }

    [Shader("D:\\Visual Studio\\Projects\\DivisionTranslate\\DivisionTranslate\\TestShaders\\TestResourceShaders.cs")]
    public struct StructTest4Shader
    {
        // Custom struct with helper methods (these become inline functions in HLSL)
        public struct Ray
        {
            public float3 origin;
            public float3 direction;

            public float3 GetPoint(float t)
            {
                return origin + direction * t;
            }
        }

        public struct HitInfo
        {
            public float distance;
            public float3 point;
            public float3 normal;
            public float3 color;
        }

        // GPU resources
        [ShaderResource]
        StructuredBuffer<Ray> Rays;

        [ShaderResource]
        RWStructuredBuffer<HitInfo> Hits;

        [ShaderResource]
        Texture2D<float4> SceneTexture;

        [ShaderResource]
        Sampler SceneSampler;

        float3 _sphereCenter;
        float _sphereRadius;
        float3 _sphereColor;

        // Inline function (will be translated to HLSL inline function)
        float HitSphere(Ray ray, float3 center, float radius)
        {
            float3 oc = ray.origin - center;
            float b = math.dot(oc, ray.direction);
            float c = math.dot(oc, oc) - radius * radius;
            float discriminant = b * b - c;

            if (discriminant < 0) return -1;
            return -b - math.sqrt(discriminant);
        }

        [Kernel(ThreadsX = 8, ThreadsY = 8, ThreadsZ = 1)]
        public void TraceRays(uint3 id)
        {
            Ray ray = Rays[id.x];
            float t = HitSphere(ray, _sphereCenter, _sphereRadius);

            HitInfo hit;
            if (t > 0)
            {
                hit.distance = t;
                hit.point = ray.GetPoint(t);
                hit.normal = math.normalize(hit.point - _sphereCenter);
                hit.color = _sphereColor;
            }
            else
            {
                hit.distance = -1;
                hit.point = new float3(0, 0, 0);
                hit.normal = new float3(0, 0, 0);
                hit.color = new float4(1, 1, 1, 1);
                //hit.color = SceneTexture.SampleLevel(SceneSampler, ray.direction.xy, 0).rgb; - Texture not implemented yet
            }

            Hits[id.x] = hit;
        }
    }
}
