using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidateGenderResolverTests
{
    [Fact]
    public void Lexicon_IsLoaded()
    {
        Assert.True(RussianNameGenderLexicon.FirstNameCount > 10_000);
        Assert.True(RussianNameGenderLexicon.MiddleNameCount > 10_000);
        Assert.True(RussianNameGenderLexicon.SurnameCount > 50_000);
    }

    [Theory]
    [InlineData("Иванов Иван Иванович", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Петрова Мария Сергеевна", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("Сидоров Никита", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Козлов Илья", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Смирнова Анна", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("Рахимов Фарход", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Каримова Мадина", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("Доржиев Баир", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Петрова", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("dilshod", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Муса", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Нигматуллин Ринат Ахметович", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Петрова С.Я.", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("Петрова C.Я.", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("А.Н. Егорова", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("Николаев С.", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Петракова Зинаида М.", CandidateGenders.Female, CandidateGenderSources.Name)]
    public void Resolve_KnownNames(string fullName, string gender, string source)
    {
        var result = CandidateGenderResolver.Resolve(fullName);
        Assert.Equal(gender, result.Gender);
        Assert.Equal(source, result.Source);
    }

    [Theory]
    [InlineData("Пользователь")]
    [InlineData("User123")]
    [InlineData("")]
    public void Resolve_Unknown_WhenAmbiguous(string fullName)
    {
        var result = CandidateGenderResolver.Resolve(fullName);
        Assert.Equal(CandidateGenders.Unknown, result.Gender);
        Assert.Equal(CandidateGenderSources.Unknown, result.Source);
    }

    [Fact]
    public void Resolve_PrefersCardGenderOverName()
    {
        var result = CandidateGenderResolver.Resolve(
            "Петрова Анна",
            cardGender: CandidateGenders.Male,
            rawText: null);

        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenderSources.Card, result.Source);
    }

    [Fact]
    public void Resolve_UsesRawTextCardWhenNoExplicitCardGender()
    {
        var result = CandidateGenderResolver.Resolve(
            "Пользователь",
            cardGender: null,
            rawText: "Женщина · 37 лет");

        Assert.Equal(CandidateGenders.Female, result.Gender);
        Assert.Equal(CandidateGenderSources.Card, result.Source);
    }

    [Fact]
    public void Tokenize_DropsInitials()
    {
        var tokens = CandidateGenderResolver.Tokenize("Петрова С.Я.");
        Assert.Equal(["петрова"], tokens);
    }
}

public sealed class ResponseCollectionFilterTests
{
    private static readonly ResponseCollectionFilters FiltersOn = new(
        Enabled: true,
        ExcludeFemale: true,
        ExcludeMale: false,
        MaxAgeMaleInclusive: 62,
        MaxAgeFemaleInclusive: 55);

    [Fact]
    public void Evaluate_Disabled_AlwaysPasses()
    {
        var result = ResponseCollectionFilter.Evaluate(
            age: 80,
            resolvedGender: CandidateGenders.Female,
            ResponseCollectionFilters.Disabled);

        Assert.True(result.Pass);
    }

    [Theory]
    [InlineData(62, true)]
    [InlineData(63, false)]
    [InlineData(null, true)]
    public void Evaluate_MaleAgeBoundary(int? age, bool pass)
    {
        var result = ResponseCollectionFilter.Evaluate(age, CandidateGenders.Male, FiltersOn);
        Assert.Equal(pass, result.Pass);
        if (!pass)
        {
            Assert.Equal(ResponseCollectionFilterReasons.AgeAboveMax, result.RejectReason);
        }
    }

    [Theory]
    [InlineData(55, true)]
    [InlineData(56, false)]
    public void Evaluate_FemaleAgeBoundary_WhenNotExcluded(int age, bool pass)
    {
        var filters = new ResponseCollectionFilters(
            Enabled: true,
            ExcludeFemale: false,
            MaxAgeMaleInclusive: 62,
            MaxAgeFemaleInclusive: 55);

        var result = ResponseCollectionFilter.Evaluate(age, CandidateGenders.Female, filters);
        Assert.Equal(pass, result.Pass);
    }

    [Fact]
    public void Evaluate_FemaleRejected_WhenExcludeFemale()
    {
        var result = ResponseCollectionFilter.Evaluate(30, CandidateGenders.Female, FiltersOn);
        Assert.False(result.Pass);
        Assert.Equal(ResponseCollectionFilterReasons.GenderFemale, result.RejectReason);
    }

    [Fact]
    public void Evaluate_MaleRejected_WhenExcludeMale()
    {
        var filters = new ResponseCollectionFilters(Enabled: true, ExcludeMale: true);
        var result = ResponseCollectionFilter.Evaluate(30, CandidateGenders.Male, filters);
        Assert.False(result.Pass);
        Assert.Equal(ResponseCollectionFilterReasons.GenderMale, result.RejectReason);
    }

    [Fact]
    public void Evaluate_UnknownGender_Passes()
    {
        var result = ResponseCollectionFilter.EvaluateCandidate(
            "Пользователь",
            age: 40,
            cardGender: null,
            rawText: null,
            FiltersOn);

        Assert.True(result.Pass);
    }

    [Fact]
    public void Evaluate_UnknownGender_AgeRejectedOnlyIfAboveBothLimits()
    {
        var filters = new ResponseCollectionFilters(
            Enabled: true,
            MaxAgeMaleInclusive: 50,
            MaxAgeFemaleInclusive: 55);

        Assert.True(ResponseCollectionFilter.Evaluate(52, CandidateGenders.Unknown, filters).Pass);
        Assert.False(ResponseCollectionFilter.Evaluate(60, CandidateGenders.Unknown, filters).Pass);
    }

    [Fact]
    public void Evaluate_FemaleFio_Rejected()
    {
        var result = ResponseCollectionFilter.EvaluateCandidate(
            "Петрова Анна Ивановна",
            age: 40,
            cardGender: null,
            rawText: null,
            FiltersOn);

        Assert.False(result.Pass);
        Assert.Equal(ResponseCollectionFilterReasons.GenderFemale, result.RejectReason);
    }

    [Fact]
    public void Evaluate_RussianNamesStyleMale_PassesWhenExcludeFemale()
    {
        var filters = new ResponseCollectionFilters(Enabled: true, ExcludeFemale: true);
        var result = ResponseCollectionFilter.EvaluateCandidate(
            "Нигматуллин Ринат Ахметович",
            age: 35,
            cardGender: null,
            rawText: null,
            filters);

        Assert.True(result.Pass);
    }

    [Fact]
    public void Normalize_ClampsInvalidMaxAge()
    {
        var filters = ResponseCollectionFilters.Normalize(true, true, false, 999, 50);
        Assert.Null(filters.MaxAgeMaleInclusive);
        Assert.Equal(50, filters.MaxAgeFemaleInclusive);

        filters = ResponseCollectionFilters.NormalizeLegacy(true, true, 62);
        Assert.Equal(62, filters.MaxAgeMaleInclusive);
        Assert.Equal(62, filters.MaxAgeFemaleInclusive);
    }

    [Fact]
    public void NormalizeLegacy_PrefersPerGenderOverShared()
    {
        var filters = ResponseCollectionFilters.NormalizeLegacy(
            enabled: true,
            excludeFemale: false,
            maxAgeInclusive: 70,
            excludeMale: false,
            maxAgeMaleInclusive: 55,
            maxAgeFemaleInclusive: 60);

        Assert.Equal(55, filters.MaxAgeMaleInclusive);
        Assert.Equal(60, filters.MaxAgeFemaleInclusive);
    }
}
