//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
namespace DivisionTranslate
{
    [AttributeUsage(AttributeTargets.Struct)]
    public class ShaderAttribute(string sourceFilePath) : Attribute
    {
        public string SourceFilePath { get; set; } = sourceFilePath;
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class KernelAttribute : Attribute
    {
        public int ThreadsX { get; set; } = 64;
        public int ThreadsY { get; set; } = 1;
        public int ThreadsZ { get; set; } = 1;

        public KernelAttribute()
        {
            ThreadsX = 64;
            ThreadsY = 1;
            ThreadsZ = 1;
        }

        public KernelAttribute(int threadsX, int threadsY, int threadsZ)
        {
            ThreadsX = threadsX;
            ThreadsY = threadsY;
            ThreadsZ = threadsZ;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class ShaderResourceAttribute : Attribute
    {
        public int Slot { get; set; }
        public ShaderResourceType Type { get; set; }
    }

    public enum ShaderResourceType { Buffer, Texture2D, Texture3D, Sampler, StructuredBuffer }
}
