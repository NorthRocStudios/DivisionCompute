//
// Copyright (c) 2026 Rex Woodfield and DivisionTranslate contributors
//
// This file is part of DivisionTranslate and is subject to the terms
// of the DivisionTranslate License. See the LICENSE.txt file in the
// project root for full license terms.
//
namespace DivisionEngine.ReferenceClasses
{
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class ShaderConstantBufferAttribute(int slot) : Attribute
    {
        public int Slot { get; set; } = slot;
    }
}
