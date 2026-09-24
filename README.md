# DivisionTranslate

[![NuGet](https://img.shields.io/nuget/v/DivisionTranslate?label=NuGet&logo=nuget)](https://www.nuget.org/packages/NorthRoc.DivisionTranslate)
[![NuGet Downloads](https://img.shields.io/nuget/dt/DivisionTranslate?label=Downloads&logo=nuget)](https://www.nuget.org/packages/NorthRoc.DivisionTranslate)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](https://github.com/NorthRocStudios/DivisionTranslate)
[![GPU](https://img.shields.io/badge/GPU-Direct3D%2011-green)](https://learn.microsoft.com/en-us/windows/win32/direct3d11/atoc-dx-graphics-direct3d-11)

**Write your compute shaders in C#. Run them anywhere.**

DivisionTranslate is a source-to-source compiler and runtime that lets you write HLSL compute shaders directly in C#. It translates your C# code to HLSL at runtime, compiles it, and executes it on the GPU using Direct3D 11. It's designed as a drop-in replacement for [ComputeSharp](https://github.com/Sergio0694/ComputeSharp), offering you full control over the pipeline without being locked into a specific runtime.

This library is part of the [Division Engine](https://github.com/NorthRocStudios/DivisionEngine) project.

## ✨ Features

- **Write Shaders in C#**: Use familiar C# syntax, structs, and methods to define your GPU kernels.
- **Runtime Compilation**: Compile new shaders on the fly without needing to restart your application or pre-compile.
- **Cross-Platform Ready**: The generated HLSL is compatible with multiple backends. The included runtime uses D3D11, but the core translation is backend-agnostic, paving the way for SPIR-V and other graphics APIs.
- **Familiar API**: The goal is a simple, intuitive API that feels like ComputeSharp, with methods for compiling shaders, binding resources, dispatching kernels, and reading back data.
- **CPU/GPU Parity**: Paired with the [DivisionMath](https://github.com/NorthRocStudios/DivisionMath) library, it ensures your shader math behaves identically on the CPU and GPU.

## 🚀 Getting Started

### Prerequisites

- .NET 10.0 or later
- Windows (the current D3D11 runtime backend is Windows-only, but a cross-platform Vulkan backend is planned)

### Installation

You can install DivisionTranslate via NuGet:

```bash
dotnet add package NorthRoc.DivisionTranslate
```

## 💖 Support

If you want to support DivisionTranslate and the rest of the Division Engine ecosystem, sponsor me on GitHub:

[![Sponsor](https://img.shields.io/badge/Sponsor-GitHub%20Sponsors-ea4aaa?style=flat&logo=githubsponsors&logoColor=white)](https://github.com/sponsors/Rex-J-W)
