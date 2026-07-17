using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidateGenderResolverTests
{
    [Fact]
    public void Lexicon_LoadsEmbeddedRussianNamesResource()
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
    // russiannames-style cases
    [InlineData("Нигматуллин Ринат Ахметович", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Петрова С.Я.", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("Петрова C.Я.", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("А.Н. Егорова", CandidateGenders.Female, CandidateGenderSources.Name)]
    [InlineData("Николаев С.", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Петракова Зинаида М.", CandidateGenders.Female, CandidateGenderSources.Name)]
    public void Resolve_FromFio_ReturnsExpected(string fullName, string gender, string source)
    {
        var result = CandidateGenderResolver.Resolve(fullName);
        Assert.Equal(gender, result.Gender);
        Assert.Equal(source, result.Source);
    }

    [Theory]
    [InlineData("Пользователь")]
    [InlineData("user123")]
    [InlineData("CoolNick")]
    [InlineData("")]
    [InlineData("А")]
    [InlineData("Xyzqwerty")]
    public void Resolve_NicknamesAndAmbiguous_AreUnknown(string fullName)
    {
        var result = CandidateGenderResolver.Resolve(fullName);
        Assert.Equal(CandidateGenders.Unknown, result.Gender);
        Assert.Equal(CandidateGenderSources.Unknown, result.Source);
    }

    [Fact]
    public void Resolve_PrefersCardGenderOverName()
    {
        var result = CandidateGenderResolver.Resolve(
            "Иванова Мария",
            cardGender: CandidateGenders.Male,
            rawText: null);

        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenderSources.Card, result.Source);
    }

    [Fact]
    public void Resolve_UsesRawTextWomanLabel()
    {
        var result = CandidateGenderResolver.Resolve(
            "CoolNick",
            cardGender: null,
            rawText: "Женщина · 37 лет · Москва");

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
        MaxAgeInclusive: 62);

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
    public void Evaluate_AgeBoundary(int? age, bool pass)
    {
        var result = ResponseCollectionFilter.Evaluate(age, CandidateGenders.Male, FiltersOn);
        Assert.Equal(pass, result.Pass);
        if (!pass)
        {
            Assert.Equal(ResponseCollectionFilterReasons.AgeAboveMax, result.RejectReason);
        }
    }

    [Fact]
    public void Evaluate_FemaleRejected_WhenExcludeFemale()
    {
        var result = ResponseCollectionFilter.Evaluate(30, CandidateGenders.Female, FiltersOn);
        Assert.False(result.Pass);
        Assert.Equal(ResponseCollectionFilterReasons.GenderFemale, result.RejectReason);
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
    public void Evaluate_RussianNamesStyleMale_RejectedWhenExcludeFemaleFalseButKnown()
    {
        var filters = new ResponseCollectionFilters(Enabled: true, ExcludeFemale: true, MaxAgeInclusive: null);
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
        var filters = ResponseCollectionFilters.Normalize(true, true, 999);
        Assert.Null(filters.MaxAgeInclusive);

        filters = ResponseCollectionFilters.Normalize(true, true, 62);
        Assert.Equal(62, filters.MaxAgeInclusive);
    }
}
