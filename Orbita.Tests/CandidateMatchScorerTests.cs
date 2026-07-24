using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidateMatchScorerTests
{
    [Fact]
    public void CalculateScore_SameFullNameDifferentPhone_IsMatch()
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

        Assert.Equal(90, score);
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_SameFullNameNoAgeDifferentPhone_IsMatch()
    {
        // Куприй-case: same person, new phone every day, age missing on Avito card.
        var existing = new CandidateMatchProfile(
            "Куприй Илья Александрович",
            null,
            "Алушта",
            "79885585794");
        var incoming = new CandidateMatchProfile(
            "Куприй Илья Александрович",
            null,
            "Алушта",
            "79883608380");

        var score = CandidateMatchScorer.CalculateScore(existing, incoming);

        Assert.Equal(80, score);
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
    public void CalculateScore_SameFullNameDifferentAge_IsStillMatch()
    {
        var existing = new CandidateMatchProfile("Гор Олег Александрович", 66, "рабочий поселок Чик", "79930099416");
        var incoming = new CandidateMatchProfile("Гор Олег Александрович", 40, "рабочий поселок Чик", "79910001122");

        var score = CandidateMatchScorer.CalculateScore(existing, incoming);

        Assert.Equal(80, score);
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_DifferentFullName_ReturnsZero()
    {
        var existing = new CandidateMatchProfile("Гор Олег Александрович", 66, "Чик", "79930099416");
        var incoming = new CandidateMatchProfile("Иванов Иван Иванович", 66, "Чик", "79930099416");

        Assert.Equal(0, CandidateMatchScorer.CalculateScore(existing, incoming));
        Assert.False(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_LastAndFirstOnly_WithoutAgeOrCity_IsNotMatch()
    {
        var existing = new CandidateMatchProfile("Иванов Иван", null, "", "79930099416");
        var incoming = new CandidateMatchProfile("Иванов Иван", null, "", "79910001122");

        Assert.Equal(0, CandidateMatchScorer.CalculateScore(existing, incoming));
        Assert.False(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_LastAndFirstOnly_WithSameAge_IsMatch()
    {
        var existing = new CandidateMatchProfile("Иванов Иван", 35, "Москва", "79930099416");
        var incoming = new CandidateMatchProfile("Иванов Иван", 35, "Казань", "79910001122");

        var score = CandidateMatchScorer.CalculateScore(existing, incoming);

        Assert.Equal(80, score);
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_LastAndFirstOnly_WithSameCity_IsMatch()
    {
        var existing = new CandidateMatchProfile("Иванов Иван", null, "рабочий поселок Чик", "79930099416");
        var incoming = new CandidateMatchProfile("Иванов Иван", null, "РП Чик", "79910001122");

        var score = CandidateMatchScorer.CalculateScore(existing, incoming);

        Assert.Equal(80, score);
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_SingleNameOnly_WithoutAgeAndCity_IsNotMatch()
    {
        var existing = new CandidateMatchProfile("Иван", null, "", "79930099416");
        var incoming = new CandidateMatchProfile("Иван", null, "", "79910001122");

        Assert.Equal(0, CandidateMatchScorer.CalculateScore(existing, incoming));
        Assert.False(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_SingleNameOnly_WithAgeButNoCity_IsNotMatch()
    {
        var existing = new CandidateMatchProfile("Иван", 35, "", "79930099416");
        var incoming = new CandidateMatchProfile("Иван", 35, "Москва", "79910001122");

        Assert.Equal(0, CandidateMatchScorer.CalculateScore(existing, incoming));
        Assert.False(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_SingleNameOnly_WithSameAgeAndCity_IsMatch()
    {
        var existing = new CandidateMatchProfile("Иван", 35, "Москва", "79930099416");
        var incoming = new CandidateMatchProfile("Иван", 35, "Москва", "79910001122");

        var score = CandidateMatchScorer.CalculateScore(existing, incoming);

        Assert.Equal(90, score);
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }

    [Fact]
    public void CalculateScore_SingleNameOnly_SamePhone_IsMatch()
    {
        var existing = new CandidateMatchProfile("Иван", null, "", "79930099416");
        var incoming = new CandidateMatchProfile("Иван", null, "", "79930099416");

        Assert.Equal(CandidateMatchScorer.MatchScore, CandidateMatchScorer.CalculateScore(existing, incoming));
        Assert.True(CandidateMatchScorer.IsMatch(existing, incoming));
    }
}
