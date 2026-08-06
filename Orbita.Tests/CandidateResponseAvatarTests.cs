using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidateResponseAvatarTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [Fact]
    public void TryDecode_ValidPng_ReturnsValidatedImageBytes()
    {
        var encoded = Convert.ToBase64String(PngHeader);

        var success = CandidateResponseAvatar.TryDecode("image/png", encoded, out var bytes, out var contentType);

        Assert.True(success);
        Assert.Equal(PngHeader, bytes);
        Assert.Equal("image/png", contentType);
    }

    [Fact]
    public void TryDecode_MismatchedOrUnsupportedImage_ReturnsFalse()
    {
        var mismatched = CandidateResponseAvatar.TryDecode(
            "image/jpeg",
            Convert.ToBase64String(PngHeader),
            out _,
            out _);
        var unsupported = CandidateResponseAvatar.TryDecode(
            "image/svg+xml",
            Convert.ToBase64String("<svg />"u8),
            out _,
            out _);

        Assert.False(mismatched);
        Assert.False(unsupported);
    }
}
