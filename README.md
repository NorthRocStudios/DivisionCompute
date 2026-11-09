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
- **Structured Buffers**: Upload arrays of structs, read them with `StructuredBuffer<T>`, write them with `RWStructuredBuffer<T>`, and read the results back without hand-building D3D11 views.
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

## 📦 Structured Buffers

`StructuredBuffer<T>` and `RWStructuredBuffer<T>` are the shader-side resource types.
`ReadOnlyBuffer<T>` and `ReadWriteBuffer<T>` are their CPU-side owners: they allocate
the D3D11 buffer, create the SRV/UAV, upload initial data, and read results back.

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

using var input = context.CreateReadOnlyBuffer(objects);
using var output = context.CreateReadWriteBuffer<SDFObject>(objects.Length);

shader.SetReadOnlyBuffer("input", input);
shader.SetReadWriteBuffer("output", output);

int kernel = shader.FindKernel("Execute");
shader.Dispatch(kernel, (uint)((objects.Length + 63) / 64), 1, 1);

SDFObject[] results = output.GetData(context.Context);
```

### Reading and writing

- `input.Length` and `input.Count` both translate to a generated `GetDimensions`
  helper. HLSL `StructuredBuffer` has no `Length` property.
- `GetData` copies through a staging buffer and blocks until the readback
  completes; `SetData` uploads through `UpdateSubresource`.
- `ReadWriteBuffer<T>` exposes both `Uav` and `Srv`. Bind it with
  `SetReadWriteBuffer` when the shader field is `RWStructuredBuffer<T>`, or with
  `SetReadOnlyBuffer` when a later pass reads it as `StructuredBuffer<T>`.
- `Dispatch` automatically unbinds UAVs afterward, so `GetData` can be called
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

## 💖 Support

If you want to support DivisionCompute and the rest of the Division Engine ecosystem, sponsor me on GitHub:

[![Sponsor](https://img.shields.io/badge/Sponsor-GitHub%20Sponsors-ea4aaa?style=flat&logo=githubsponsors&logoColor=white)](https://github.com/sponsors/Rex-J-W)
