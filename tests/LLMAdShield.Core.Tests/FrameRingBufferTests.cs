using LLMAdShield.Core;

namespace LLMAdShield.Core.Tests;

public sealed class FrameRingBufferTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Add_PrunesFramesOlderThanRetentionAndDisposesTheirBytes()
    {
        using var buffer = new FrameRingBuffer(TimeSpan.FromSeconds(10), 32);
        var expired = Frame(Epoch, 1);

        buffer.Add(expired);
        buffer.Add(Frame(Epoch.AddSeconds(11), 2));

        Assert.Equal(1, buffer.Count);
        Assert.Throws<ObjectDisposedException>(() => _ = expired.EncodedImage);
    }

    [Fact]
    public void LatestWindow_HasOrderedOwnedCopiesThatOutliveTheBuffer()
    {
        var buffer = FrameRingBuffer.CreateTenSecondBuffer();
        buffer.Add(Frame(Epoch, 1));
        buffer.Add(Frame(Epoch.AddMilliseconds(500), 2, likelyBlack: true));
        buffer.Add(Frame(Epoch.AddSeconds(1), 3));
        buffer.Add(Frame(Epoch.AddMilliseconds(1500), 4));

        Assert.True(buffer.TryCreateLatestWindow(3, out var window));
        buffer.Dispose();

        using (window)
        {
            Assert.Equal(
                [Epoch.AddMilliseconds(500), Epoch.AddSeconds(1), Epoch.AddMilliseconds(1500)],
                window.Frames.Select(frame => frame.CapturedAt));
            Assert.True(window.Frames[0].LikelyBlack);
            Assert.Equal(new byte[] { 4 }, window.Frames[^1].EncodedImage.ToArray());
        }
    }

    [Fact]
    public void Add_RejectsOutOfOrderFramesWithoutTakingOwnership()
    {
        using var buffer = FrameRingBuffer.CreateTenSecondBuffer();
        buffer.Add(Frame(Epoch.AddSeconds(1), 1));
        using var outOfOrder = Frame(Epoch, 2);

        Assert.Throws<ArgumentException>(() => buffer.Add(outOfOrder));
        Assert.Equal(new byte[] { 2 }, outOfOrder.EncodedImage.ToArray());
    }

    [Fact]
    public void LatestWindow_ReturnsFalseUntilEnoughFramesExist()
    {
        using var buffer = FrameRingBuffer.CreateTenSecondBuffer();
        buffer.Add(Frame(Epoch, 1));
        buffer.Add(Frame(Epoch.AddMilliseconds(500), 2));

        Assert.False(buffer.TryCreateLatestWindow(3, out var window));
        Assert.Null(window);
    }

    private static CapturedFrame Frame(DateTimeOffset timestamp, byte value, bool likelyBlack = false) =>
        new(timestamp, 640, 360, "image/jpeg", new byte[] { value }, likelyBlack);
}
