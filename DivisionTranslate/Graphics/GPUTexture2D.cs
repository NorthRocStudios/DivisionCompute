using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DivisionTranslate.Graphics
{
    /// <summary>
    /// Owns a D3D11 Texture2D plus its UAV (write) and SRV (read) views.
    /// This is the runtime counterpart of the RWTexture2D&lt;T&gt; marker.
    /// </summary>
    public sealed class GPUTexture2D<T> : IDisposable where T : unmanaged
    {
        public ID3D11Texture2D Texture { get; }
        public ID3D11UnorderedAccessView Uav { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public int Width { get; }
        public int Height { get; }
        public Format Format { get; }

        public GPUTexture2D(ID3D11Device device, int width, int height, Format format)
        {
            Width = width;
            Height = height;
            Format = format;

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            Texture = device.CreateTexture2D(desc);

            Uav = device.CreateUnorderedAccessView(Texture, new UnorderedAccessViewDescription
            {
                Format = format,
                ViewDimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0 },
            });

            Srv = device.CreateShaderResourceView(Texture, new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            });
        }

        public void Dispose()
        {
            Srv?.Dispose();
            Uav?.Dispose();
            Texture?.Dispose();
        }
    }
}
