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
    // Production regressions: male diminutives / FIO mislabeled as female
    [InlineData("Сеня", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Рома", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Roma", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Ковалев Игорь Анатольевич", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Серёга", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Серега", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Дима", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Дима Матвиенко", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Матвиенко Дима", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Саня", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Вася", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Коля", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Миша", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Паша", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Серёжа", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Сережа", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Юра", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Лёша", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Андрюша", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Вова", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Толя", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Петя", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Ваня", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Костя", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Боря", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Витя", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Гриша", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("Стёпа", CandidateGenders.Male, CandidateGenderSources.Name)]
    [InlineData("dima", CandidateGenders.Male, CandidateGenderSources.Name)]
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
    public void Resolve_PrefersCardGenderWhenNameIsWeak()
    {
        var result = CandidateGenderResolver.Resolve(
            "Пользователь",
            cardGender: CandidateGenders.Male,
            rawText: null);

        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenderSources.Card, result.Source);
    }

    [Fact]
    public void Resolve_StrongFioOverridesConflictingCardGender()
    {
        var result = CandidateGenderResolver.Resolve(
            "Ковалев Игорь Анатольевич",
            cardGender: CandidateGenders.Female,
            rawText: null);

        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenderSources.Name, result.Source);
    }

    [Fact]
    public void Resolve_StrongFioOverridesConflictingRawTextGender()
    {
        var result = CandidateGenderResolver.Resolve(
            "Ковалев Игорь Анатольевич",
            cardGender: null,
            rawText: "Женщина · 59 лет");

        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenderSources.Name, result.Source);
    }

    [Fact]
    public void Resolve_StrongFemaleFioOverridesConflictingCardMale()
    {
        var result = CandidateGenderResolver.Resolve(
            "Петрова Анна",
            cardGender: CandidateGenders.Male,
            rawText: null);

        Assert.Equal(CandidateGenders.Female, result.Gender);
        Assert.Equal(CandidateGenderSources.Name, result.Source);
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
    public void Resolve_MaleDiminutiveNotFlippedByFemaleMorphology()
    {
        Assert.Equal(CandidateGenders.Male, CandidateGenderResolver.Resolve("Сеня").Gender);
        Assert.Equal(CandidateGenders.Male, CandidateGenderResolver.Resolve("Рома").Gender);
        Assert.Equal(CandidateGenders.Male, CandidateGenderResolver.Resolve("Roma").Gender);
    }

    /// <summary>
    /// Lexicon wrongly marks some male diminutives as female (дима/рома/саня);
    /// curated seeds must win, including when Avito card says «Женщина».
    /// </summary>
    [Theory]
    [InlineData("Сеня")]
    [InlineData("Рома")]
    [InlineData("Roma")]
    [InlineData("Дима")]
    [InlineData("dima")]
    [InlineData("Серёга")]
    [InlineData("Серега")]
    [InlineData("Саня")]
    [InlineData("Дима Матвиенко")]
    [InlineData("Матвиенко Дима")]
    [InlineData("Ковалев Игорь Анатольевич")]
    public void Resolve_KnownMaleDiminutiveOverridesConflictingCard(string name)
    {
        var result = CandidateGenderResolver.Resolve(
            name,
            cardGender: CandidateGenders.Female,
            rawText: "Женщина · 42 года");

        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenderSources.Name, result.Source);
    }

    [Theory]
    [InlineData("Дима", "female", "Женщина · 26 лет")]
    [InlineData("Серёга", "female", "Женщина · 26 лет")]
    [InlineData("Дима Матвиенко", "female", "Женщина · 43 года")]
    [InlineData("Сеня", null, "Женщина · 30 лет")]
    [InlineData("Рома", "female", null)]
    public void Resolve_ProductionRegression_MaleNotStoredAsFemale(
        string fullName,
        string? cardGender,
        string? rawText)
    {
        var result = CandidateGenderResolver.Resolve(fullName, cardGender, rawText);
        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenders.Male, CandidateGenderResolver.ToStoredGender(result));
    }

    [Theory]
    [InlineData("Петрова Анна", "male", null, CandidateGenders.Female)]
    [InlineData("Смирнова", null, "Мужчина · 40 лет", CandidateGenders.Female)]
    [InlineData("Мария", "male", "Мужчина · 25 лет", CandidateGenders.Female)]
    public void Resolve_StrongFemaleNameOverridesConflictingCardMale(
        string fullName,
        string? cardGender,
        string? rawText,
        string expected)
    {
        var result = CandidateGenderResolver.Resolve(fullName, cardGender, rawText);
        Assert.Equal(expected, result.Gender);
        Assert.Equal(CandidateGenderSources.Name, result.Source);
    }

    [Fact]
    public void Resolve_MultilineFullName_DimaMatvienko()
    {
        var result = CandidateGenderResolver.Resolve(
            "Дима\nМатвиенко",
            cardGender: CandidateGenders.Female,
            rawText: "Женщина · 43 года");

        Assert.Equal(CandidateGenders.Male, result.Gender);
        Assert.Equal(CandidateGenderSources.Name, result.Source);
    }

    [Fact]
    public void InferFromFullName_SeedOverridesLexiconFemaleDima()
    {
        // russiannames has n\tдима\tf — seed must still report male
        Assert.Equal(CandidateGenders.Female, RussianNameGenderLexicon.LookupFirstName("дима"));
        Assert.Equal(CandidateGenders.Male, CandidateGenderResolver.InferFromFullName("Дима"));
        Assert.Equal(CandidateGenders.Female, RussianNameGenderLexicon.LookupFirstName("рома"));
        Assert.Equal(CandidateGenders.Male, CandidateGenderResolver.InferFromFullName("Рома"));
        Assert.Equal(CandidateGenders.Female, RussianNameGenderLexicon.LookupFirstName("саня"));
        Assert.Equal(CandidateGenders.Male, CandidateGenderResolver.InferFromFullName("Саня"));
    }

    [Theory]
    [InlineData("оглы", CandidateGenders.Male)]
    [InlineData("уулу", CandidateGenders.Male)]
    [InlineData("кызы", CandidateGenders.Female)]
    public void Resolve_CentralAsiaHonorific(string honorific, string gender)
    {
        var result = CandidateGenderResolver.Resolve($"Али {honorific}");
        Assert.Equal(gender, result.Gender);
    }

    [Fact]
    public void Tokenize_DropsInitials()
    {
        var tokens = CandidateGenderResolver.Tokenize("Петрова С.Я.");
        Assert.Equal(["петрова"], tokens);
    }

    [Fact]
    public void Tokenize_KeepsDiminutivesAndSplitsWhitespace()
    {
        var tokens = CandidateGenderResolver.Tokenize("Дима\nМатвиенко");
        Assert.Equal(["дима", "матвиенко"], tokens);
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

    [Theory]
    [InlineData("Дима", 26)]
    [InlineData("Серёга", 26)]
    [InlineData("Дима Матвиенко", 43)]
    [InlineData("Сеня", 30)]
    [InlineData("Рома", 42)]
    [InlineData("Ковалев Игорь Анатольевич", 59)]
    public void EvaluateCandidate_MaleDiminutive_PassesExcludeFemale_EvenWithFemaleCard(
        string fullName,
        int age)
    {
        var filters = new ResponseCollectionFilters(Enabled: true, ExcludeFemale: true);
        var result = ResponseCollectionFilter.EvaluateCandidate(
            fullName,
            age,
            cardGender: CandidateGenders.Female,
            rawText: $"Женщина · {age} лет",
            filters);

        Assert.True(result.Pass, $"Expected pass for male name «{fullName}», got reject={result.RejectReason}");
    }

    [Fact]
    public void EvaluateCandidate_RealFemale_RejectedWhenExcludeFemale()
    {
        var filters = new ResponseCollectionFilters(Enabled: true, ExcludeFemale: true);
        var result = ResponseCollectionFilter.EvaluateCandidate(
            "Петрова Анна",
            age: 30,
            cardGender: CandidateGenders.Female,
            rawText: "Женщина · 30 лет",
            filters);

        Assert.False(result.Pass);
        Assert.Equal(ResponseCollectionFilterReasons.GenderFemale, result.RejectReason);
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
