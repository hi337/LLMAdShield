namespace LLMAdShield.Core;

public sealed class FrameWindow : IDisposable
{
    private IReadOnlyList<CapturedFrame>? _frames;

    internal FrameWindow(IReadOnlyList<CapturedFrame> ownedFrames)
    {
        ArgumentNullException.ThrowIfNull(ownedFrames);

        if (ownedFrames.Count == 0)
        {
            throw new ArgumentException("A frame window must contain at least one frame.", nameof(ownedFrames));
        }

        for (var index = 1; index < ownedFrames.Count; index++)
        {
            if (ownedFrames[index].CapturedAt < ownedFrames[index - 1].CapturedAt)
            {
                throw new ArgumentException("Frames must be ordered by capture time.", nameof(ownedFrames));
            }
        }

        _frames = ownedFrames;
    }

    public IReadOnlyList<CapturedFrame> Frames =>
        _frames ?? throw new ObjectDisposedException(nameof(FrameWindow));

    public DateTimeOffset StartedAt => Frames[0].CapturedAt;

    public DateTimeOffset EndedAt => Frames[^1].CapturedAt;

    public void Dispose()
    {
        var frames = Interlocked.Exchange(ref _frames, null);
        if (frames is null)
        {
            return;
        }

        foreach (var frame in frames)
        {
            frame.Dispose();
        }
    }
}
