using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidateGendersTests
{
    [Theory]
    [InlineData("Мужчина · 54 года · Москва", CandidateGenders.Male)]
    [InlineData("Женщина · 37 лет · Казань", CandidateGenders.Female)]
    [InlineData("курьер", null)]
    public void ParseFromText_DetectsAvitoCardGender(string? text, string? expected) =>
        Assert.Equal(expected, CandidateGenders.ParseFromText(text));

    [Theory]
    [InlineData("male", "Мужчина")]
    [InlineData("female", "Женщина")]
    [InlineData("unknown", "Не указан")]
    [InlineData("", "")]
    public void FormatFilterLabels_AreHumanReadable(string? gender, string expected) =>
        Assert.Equal(expected, CandidateGenders.FormatFilterLabel(gender));

    [Theory]
    [InlineData("male", "Мужчина")]
    [InlineData("female", "Женщина")]
    [InlineData("", "—")]
    public void FormatLabels_AreHumanReadable(string? gender, string expected) =>
        Assert.Equal(expected, CandidateGenders.FormatLabel(gender));
}