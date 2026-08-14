using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ResponseOperatorLocksTests
{
    [Fact]
    public void Contains_EmptyPacked_IsFalse()
    {
        Assert.False(ResponseOperatorLocks.Contains(null, ResponseOperatorLocks.City));
        Assert.False(ResponseOperatorLocks.Contains("", ResponseOperatorLocks.City));
        Assert.False(ResponseOperatorLocks.Contains("vacancy", ResponseOperatorLocks.City));
    }

    [Fact]
    public void Add_ThenContains_IgnoresCaseAndKeepsStableOrder()
    {
        var packed = ResponseOperatorLocks.Add(null, ResponseOperatorLocks.Vacancy);
        packed = ResponseOperatorLocks.Add(packed, ResponseOperatorLocks.City);
        packed = ResponseOperatorLocks.Add(packed, "CITY");

        Assert.Equal("city,vacancy", packed);
        Assert.True(ResponseOperatorLocks.Contains(packed, "City"));
        Assert.True(ResponseOperatorLocks.Contains(packed, ResponseOperatorLocks.Vacancy));
    }
}
