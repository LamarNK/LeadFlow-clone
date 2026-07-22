using System.Text.RegularExpressions;

namespace Orbita.Contracts;

public static class CandidateGenderSources
{
    public const string Card = "card";
    public const string Name = "name";
    public const string Unknown = "unknown";
}

public readonly record struct CandidateGenderResolution(string Gender, string Source)
{
    public static CandidateGenderResolution Unknown { get; } =
        new(CandidateGenders.Unknown, CandidateGenderSources.Unknown);

    public string? StoredGender =>
        Gender is CandidateGenders.Male or CandidateGenders.Female ? Gender : string.Empty;
}

/// <summary>
/// Gender resolution for Avito response cards.
/// Prefer explicit card labels; then embedded russiannames lexicon (names/midnames/surnames);
/// then conservative FIO heuristics. Nicknames / unknown → do not reject.
/// </summary>
public static class CandidateGenderResolver
{
    private static readonly HashSet<string> NicknameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "пользователь", "user", "guest", "гость", "аноним", "anonymous", "buyer", "покупатель",
        "кандидат", "candidate", "аккаунт", "account", "клиент", "client", "ник", "nick", "nickname",
        "рабочий", "водитель", "мастер", "сотрудник"
    };

    /// <summary>Seed first names (CA / Buryat / latin) not always present or needed as fallback.</summary>
    private static readonly HashSet<string> MaleFirstNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "александр", "алексей", "анатолий", "андрей", "антон", "артем", "артём", "борис", "вадим",
        "валентин", "валерий", "василий", "виктор", "виталий", "владимир", "владислав", "вячеслав",
        "геннадий", "георгий", "григорий", "даниил", "данил", "данила", "денис", "дмитрий", "евгений",
        "егор", "иван", "игорь", "илья", "кирилл", "константин", "кузьма", "лев", "леонид", "макар",
        "максим", "матвей", "михаил", "никита", "николай", "олег", "павел", "пётр", "петр", "роман",
        "руслан", "семен", "семён", "сергей", "станислав", "степан", "тимофей", "тимур", "федор", "фёдор",
        "фома", "эдуард", "юрий", "ярослав", "лёва", "лева", "саня", "дима", "коля", "вова", "толя",
        "ваня", "петя", "костя", "миша", "паша", "сережа", "серёжа", "глеб", "захар", "тарас", "платон",
        "марк", "назар", "давид", "адам", "савва", "лука",
        // Male diminutives (often missing or wrong-gender in russiannames)
        "сеня", "сенёк", "сенек", "рома", "ромка", "вася", "лёша", "леша", "алёша", "алеша",
        "андрюша", "серёга", "серега", "юра", "жека", "митя", "стёпа", "степа", "тоха", "гоша", "гриша",
        "абдулло", "абдулла", "азиз", "азизбек", "айбек", "айрат", "акбар", "акмал", "али", "алишер",
        "аман", "амир", "анвар", "ахмад", "ахмед", "бахтиёр", "бахтияр", "бобур", "болат", "далер",
        "джамшид", "дилшод", "дониёр", "ерлан", "жамшид", "зафар", "иброхим", "ислом", "карим", "комил",
        "мансур", "мурат", "мурад", "мурод", "муса", "мустафа", "мухаммад", "нурлан", "отабек", "парвиз",
        "рахим", "рустам", "саид", "сардор", "темур", "улугбек", "фарход", "фируз", "хасан", "хусейн",
        "шахзод", "юсуф", "бекзод", "умар", "усман", "иса",
        "баир", "бато", "батыр", "батор", "булат", "даши", "доржи", "жаргал", "цырен", "алдар",
        "армен", "арам", "гарик", "левон", "сурен", "тигран", "магомед", "шамиль", "рамиль", "леван",
        "ali", "alisher", "aziz", "dilshod", "farhod", "jamshid", "rustam", "timur", "umar", "yusuf",
        "magomed", "shamil", "tigran", "armen", "bair", "bulat",
        "roma", "roman", "senya", "sena", "igor", "ivan", "dima", "sanya", "pasha", "misha"
    };

    private static readonly HashSet<string> FemaleFirstNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "александра", "алина", "алиса", "алла", "анастасия", "ангелина", "анна", "валентина", "валерия",
        "варвара", "вера", "вероника", "виктория", "галина", "дарья", "диана", "евгения", "екатерина",
        "елена", "елизавета", "жанна", "зоя", "инна", "ирина", "карина", "кира", "кристина", "ксения",
        "лариса", "лидия", "лилия", "любовь", "людмила", "маргарита", "марина", "мария", "милана",
        "надежда", "наталья", "наталия", "нина", "оксана", "олеся", "ольга", "полина", "раиса", "светлана",
        "софия", "софья", "тамара", "татьяна", "ульяна", "юлия", "яна", "катя", "настя", "лена", "маша",
        "даша", "таня", "оля", "юля", "света", "вика", "ира", "надя", "люба",
        "айгуль", "айнура", "айша", "амина", "гулноза", "гулнора", "дилноза", "зарина", "зухра", "зульфия",
        "камола", "мадина", "малика", "нигора", "нилуфар", "нодира", "парвина", "сабина", "севара", "умида",
        "фарида", "феруза", "шахноза", "гульнара", "айгерим", "асель", "гулчехра",
        "баирма", "дарима", "эржена", "туяна", "ариуна",
        "анаит", "ануш", "гаянэ", "лилит", "мариам", "лейла", "нармин",
        "madina", "malika", "zarina", "dilnoza", "sevara", "feruza", "nigora", "gulnara", "aigul", "anush"
    };

    private static readonly HashSet<string> MaleNamesEndingAYa = new(StringComparer.OrdinalIgnoreCase)
    {
        "илья", "никита", "кузьма", "фома", "данила", "лёва", "лева", "савва", "лука", "муса",
        "мустафа", "иса", "костя", "ваня", "петя", "миша", "паша", "гриша", "гоша", "тоша",
        "боря", "витя", "вова", "толя", "дима", "коля",
        "сеня", "рома", "вася", "лёша", "леша", "алёша", "алеша", "андрюша", "юра", "митя",
        "стёпа", "степа", "саня"
    };

    private static readonly Regex MalePatronymic = new(
        @"(ович|евич|ич)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FemalePatronymic = new(
        @"(овна|евна|ична|инична)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SlavicFemaleLastName = new(
        @"(ова|ева|ёва|ина|ына|ая|ская|цкая)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SlavicMaleLastName = new(
        @"(ов|ев|ёв|ин|ын|ский|цкий)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HasCyrillic = new(
        @"[а-яё]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HasLatinLetter = new(
        @"[a-z]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TokenSplit = new(
        @"[\s.]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static CandidateGenderResolution Resolve(
        string? fullName,
        string? cardGender = null,
        string? rawText = null)
    {
        var fromName = InferFromFullName(fullName);

        var fromCard = CandidateGenders.NormalizeFilterValue(cardGender);
        if (fromCard is CandidateGenders.Male or CandidateGenders.Female)
        {
            // Strong FIO (patronymic / known first+second token) beats a conflicting card label.
            // Fixes cases like «Ковалев Игорь Анатольевич» when Avito/rawText wrongly says «Женщина».
            if (fromName is CandidateGenders.Male or CandidateGenders.Female
                && !string.Equals(fromName, fromCard, StringComparison.Ordinal)
                && HasStrongNameGenderSignal(fullName, fromName))
            {
                return new CandidateGenderResolution(fromName, CandidateGenderSources.Name);
            }

            return new CandidateGenderResolution(fromCard, CandidateGenderSources.Card);
        }

        var fromText = CandidateGenders.ParseFromText(rawText);
        if (fromText is CandidateGenders.Male or CandidateGenders.Female)
        {
            if (fromName is CandidateGenders.Male or CandidateGenders.Female
                && !string.Equals(fromName, fromText, StringComparison.Ordinal)
                && HasStrongNameGenderSignal(fullName, fromName))
            {
                return new CandidateGenderResolution(fromName, CandidateGenderSources.Name);
            }

            return new CandidateGenderResolution(fromText, CandidateGenderSources.Card);
        }

        if (fromName is CandidateGenders.Male or CandidateGenders.Female)
        {
            return new CandidateGenderResolution(fromName, CandidateGenderSources.Name);
        }

        return CandidateGenderResolution.Unknown;
    }

    /// <summary>
    /// True when name gender is backed by patronymic and/or multi-token known first name —
    /// not a single ambiguous nickname token.
    /// </summary>
    internal static bool HasStrongNameGenderSignal(string? fullName, string gender)
    {
        if (gender is not (CandidateGenders.Male or CandidateGenders.Female))
        {
            return false;
        }

        var tokens = Tokenize(fullName);
        if (tokens.Count == 0)
        {
            return false;
        }

        var (surname, firstName, middleName) = AssignParts(tokens);

        if (!string.IsNullOrEmpty(middleName)
            && string.Equals(LookupMiddle(middleName), gender, StringComparison.Ordinal))
        {
            return true;
        }

        // Known first name (incl. single-token diminutives: Сеня, Рома, Roma).
        if (!string.IsNullOrEmpty(firstName)
            && string.Equals(LookupFirst(firstName), gender, StringComparison.Ordinal))
        {
            return true;
        }

        // Surname-only strong morphology (Петрова) — only when single-token surname assignment.
        if (tokens.Count == 1
            && !string.IsNullOrEmpty(surname)
            && string.IsNullOrEmpty(firstName)
            && string.Equals(LookupSurname(surname), gender, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static string? InferFromFullName(string? fullName)
    {
        var tokens = Tokenize(fullName);
        if (tokens.Count == 0)
        {
            return null;
        }

        // Honorifics used in Central Asia full names (as in russiannames).
        if (tokens.Count >= 2)
        {
            var last = tokens[^1];
            if (last is "оглы" or "углы" or "уулу")
            {
                return CandidateGenders.Male;
            }

            if (last is "кызы" or "кизи" or "гызы")
            {
                return CandidateGenders.Female;
            }
        }

        var (surname, firstName, middleName) = AssignParts(tokens);

        // Priority mirrors russiannames intent: midname > first name > surname (strong → weak).
        var fromMiddle = LookupMiddle(middleName);
        if (fromMiddle is not null)
        {
            return fromMiddle;
        }

        var fromFirst = LookupFirst(firstName);
        if (fromFirst is not null)
        {
            return fromFirst;
        }

        var fromSurname = LookupSurname(surname);
        if (fromSurname is not null)
        {
            return fromSurname;
        }

        // Heuristic fallbacks when lexicon miss.
        if (!string.IsNullOrEmpty(middleName))
        {
            if (FemalePatronymic.IsMatch(middleName))
            {
                return CandidateGenders.Female;
            }

            if (MalePatronymic.IsMatch(middleName))
            {
                return CandidateGenders.Male;
            }
        }

        if (!string.IsNullOrEmpty(surname) && HasCyrillic.IsMatch(surname))
        {
            if (SlavicFemaleLastName.IsMatch(surname))
            {
                return CandidateGenders.Female;
            }

            if (string.IsNullOrEmpty(firstName)
                && string.IsNullOrEmpty(middleName)
                && SlavicMaleLastName.IsMatch(surname))
            {
                return CandidateGenders.Male;
            }
        }

        if (!string.IsNullOrEmpty(firstName)
            && HasCyrillic.IsMatch(firstName)
            && LooksFemaleFirstNameMorphology(firstName)
            && !MaleFirstNames.Contains(firstName)
            && !MaleNamesEndingAYa.Contains(firstName))
        {
            return CandidateGenders.Female;
        }

        // Single remaining token that was not classified as first/surname by AssignParts.
        if (tokens.Count == 1)
        {
            var only = tokens[0];
            return LookupFirst(only)
                ?? LookupSurname(only)
                ?? LookupMiddle(only)
                ?? HeuristicSingleToken(only);
        }

        return null;
    }

    /// <summary>Split full name; drop single-letter initials (С.Я. → skipped).</summary>
    public static IReadOnlyList<string> Tokenize(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return [];
        }

        return TokenSplit
            .Split(fullName.Trim())
            .Select(static t => t.Trim().ToLowerInvariant())
            .Where(static t => t.Length > 1)
            .Where(static t => !IsNicknameToken(t))
            .ToArray();
    }

    private static (string? Surname, string? FirstName, string? MiddleName) AssignParts(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 1)
        {
            var only = tokens[0];
            if (RussianNameGenderLexicon.IsKnownFirstName(only) || SeedIsFirstName(only))
            {
                return (null, only, null);
            }

            if (RussianNameGenderLexicon.LookupSurname(only) is not null || LooksLikeSurname(only))
            {
                return (only, null, null);
            }

            return (null, only, null);
        }

        if (tokens.Count == 2)
        {
            var a = tokens[0];
            var b = tokens[1];

            // fm: Иван Петрович
            if (RussianNameGenderLexicon.IsKnownMiddleName(b) || LooksLikeMiddleName(b))
            {
                return (null, a, b);
            }

            // sf preferred when a is surname or b is first name
            var aIsFirst = RussianNameGenderLexicon.IsKnownFirstName(a) || SeedIsFirstName(a);
            var bIsFirst = RussianNameGenderLexicon.IsKnownFirstName(b) || SeedIsFirstName(b);
            var aIsSurname = RussianNameGenderLexicon.LookupSurname(a) is not null || LooksLikeSurname(a);

            if (!aIsFirst && bIsFirst)
            {
                return (a, b, null);
            }

            if (aIsFirst && !bIsFirst)
            {
                return (b, a, null);
            }

            if (aIsSurname)
            {
                return (a, b, null);
            }

            // default Avito style: Фамилия Имя
            return (a, b, null);
        }

        // 3+ tokens
        var last = tokens[^1];
        var first = tokens[0];

        // sfm: Фамилия Имя Отчество
        if (RussianNameGenderLexicon.IsKnownMiddleName(last) || LooksLikeMiddleName(last))
        {
            return (tokens[0], tokens[1], last);
        }

        // fms: Имя Отчество Фамилия
        if (RussianNameGenderLexicon.IsKnownMiddleName(tokens[1]) || LooksLikeMiddleName(tokens[1]))
        {
            return (last, first, tokens[1]);
        }

        if (RussianNameGenderLexicon.IsKnownFirstName(first) || SeedIsFirstName(first))
        {
            return (last, first, tokens[1]);
        }

        // default: surname first name middle
        return (tokens[0], tokens[1], tokens.Count > 2 ? tokens[2] : null);
    }

    private static string? LookupMiddle(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return RussianNameGenderLexicon.LookupMiddleName(token)
            ?? (FemalePatronymic.IsMatch(token)
                ? CandidateGenders.Female
                : MalePatronymic.IsMatch(token)
                    ? CandidateGenders.Male
                    : null);
    }

    private static string? LookupFirst(string? token)
    {
        if (string.IsNullOrEmpty(token) || IsNicknameToken(token))
        {
            return null;
        }

        // Curated seeds win over russiannames (e.g. «рома» is wrongly female in the dataset).
        return SeedLookupFirst(token)
            ?? RussianNameGenderLexicon.LookupFirstName(token);
    }

    private static string? LookupSurname(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        return RussianNameGenderLexicon.LookupSurname(token);
    }

    private static string? SeedLookupFirst(string token)
    {
        if (!HasCyrillic.IsMatch(token) && !HasLatinLetter.IsMatch(token))
        {
            return null;
        }

        if (MaleFirstNames.Contains(token) || MaleNamesEndingAYa.Contains(token))
        {
            return CandidateGenders.Male;
        }

        if (FemaleFirstNames.Contains(token))
        {
            return CandidateGenders.Female;
        }

        return null;
    }

    private static bool SeedIsFirstName(string token) =>
        MaleFirstNames.Contains(token)
        || FemaleFirstNames.Contains(token)
        || MaleNamesEndingAYa.Contains(token);

    private static string? HeuristicSingleToken(string only)
    {
        if (!HasCyrillic.IsMatch(only))
        {
            return null;
        }

        if (SlavicFemaleLastName.IsMatch(only))
        {
            return CandidateGenders.Female;
        }

        if (SlavicMaleLastName.IsMatch(only))
        {
            return CandidateGenders.Male;
        }

        return null;
    }

    private static bool LooksLikeMiddleName(string token) =>
        FemalePatronymic.IsMatch(token) || MalePatronymic.IsMatch(token);

    private static bool LooksLikeSurname(string token) =>
        HasCyrillic.IsMatch(token)
        && (SlavicFemaleLastName.IsMatch(token) || SlavicMaleLastName.IsMatch(token));

    private static bool IsNicknameToken(string token) =>
        NicknameTokens.Contains(token) || token.Length <= 1;

    private static bool LooksFemaleFirstNameMorphology(string firstName)
    {
        if (firstName.Length < 4)
        {
            return false;
        }

        return firstName.EndsWith("ия", StringComparison.OrdinalIgnoreCase)
            || firstName.EndsWith("ья", StringComparison.OrdinalIgnoreCase)
            || firstName.EndsWith("а", StringComparison.OrdinalIgnoreCase)
            || firstName.EndsWith("я", StringComparison.OrdinalIgnoreCase);
    }

    public static string ToStoredGender(CandidateGenderResolution resolution) =>
        resolution.StoredGender ?? string.Empty;
}
