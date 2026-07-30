using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LLMAdShield.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LLMAdShield.Windows.Platform.Capture;

/// <summary>
/// Captures a caller-selected HWND with Windows Graphics Capture. Frames are sampled at
/// two frames per second, resized in memory, and emitted as JPEGs. No image is written to disk.
/// </summary>
public sealed class WindowsGraphicsCaptureSource : ICaptureSource
{
    private const int MaximumWidth = 640;
    private const int MaximumHeight = 360;
    private const int HistoryCapacity = 20;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);

    private readonly CaptureTarget _target;
    private readonly nint _windowHandle;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _historySync = new();
    private readonly Queue<CapturedFrame> _history = new(HistoryCapacity);
    private int _disposeStarted;

    public WindowsGraphicsCaptureSource(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _windowHandle = target.WindowHandle;
        Source = new CaptureSourceDescriptor(
            $"hwnd:{target.WindowHandle.ToInt64():X}:{target.ProcessId}:{target.ProcessStartTimeUtcTicks}",
            target.DisplayName);
    }

    public WindowsGraphicsCaptureSource(nint windowHandle, string displayName)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A capture target window handle is required.", nameof(windowHandle));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        _windowHandle = windowHandle;
        _target = new CaptureTarget(
            windowHandle,
            displayName.Trim(),
            0,
            0,
            string.Empty,
            System.Windows.Rect.Empty);
        Source = new CaptureSourceDescriptor($"hwnd:{windowHandle.ToInt64():X}", displayName.Trim());
    }

    public CaptureSourceDescriptor Source { get; }

    /// <summary>
    /// Returns owned copies of the latest ten seconds of sampled frames. The caller must
    /// dispose every returned frame. The internal ring buffer never persists imagery.
    /// </summary>
    public IReadOnlyList<CapturedFrame> GetRecentFrames()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);

        lock (_historySync)
        {
            return _history.Select(frame => frame.Clone()).ToArray();
        }
    }

    public async IAsyncEnumerable<CapturedFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            using var linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var token = linkedCancellation.Token;

            if (!GraphicsCaptureSession.IsSupported())
            {
                throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this device.");
            }

            if (!IsExpectedTargetWindow())
            {
                throw new InvalidOperationException("The selected capture window no longer exists.");
            }

            var item = GraphicsCaptureInterop.CreateItemForWindow(_windowHandle);
            var device = GraphicsCaptureInterop.CreateDirect3DDevice();
            Direct3D11CaptureFramePool? framePool = null;
            GraphicsCaptureSession? session = null;

            var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            var sourceClosed = false;

            void OnFrameArrived(Direct3D11CaptureFramePool _, object __) =>
                signal.Writer.TryWrite(true);

            void OnSourceClosed(GraphicsCaptureItem _, object __)
            {
                sourceClosed = true;
                signal.Writer.TryWrite(false);
            }

            try
            {
                var initialSize = item.Size;
                if (initialSize.Width <= 0 || initialSize.Height <= 0)
                {
                    throw new InvalidOperationException("The selected capture window has no drawable area.");
                }

                framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    initialSize);

                session = framePool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;

                framePool.FrameArrived += OnFrameArrived;
                item.Closed += OnSourceClosed;
                session.StartCapture();

                var poolSize = initialSize;
                long lastSampleTimestamp = 0;

                while (!sourceClosed)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsExpectedTargetWindow())
                    {
                        throw new InvalidOperationException("The selected capture window identity changed.");
                    }

                    _ = await signal.Reader.ReadAsync(token).ConfigureAwait(false);

                    if (sourceClosed)
                    {
                        break;
                    }

                    Direct3D11CaptureFrame? latestFrame = null;
                    EncodedFrame? encoded = null;
                    var nextPoolSize = poolSize;
                    try
                    {
                        // Always drain to the newest frame. This keeps API latency from creating
                        // a stale-frame backlog when classification is slower than the source.
                        while (framePool.TryGetNextFrame() is { } pendingFrame)
                        {
                            latestFrame?.Dispose();
                            latestFrame = pendingFrame;
                        }

                        if (latestFrame is null)
                        {
                            continue;
                        }

                        var nowTimestamp = Stopwatch.GetTimestamp();
                        if (lastSampleTimestamp != 0 &&
                            Stopwatch.GetElapsedTime(lastSampleTimestamp, nowTimestamp) < SampleInterval)
                        {
                            continue;
                        }

                        var contentSize = latestFrame.ContentSize;
                        if (contentSize.Width <= 0 || contentSize.Height <= 0)
                        {
                            continue;
                        }

                        token.ThrowIfCancellationRequested();
                        encoded = await EncodeJpegAsync(latestFrame, token).ConfigureAwait(false);
                        lastSampleTimestamp = Stopwatch.GetTimestamp();
                        if (contentSize.Width != poolSize.Width || contentSize.Height != poolSize.Height)
                        {
                            nextPoolSize = contentSize;
                        }
                    }
                    finally
                    {
                        latestFrame?.Dispose();
                    }

                    if (encoded is null)
                    {
                        continue;
                    }

                    var output = CapturedFrame.FromOwnedBytes(
                        DateTimeOffset.UtcNow,
                        encoded.Width,
                        encoded.Height,
                        "image/jpeg",
                        encoded.Bytes,
                        encoded.LikelyBlack);

                    try
                    {
                        if (nextPoolSize.Width != poolSize.Width || nextPoolSize.Height != poolSize.Height)
                        {
                            poolSize = nextPoolSize;
                            framePool.Recreate(
                                device,
                                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                                2,
                                poolSize);
                        }

                        AddToHistory(output);
                    }
                    catch
                    {
                        output.Dispose();
                        throw;
                    }

                    yield return output;
                }
            }
            finally
            {
                signal.Writer.TryComplete();

                if (framePool is not null)
                {
                    framePool.FrameArrived -= OnFrameArrived;
                }

                item.Closed -= OnSourceClosed;
                session?.Dispose();
                framePool?.Dispose();
                (device as IDisposable)?.Dispose();
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        await _captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_historySync)
            {
                while (_history.TryDequeue(out var frame))
                {
                    frame.Dispose();
                }
            }
        }
        finally
        {
            _captureGate.Release();
            _lifetime.Dispose();
        }
    }

    private bool IsExpectedTargetWindow() =>
        _target.ProcessId == 0
            ? WindowCaptureCatalog.Exists(_windowHandle)
            : WindowCaptureCatalog.IsSameWindow(_target);

    private static async ValueTask<EncodedFrame> EncodeJpegAsync(
        Direct3D11CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var softwareBitmap = await SoftwareBitmap
            .CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore);

        cancellationToken.ThrowIfCancellationRequested();
        var (outputWidth, outputHeight) = CalculateOutputSize(
            softwareBitmap.PixelWidth,
            softwareBitmap.PixelHeight);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetSoftwareBitmap(softwareBitmap);

        if (outputWidth != softwareBitmap.PixelWidth || outputHeight != softwareBitmap.PixelHeight)
        {
            encoder.BitmapTransform.ScaledWidth = checked((uint)outputWidth);
            encoder.BitmapTransform.ScaledHeight = checked((uint)outputHeight);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();
        cancellationToken.ThrowIfCancellationRequested();

        var likelyBlack = await IsLikelyBlackAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();

        if (stream.Size == 0 || stream.Size > int.MaxValue)
        {
            throw new InvalidOperationException("Windows produced an invalid in-memory JPEG frame.");
        }

        var bytes = new byte[checked((int)stream.Size)];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var loaded = await reader.LoadAsync(checked((uint)bytes.Length));
        if (loaded != bytes.Length)
        {
            throw new InvalidOperationException("Windows did not return the complete in-memory JPEG frame.");
        }

        reader.ReadBytes(bytes);
        return new EncodedFrame(bytes, outputWidth, outputHeight, likelyBlack);
    }

    private static async ValueTask<bool> IsLikelyBlackAsync(InMemoryRandomAccessStream jpegStream)
    {
        jpegStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(jpegStream);
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Min(32u, decoder.PixelWidth),
            ScaledHeight = Math.Min(18u, decoder.PixelHeight),
            InterpolationMode = BitmapInterpolationMode.Linear,
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var pixels = pixelData.DetachPixelData();

        var pixelCount = pixels.Length / 4;
        if (pixelCount == 0)
        {
            return true;
        }

        var sampled = 0;
        var nearBlack = 0;
        long brightnessTotal = 0;

        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 4;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var brightness = ((red * 54) + (green * 183) + (blue * 19)) >> 8;

            sampled++;
            brightnessTotal += brightness;
            if (brightness <= 16)
            {
                nearBlack++;
            }
        }

        var nearBlackRatio = nearBlack / (double)sampled;
        var averageBrightness = brightnessTotal / (double)sampled;
        return nearBlackRatio >= 0.98 && averageBrightness <= 10;
    }

    private static (int Width, int Height) CalculateOutputSize(int sourceWidth, int sourceHeight)
    {
        var scale = Math.Min(
            1d,
            Math.Min(MaximumWidth / (double)sourceWidth, MaximumHeight / (double)sourceHeight));

        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private void AddToHistory(CapturedFrame frame)
    {
        var historyFrame = frame.Clone();
        lock (_historySync)
        {
            if (_history.Count == HistoryCapacity)
            {
                _history.Dequeue().Dispose();
            }

            _history.Enqueue(historyFrame);
        }
    }

    private sealed record EncodedFrame(byte[] Bytes, int Width, int Height, bool LikelyBlack);
}
