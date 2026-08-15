using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmContractsTests
{
    [Fact]
    public void CloseReasons_Officer_IsAvailableAndValid()
    {
        Assert.Contains(CrmCloseReasons.Officer, CrmCloseReasons.All);
        Assert.True(CrmCloseReasons.IsValid("Офицер"));
    }
}
