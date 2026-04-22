namespace DivisionTranslate.ReferenceClasses
{
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class ShaderConstantBufferAttribute(int slot) : Attribute
    {
        public int Slot { get; set; } = slot;
    }
}
