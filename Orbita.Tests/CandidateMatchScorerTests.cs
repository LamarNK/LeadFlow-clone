using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidateMatchScorerTests
{
    [Fact]
    public void CalculateScore_SamePersonDifferentPhone_ReachesThreshold()
    {
        var existing = new CandidateMatchProfile(
            "Гор Олег Александрович",
            66,
            "рабочий поселок Чик",
            "79930099416");
        var incoming = new CandidateMatchProfile(
            "Гор Олег Александрович",
            66,
            "рабочий поселок Чик",
            "79910001122");

        var score = CandidateMatchScorer.CalculateScore(existing, incoming);

        Assert.Equal(70, score);
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_SamePersonDifferentCitySamePhone_IsMatch()
    {
        var existing = new CandidateMatchProfile(
            "Узбеков Шовкат Джумазарович",
            42,
            "Серпухов",
            "79999213355");
        var incoming = new CandidateMatchProfile(
            "Узбеков Шовкат Джумазарович",
            42,
            "Протвино",
            "79999213355");

        Assert.Equal(CandidateMatchScorer.MatchScore, CandidateMatchScorer.CalculateScore(existing, incoming));
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_DifferentAge_IsBelowThreshold()
    {
        var existing = new CandidateMatchProfile("Гор Олег Александрович", 66, "рабочий поселок Чик", "79930099416");
        var incoming = new CandidateMatchProfile("Гор Олег Александрович", 40, "рабочий поселок Чик", "79910001122");

        Assert.Equal(30, CandidateMatchScorer.CalculateScore(existing, incoming));
        Assert.False(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_DifferentFullName_ReturnsZero()
    {
        var existing = new CandidateMatchProfile("Гор Олег Александрович", 66, "Чик", "79930099416");
        var incoming = new CandidateMatchProfile("Иванов Иван Иванович", 66, "Чик", "79930099416");

        Assert.Equal(0, CandidateMatchScorer.CalculateScore(existing, incoming));
    }
}