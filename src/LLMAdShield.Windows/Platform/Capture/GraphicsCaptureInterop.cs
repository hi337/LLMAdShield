using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace LLMAdShield.Windows.Platform.Capture;

internal static class GraphicsCaptureInterop
{
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;

    private static readonly Guid GraphicsCaptureItemInterfaceId =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static readonly Guid DxgiDeviceInterfaceId =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static GraphicsCaptureItem CreateItemForWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A capture target window handle is required.", nameof(windowHandle));
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForWindow(windowHandle, GraphicsCaptureItemInterfaceId);
        if (itemPointer == nint.Zero)
        {
            throw new InvalidOperationException("Windows Graphics Capture did not create a capture item.");
        }

        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            _ = Marshal.Release(itemPointer);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice()
    {
        var result = CreateNativeDevice(D3DDriverTypeHardware, out var nativeDevice, out var immediateContext);
        if (result < 0)
        {
            ReleaseIfPresent(ref immediateContext);
            ReleaseIfPresent(ref nativeDevice);
            result = CreateNativeDevice(D3DDriverTypeWarp, out nativeDevice, out immediateContext);
        }

        Marshal.ThrowExceptionForHR(result);

        try
        {
            var interfaceId = DxgiDeviceInterfaceId;
            var queryResult = Marshal.QueryInterface(nativeDevice, in interfaceId, out var dxgiDevice);
            Marshal.ThrowExceptionForHR(queryResult);

            try
            {
                var wrapResult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectableDevice);
                Marshal.ThrowExceptionForHR(wrapResult);

                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectableDevice);
                }
                finally
                {
                    if (inspectableDevice != nint.Zero)
                    {
                        _ = Marshal.Release(inspectableDevice);
                    }
                }
            }
            finally
            {
                if (dxgiDevice != nint.Zero)
                {
                    _ = Marshal.Release(dxgiDevice);
                }
            }
        }
        finally
        {
            if (immediateContext != nint.Zero)
            {
                _ = Marshal.Release(immediateContext);
            }

            if (nativeDevice != nint.Zero)
            {
                _ = Marshal.Release(nativeDevice);
            }
        }
    }

    private static int CreateNativeDevice(
        int driverType,
        out nint nativeDevice,
        out nint immediateContext)
    {
        return D3D11CreateDevice(
            nint.Zero,
            driverType,
            nint.Zero,
            D3D11CreateDeviceBgraSupport,
            nint.Zero,
            0,
            D3D11SdkVersion,
            out nativeDevice,
            out _,
            out immediateContext);
    }

    private static void ReleaseIfPresent(ref nint pointer)
    {
        if (pointer == nint.Zero)
        {
            return;
        }

        _ = Marshal.Release(pointer);
        pointer = nint.Zero;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint windowHandle, in Guid interfaceId);

        nint CreateForMonitor([In] nint monitorHandle, in Guid interfaceId);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);
}
