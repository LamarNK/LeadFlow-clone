using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class WorkerReleasePackageValidatorTests
{
    [Fact]
    public void LooksLikeMsi_ReturnsFalse_ForEmptyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orbita-test-{Guid.NewGuid():N}.msi");
        try
        {
            File.WriteAllBytes(path, [0x00, 0x01]);
            Assert.False(WorkerReleasePackageValidator.LooksLikeMsi(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LooksLikeMsi_ReturnsTrue_ForOleHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orbita-test-{Guid.NewGuid():N}.msi");
        try
        {
            File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00]);
            Assert.True(WorkerReleasePackageValidator.LooksLikeMsi(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}