using System.Diagnostics.CodeAnalysis;

namespace LLMAdShield.Core;

public sealed class FrameRingBuffer : IDisposable
{
    private readonly object _gate = new();
    private readonly List<CapturedFrame> _frames = [];
    private readonly TimeSpan _retention;
    private readonly int _maximumFrameCount;
    private bool _disposed;

    public FrameRingBuffer(TimeSpan retention, int maximumFrameCount)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameCount);
        _retention = retention;
        _maximumFrameCount = maximumFrameCount;
    }

    public static FrameRingBuffer CreateTenSecondBuffer(int maximumFrameCount = 32) =>
        new(TimeSpan.FromSeconds(10), maximumFrameCount);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _frames.Count;
            }
        }
    }

    public void Add(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_frames.Count > 0 && frame.CapturedAt < _frames[^1].CapturedAt)
            {
                throw new ArgumentException(
                    "Frames must be added in nondecreasing capture-time order.",
                    nameof(frame));
            }

            _frames.Add(frame);
            Prune(frame.CapturedAt);
        }
    }

    public bool TryCreateLatestWindow(
        int frameCount,
        [NotNullWhen(true)] out FrameWindow? window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_frames.Count < frameCount)
            {
                window = null;
                return false;
            }

            var clones = new List<CapturedFrame>(frameCount);
            try
            {
                for (var index = _frames.Count - frameCount; index < _frames.Count; index++)
                {
                    clones.Add(_frames[index].Clone());
                }

                window = new FrameWindow(clones);
                return true;
            }
            catch
            {
                foreach (var clone in clones)
                {
                    clone.Dispose();
                }

                throw;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            DisposeAndClearFrames();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeAndClearFrames();
        }
    }

    private void Prune(DateTimeOffset newestCaptureTime)
    {
        var cutoff = newestCaptureTime - _retention;
        while (_frames.Count > 0 &&
               (_frames[0].CapturedAt < cutoff || _frames.Count > _maximumFrameCount))
        {
            _frames[0].Dispose();
            _frames.RemoveAt(0);
        }
    }

    private void DisposeAndClearFrames()
    {
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }

        _frames.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
