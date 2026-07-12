namespace Orbita.Contracts;

public sealed record CandidateMatchProfile(
    string FullName,
    int? Age,
    string City,
    string PhoneNormalized);