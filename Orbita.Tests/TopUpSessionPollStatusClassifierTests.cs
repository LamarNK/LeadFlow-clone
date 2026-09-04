using Orbita.Contracts;
using Xunit;

namespace Orbita.Tests;

public sealed class TopUpSessionPollStatusClassifierTests
{
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    [InlineData(429)]
    public void FromHttpStatusCode_TransientStatuses_ReturnTransient(int statusCode)
    {
        Assert.Equal(TopUpSessionPollStatus.Transient, TopUpSessionPollStatusClassifier.FromHttpStatusCode(statusCode));
    }

    [Theory]
    [InlineData(404)]
    public void FromHttpStatusCode_NotFound_ReturnsMissing(int statusCode)
    {
        Assert.Equal(TopUpSessionPollStatus.Missing, TopUpSessionPollStatusClassifier.FromHttpStatusCode(statusCode));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(422)]
    public void FromHttpStatusCode_PermanentStatuses_ReturnPermanent(int statusCode)
    {
        Assert.Equal(TopUpSessionPollStatus.Permanent, TopUpSessionPollStatusClassifier.FromHttpStatusCode(statusCode));
    }
}
