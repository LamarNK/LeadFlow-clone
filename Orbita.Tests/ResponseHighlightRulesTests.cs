using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ResponseHighlightRulesTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void IsHighlighted_MatchesWholeProfile()
    {
        var targets = ResponseHighlightRules.NormalizeTargetsFormValues([new ResponseHighlightTarget(AccountId).ToFormValue()]);

        var highlighted = ResponseHighlightRules.IsHighlighted(
            age: null,
            enabled: true,
            ageBucketsCsv: null,
            accountId: AccountId,
            subProfileId: "sub-profile-1",
            targetsJson: targets,
            out var label);

        Assert.True(highlighted);
        Assert.Equal("Профиль", label);
    }

    [Fact]
    public void IsHighlighted_MatchesOnlySelectedSubProfile()
    {
        var targets = ResponseHighlightRules.NormalizeTargetsFormValues(
            [new ResponseHighlightTarget(AccountId, "sub-profile-1").ToFormValue()]);

        var selected = ResponseHighlightRules.IsHighlighted(
            age: null,
            enabled: true,
            ageBucketsCsv: null,
            accountId: AccountId,
            subProfileId: "sub-profile-1",
            targetsJson: targets,
            out var selectedLabel);
        var other = ResponseHighlightRules.IsHighlighted(
            age: null,
            enabled: true,
            ageBucketsCsv: null,
            accountId: AccountId,
            subProfileId: "sub-profile-2",
            targetsJson: targets,
            out _);

        Assert.True(selected);
        Assert.Equal("Субпрофиль", selectedLabel);
        Assert.False(other);
    }

    [Fact]
    public void GetHighlightLabels_ReturnsEveryMatchingRule()
    {
        var targets = ResponseHighlightRules.NormalizeTargetsFormValues(
            [
                new ResponseHighlightTarget(AccountId).ToFormValue(),
                new ResponseHighlightTarget(AccountId, "sub-profile-1").ToFormValue()
            ]);

        var labels = ResponseHighlightRules.GetHighlightLabels(
            age: 74,
            enabled: true,
            ageBucketsCsv: "63+",
            accountId: AccountId,
            subProfileId: "sub-profile-1",
            targetsJson: targets);

        Assert.Equal(["Возраст: 63+", "Профиль Avito", "Субпрофиль Avito"], labels);
    }
}
