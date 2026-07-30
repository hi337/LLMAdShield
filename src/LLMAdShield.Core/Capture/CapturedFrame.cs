using System.Security.Cryptography;

namespace LLMAdShield.Core;

public sealed class CapturedFrame : IDisposable
{
    private byte[]? _encodedImage;

    public CapturedFrame(
        DateTimeOffset capturedAt,
        int width,
        int height,
        string mediaType,
        ReadOnlySpan<byte> encodedImage,
        bool likelyBlack = false)
        : this(capturedAt, width, height, mediaType, encodedImage.ToArray(), likelyBlack)
    {
    }

    private CapturedFrame(
        DateTimeOffset capturedAt,
        int width,
        int height,
        string mediaType,
        byte[] ownedEncodedImage,
        bool likelyBlack)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The media type must describe an image.", nameof(mediaType));
        }

        if (ownedEncodedImage.Length == 0)
        {
            throw new ArgumentException("A captured frame cannot be empty.", nameof(ownedEncodedImage));
        }

        CapturedAt = capturedAt;
        Width = width;
        Height = height;
        MediaType = mediaType;
        LikelyBlack = likelyBlack;
        _encodedImage = ownedEncodedImage;
    }

    public DateTimeOffset CapturedAt { get; }

    public int Width { get; }

    public int Height { get; }

    public string MediaType { get; }

    public bool LikelyBlack { get; }

    public ReadOnlyMemory<byte> EncodedImage =>
        _encodedImage ?? throw new ObjectDisposedException(nameof(CapturedFrame));

    public static CapturedFrame FromOwnedBytes(
        DateTimeOffset capturedAt,
        int width,
        int height,
        string mediaType,
        byte[] encodedImage,
        bool likelyBlack = false)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);
        return new CapturedFrame(capturedAt, width, height, mediaType, encodedImage, likelyBlack);
    }

    public CapturedFrame Clone() =>
        new(CapturedAt, Width, Height, MediaType, EncodedImage.Span, LikelyBlack);

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _encodedImage, null);
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
