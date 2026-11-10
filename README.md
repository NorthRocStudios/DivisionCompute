# DivisionCompute

[![NuGet](https://img.shields.io/nuget/v/NorthRoc.DivisionCompute?label=NuGet&logo=nuget)](https://www.nuget.org/packages/NorthRoc.DivisionCompute/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NorthRoc.DivisionCompute?label=Downloads&logo=nuget)](https://www.nuget.org/packages/NorthRoc.DivisionCompute/)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](https://github.com/NorthRocStudios/DivisionCompute)
[![GPU](https://img.shields.io/badge/GPU-Direct3D%2011-green)](https://learn.microsoft.com/en-us/windows/win32/direct3d11/atoc-dx-graphics-direct3d-11)

**Write your compute shaders in C#. Run them anywhere.**

DivisionCompute is a source-to-source compiler and runtime that lets you write HLSL compute shaders directly in C#. It translates your C# code to HLSL at runtime, compiles it, and executes it on the GPU using Direct3D 11. It's designed as a drop-in replacement for [ComputeSharp](https://github.com/Sergio0694/ComputeSharp), offering you full control over the pipeline without being locked into a specific runtime.

This library is part of the [Division Engine](https://github.com/NorthRocStudios/DivisionEngine) project.

## ✨ Features

- **Write Shaders in C#**: Use familiar C# syntax, structs, and methods to define your GPU kernels.
- **Runtime Compilation**: Compile new shaders on the fly without needing to restart your application or pre-compile.
- **Cross-Platform Ready**: The generated HLSL is compatible with multiple backends. The included runtime uses D3D11, but the core translation is backend-agnostic, paving the way for SPIR-V and other graphics APIs.
- **Familiar API**: The goal is a simple, intuitive API that feels like ComputeSharp, with methods for compiling shaders, binding resources, dispatching kernels, and reading back data.
- **Unity-Style Parameters**: Plain fields on the shader become uniforms, set with `shader.SetFloat("width", 1280)` / `SetVector` / `SetInt` / `SetBool` - no parameter structs or manual constant-buffer packing.
- **Structured Buffers**: Upload arrays of structs, read them with `StructuredBuffer<T>`, write them with `RWStructuredBuffer<T>`, and read the results back without hand-building D3D11 views.
- **Unified Resource Types**: The same `StructuredBuffer<T>`, `RWStructuredBuffer<T>`, `Texture2D<T>`, and `Texture3D<T>` types are used in shader code and on the CPU for allocation and readback.
- **CPU/GPU Parity**: Paired with the [DivisionMath](https://github.com/NorthRocStudios/DivisionMath) library, it ensures your shader math behaves identically on the CPU and GPU.

## 🚀 Getting Started

### Prerequisites

- .NET 10.0 or later
- Windows (the current D3D11 runtime backend is Windows-only, but a cross-platform Vulkan backend is planned)

### Installation

You can install DivisionCompute via NuGet:

```bash
dotnet add package NorthRoc.DivisionCompute
```

## 🧠 Shader Compilation Cache

Compiling HLSL is the expensive part of the pipeline (D3DCompiler is not fast on
large, heavily inlined shaders), so a successful compilation is cached on disk:

- Location: `%LOCALAPPDATA%\DivisionEngine\ShaderCache`
- Key: SHA-256 over the generated HLSL, entry point, profile and compiler flags,
  so editing a shader invalidates its entry automatically.
- Override the directory with `DivisionShaderCompiler.CacheDirectory`, or turn
  caching off entirely with `DIVISION_DISABLE_SHADER_CACHE=1`.
- Cache writes are best-effort: a missing or unwritable cache simply compiles.

Pass `ShaderFlags.SkipOptimization` / `OptimizationLevel1` to
`DivisionShaderCompiler.CompileHLSL(result, flags)` when you want to trade code
quality for compile time.

## 📦 Structured Buffers

The resource types are both shader field types and CPU-side owners. `StructuredBuffer<T>`
allocates the D3D11 structured buffer and its SRV; `RWStructuredBuffer<T>` adds a UAV
and can also be read through its SRV.

```csharp
using DivisionEngine;
using DivisionEngine.Graphics;
using DivisionEngine.MathLib;

public struct SDFObject
{
    public float3 position;
    public float radius;
    public int materialId;
}

[Shader]
public struct SDFShader
{
    [ShaderResource]
    public StructuredBuffer<SDFObject> input;

    [ShaderResource]
    public RWStructuredBuffer<SDFObject> output;

    [Kernel(64, 1, 1)]
    public void Execute(uint3 id)
    {
        uint index = id.x;
        if (index >= (uint)input.Length)
            return;

        SDFObject item = input[index];
        item.radius *= 2.0f;
        output[index] = item;
    }
}
```

```csharp
using var context = new DivisionComputeContext();
using var shader = context.CompileShader(typeof(SDFShader));

using var input = context.CreateStructuredBuffer(objects);
using var output = context.CreateRWStructuredBuffer<SDFObject>(objects.Length);

shader.SetStructuredBuffer("input", input);
shader.SetRWStructuredBuffer("output", output);

int kernel = shader.FindKernel("Execute");
shader.Dispatch(kernel, (uint)((objects.Length + 63) / 64), 1, 1);

SDFObject[] results = output.GetData(context.Context);
```

### Reading and writing

- `input.Length` and `input.Count` are real CPU properties on the buffer object.
  Inside HLSL they translate to a generated `GetDimensions` helper; HLSL
  `StructuredBuffer` has no `Length` property.
- `GetData` copies through a staging buffer and blocks until the readback
  completes; `SetData` uploads through `UpdateSubresource`.
- `RWStructuredBuffer<T>` exposes both `Uav` and `Srv`. Bind it with
  `SetRWStructuredBuffer` when the shader field is `RWStructuredBuffer<T>`, or with
  `SetStructuredBuffer` when a later pass reads it as `StructuredBuffer<T>`.
- `Dispatch` automatically unbinds resources afterward, so `GetData` can be called
  immediately.

### Element layout rules

`T` must be an unmanaged, sequential struct whose C# layout matches its HLSL layout.
The supported fields are:

- `float`, `double`, `int`, `uint`
- DivisionMath vectors such as `float2`, `float3`, `float4`, `int2`, `uint4`
- enums backed by `int` or `uint`
- nested structs made from the same supported fields

The following are rejected with a diagnostic instead of being silently sent to the
GPU with the wrong stride:

- `bool` and `char` (C# and HLSL use different sizes)
- matrices (layout rules need an explicit contract)
- arrays, strings, reference types, properties, and explicit/packed layouts

Element structs may be declared in another file or assembly. The translator
resolves them from metadata, emits their HLSL declarations in dependency order, and
the runtime validates the stride before allocating the buffer. Nested structs are
supported and are emitted before their parent.

## 🧱 Textures

`Texture2D<T>` / `RWTexture2D<T>` and `Texture3D<T>` / `RWTexture3D<T>` follow the
same pattern. They are the shader field types and the CPU-side texture owners.

```csharp
[Shader]
public struct TextureCopyShader
{
    [ShaderResource]
    public Texture3D<float4> input;

    [ShaderResource]
    public RWTexture3D<float4> output;

    [Kernel(4, 4, 4)]
    public void Execute(uint3 id)
    {
        uint3 coord = new uint3(id.x, id.y, id.z);
        output[coord] = input[coord];
    }
}
```

```csharp
using var input = context.CreateTexture3D<float4>(4, 4, 4);
using var output = context.CreateRWTexture3D<float4>(4, 4, 4);

input.SetData(context.Context, inputData);

shader.SetTexture3D("input", input);
shader.SetRWTexture3D("output", output);
shader.Dispatch(shader.FindKernel("Execute"), 1, 1, 1);

float4[] result = output.GetData(context.Context);
```

- `Texture2D<T>` and `Texture3D<T>` own an SRV; the `RW` variants also own a UAV.
- `CreateTexture2D<T>(width, height)` and `CreateTexture3D<T>(width, height, depth)`
  infer the DXGI format from `T` through `DXGIFormatMapper`. Explicit-format
  overloads exist for special cases; the format's element size must match `T`.
- `GetData` blocks until the readback completes and returns row-major data:
  `x + y * Width` for 2D and `x + y * Width + z * Width * Height` for 3D.
- `SetData` requires a complete image/volume.
- Texture indexers are shader-only; use `SetData` / `GetData` on the CPU.
- The old `ReadOnlyBuffer<T>`, `ReadWriteBuffer<T>`, and `GPUTexture2D<T>` wrappers
  have been removed. The shader field types are now the CPU types.

## 🎛️ Shader Parameters

Plain fields on the shader struct are uniforms. Set them by name from C#, the same
way a Unity compute shader works - no parameter struct, no manual packing:

```csharp
[Shader]
public struct BlurShader
{
    public float width;
    public float height;
    public float radius;
    public float3 tint;

    [ShaderResource]
    public RWTexture2D<float4> output;

    [Kernel(8, 8, 1)]
    public void Execute(uint3 id)
    {
        float sigma = radius * 0.5f;   // uniforms are plain identifiers in HLSL
        output[new int2((int)id.x, (int)id.y)] = new float4(tint, sigma);
    }
}
```

```csharp
using DivisionShader shader = context.CompileShader(typeof(BlurShader));

shader.SetFloat("width", 1280);
shader.SetFloat("height", 720);
shader.SetVector("tint", new float3(1f, 0.8f, 0.6f));
shader.SetFloat("radius", 6f);

shader.Dispatch2D(shader.FindKernel("Execute"), 1280, 720);

shader.SetFloat("radius", 12f);          // just set it again for the next dispatch
shader.Dispatch2D(shader.FindKernel("Execute"), 1280, 720);
```

- `SetFloat`, `SetInt`, `SetUInt`, `SetBool`, and `SetVector` (`float2/3/4`,
  `int2/3/4`, `uint2/3/4`) write into a CPU-side mirror of the parameter buffer.
  `Dispatch` uploads it once, so any number of setters costs a single
  constant-buffer update.
- The translator emits `cbuffer DivisionParams : register(b0) { float width; ... }`
  from those fields and keeps them as plain identifiers in shader code.
- Values are packed with the Direct3D constant-buffer rules (16-byte registers, no
  member may straddle a register boundary), computed from the field types - so the
  C# struct needs **no manual padding**.
- Unknown names and type mismatches throw `ArgumentException` at the call site
  instead of silently doing nothing, and `shader.UniformNames` lists what is settable.
- Supported types: `float`, `int`, `uint`, `bool`, int/uint-backed enums, and
  DivisionMath float/int/uint vectors. Matrices, nested structs, arrays, and
  `double` are rejected with a diagnostic - put larger data in a structured buffer.
- `Dispatch2D(kernel, width, height)` is the `Device.For(width, height, shader)`
  equivalent; it reads the thread-group size from the `[Kernel]` attribute.
  `GetThreadGroupSize` and `ResourceLayout` expose the same information for tooling.
## 💖 Support

If you want to support DivisionCompute and the rest of the Division Engine ecosystem, sponsor me on GitHub:

[![Sponsor](https://img.shields.io/badge/Sponsor-GitHub%20Sponsors-ea4aaa?style=flat&logo=githubsponsors&logoColor=white)](https://github.com/sponsors/Rex-J-W)
