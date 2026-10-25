using Merit.Domain.Text;

namespace Merit.Domain.Matching;

/// <summary>How one field of an incoming record compares with a candidate person.</summary>
public enum FieldAgreement
{
    Agree,
    Close,
    Different,
    Missing,
}

public sealed record MatchAddress(string? Line1, string? Unit, string? City, string? State, string? Zip5, bool IsCurrent);

/// <summary>The incoming record's identity, as reported (after any staff correction).</summary>
public sealed record IncomingIdentity(string? LastName, string? FirstName, string? MiddleName, DateOnly? Dob, string? Sex,
                                      MatchAddress? Address);

public sealed record CandidateIdentity(
    Guid PersonId,
    string LastName,
    string? FirstName,
    string? MiddleName,
    DateOnly? Dob,
    string? Sex,
    bool IsPlaceholderName,
    IReadOnlyList<(string LastName, string? FirstName)> Aliases,
    IReadOnlyList<MatchAddress> Addresses);

public sealed record FieldComparison(string Field, string Label, string? Incoming, string? Candidate, FieldAgreement Agreement,
                                     string? Note);

public sealed record CandidateComparison(
    Guid PersonId,
    int Score,
    IReadOnlyList<FieldComparison> Fields,
    string Explanation,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Compares an incoming record with candidate persons field by field for the matching review screen, ranks them, and
/// writes the one-line reason each was suggested. The reviewer decides; nothing here links a record. The safeguards
/// (placeholder names, possible twins) appear as warnings so the reviewer sees why a record was not linked
/// automatically.
/// </summary>
public static class MatchComparer
{
    // Common English given-name equivalents. Each group is one name; matching any two members is "close".
    private static readonly string[][] NicknameGroups =
    [
        ["JONATHAN", "JON", "JOHN", "JOHNNY", "JACK"], ["WILLIAM", "BILL", "BILLY", "WILL", "WILLIE", "LIAM"],
        ["ROBERT", "BOB", "BOBBY", "ROB", "ROBBIE", "BERT"], ["RICHARD", "RICK", "RICKY", "DICK", "RICH"],
        ["MICHAEL", "MIKE", "MICKEY", "MIKEY"], ["JAMES", "JIM", "JIMMY", "JAMIE"], ["JOSEPH", "JOE", "JOEY"],
        ["THOMAS", "TOM", "TOMMY"], ["CHRISTOPHER", "CHRIS", "TOPHER"], ["DANIEL", "DAN", "DANNY"],
        ["ANTHONY", "TONY"], ["NICHOLAS", "NICK", "NICKY"], ["ALEXANDER", "ALEX", "XANDER", "SASHA"],
        ["BENJAMIN", "BEN", "BENNY"], ["SAMUEL", "SAM", "SAMMY"], ["MATTHEW", "MATT"], ["ANDREW", "ANDY", "DREW"],
        ["EDWARD", "ED", "EDDIE", "TED", "TEDDY"], ["CHARLES", "CHARLIE", "CHUCK"], ["STEVEN", "STEPHEN", "STEVE"],
        ["ELIZABETH", "LIZ", "LIZZIE", "BETH", "BETTY", "ELIZA", "LISA"], ["MARGARET", "MAGGIE", "PEGGY", "MEG"],
        ["KATHERINE", "CATHERINE", "KATHRYN", "KATE", "KATIE", "CATHY", "KATHY", "KAT"], ["SUSAN", "SUE", "SUSIE"],
        ["JENNIFER", "JEN", "JENNY"], ["PATRICIA", "PAT", "PATTY", "TRISH"], ["DEBORAH", "DEBRA", "DEB", "DEBBIE"],
        ["REBECCA", "BECKY", "BECCA"], ["VICTORIA", "VICKY", "TORI"], ["ABIGAIL", "ABBY"], ["ISABELLA", "BELLA", "IZZY"],
        ["AMELIA", "AMY", "MILLIE"], ["SAMANTHA", "SAM", "SAMMY"], ["ALEXANDRA", "ALEX", "LEXI", "SASHA"],
        ["JOSEPHINE", "JOSIE", "JO"], ["MARIA", "MARY", "MARIE", "MOLLY"], ["ANA", "ANNA", "ANNIE", "ANN", "ANNE"],
    ];

    private static readonly Dictionary<string, HashSet<int>> GroupsByName = BuildGroups();

    public static IReadOnlyList<CandidateComparison> Rank(IncomingIdentity incoming, IEnumerable<CandidateIdentity> candidates, int max)
        => candidates.Select(c => Compare(incoming, c))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.PersonId)
            .Take(max)
            .ToList();

    public static CandidateComparison Compare(IncomingIdentity incoming, CandidateIdentity candidate)
    {
        var fields = new List<FieldComparison>
        {
            LastName(incoming, candidate),
            FirstName(incoming, candidate),
            MiddleName(incoming, candidate),
            Dob(incoming.Dob, candidate.Dob),
            Sex(incoming.Sex, candidate.Sex),
            Address(incoming.Address, candidate.Addresses),
        };

        var warnings = new List<string>();
        if (MatchKeys.IsPlaceholderName(incoming.FirstName) || candidate.IsPlaceholderName)
        {
            warnings.Add("Placeholder first name (for example BABY BOY): never linked automatically.");
        }

        if (Get(fields, "dob").Agreement == FieldAgreement.Agree && Get(fields, "address").Agreement == FieldAgreement.Agree
            && Get(fields, "firstName").Agreement == FieldAgreement.Different)
        {
            warnings.Add("Possible twin: same date of birth and address with a different first name.");
        }

        return new CandidateComparison(candidate.PersonId, Score(fields), fields, Explain(fields), warnings);
    }

    /// <summary>The key that remembers a Different person decision: last name, first name and DOB keys.</summary>
    public static string IncomingKey(string? lastName, string? firstName, DateOnly? dob)
        => string.Join('|', MatchKeys.NameKey(lastName) ?? string.Empty, MatchKeys.NameKey(firstName) ?? string.Empty,
            dob?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    public static bool AreNicknames(string? a, string? b)
    {
        string? ka = MatchKeys.NameKey(a), kb = MatchKeys.NameKey(b);
        return ka is not null && kb is not null && ka != kb
               && GroupsByName.TryGetValue(ka, out var ga) && GroupsByName.TryGetValue(kb, out var gb) && ga.Overlaps(gb);
    }

    public static bool IsDayMonthSwap(DateOnly a, DateOnly b)
        => a != b && a.Year == b.Year && a.Month == b.Day && a.Day == b.Month;

    private static FieldComparison LastName(IncomingIdentity i, CandidateIdentity c)
    {
        string? ki = MatchKeys.NameKey(i.LastName), kc = MatchKeys.NameKey(c.LastName);
        if (ki is null || kc is null)
        {
            return new("lastName", "Last name", i.LastName, c.LastName, FieldAgreement.Missing, null);
        }

        if (ki == kc)
        {
            return new("lastName", "Last name", i.LastName, c.LastName, FieldAgreement.Agree, null);
        }

        var alias = c.Aliases.FirstOrDefault(a => MatchKeys.NameKey(a.LastName) == ki);
        if (alias.LastName is not null)
        {
            return new("lastName", "Last name", i.LastName, c.LastName, FieldAgreement.Close, $"matches alias \"{alias.LastName}\"");
        }

        // Hyphenated or compound names: one is part of the other.
        if (Math.Min(ki.Length, kc.Length) >= 3 && (ki.Contains(kc, StringComparison.Ordinal) || kc.Contains(ki, StringComparison.Ordinal)))
        {
            return new("lastName", "Last name", i.LastName, c.LastName, FieldAgreement.Close, "one name is part of the other");
        }

        return new("lastName", "Last name", i.LastName, c.LastName, FieldAgreement.Different, null);
    }

    private static FieldComparison FirstName(IncomingIdentity i, CandidateIdentity c)
    {
        string? ki = MatchKeys.NameKey(i.FirstName), kc = MatchKeys.NameKey(c.FirstName);
        if (ki is null || kc is null || MatchKeys.IsPlaceholderName(i.FirstName))
        {
            return new("firstName", "First name", i.FirstName, c.FirstName, FieldAgreement.Missing,
                MatchKeys.IsPlaceholderName(i.FirstName) && ki is not null ? "placeholder name" : null);
        }

        if (ki == kc)
        {
            return new("firstName", "First name", i.FirstName, c.FirstName, FieldAgreement.Agree, null);
        }

        if (AreNicknames(i.FirstName, c.FirstName))
        {
            return new("firstName", "First name", i.FirstName, c.FirstName, FieldAgreement.Close, "nickname");
        }

        if ((ki.Length == 1 && kc.StartsWith(ki, StringComparison.Ordinal)) || (kc.Length == 1 && ki.StartsWith(kc, StringComparison.Ordinal)))
        {
            return new("firstName", "First name", i.FirstName, c.FirstName, FieldAgreement.Close, "initial");
        }

        if (Math.Min(ki.Length, kc.Length) >= 3 && (ki.StartsWith(kc, StringComparison.Ordinal) || kc.StartsWith(ki, StringComparison.Ordinal)))
        {
            return new("firstName", "First name", i.FirstName, c.FirstName, FieldAgreement.Close, "shortened form");
        }

        if (c.Aliases.Any(a => MatchKeys.NameKey(a.FirstName) == ki))
        {
            return new("firstName", "First name", i.FirstName, c.FirstName, FieldAgreement.Close, "matches an alias");
        }

        return new("firstName", "First name", i.FirstName, c.FirstName, FieldAgreement.Different, null);
    }

    private static FieldComparison MiddleName(IncomingIdentity i, CandidateIdentity c)
    {
        string? ki = MatchKeys.NameKey(i.MiddleName), kc = MatchKeys.NameKey(c.MiddleName);
        if (ki is null || kc is null)
        {
            return new("middleName", "Middle name", i.MiddleName, c.MiddleName, FieldAgreement.Missing, null);
        }

        if (ki == kc)
        {
            return new("middleName", "Middle name", i.MiddleName, c.MiddleName, FieldAgreement.Agree, null);
        }

        return ki[0] == kc[0] && (ki.Length == 1 || kc.Length == 1)
            ? new("middleName", "Middle name", i.MiddleName, c.MiddleName, FieldAgreement.Close, "initial")
            : new("middleName", "Middle name", i.MiddleName, c.MiddleName, FieldAgreement.Different, null);
    }

    private static FieldComparison Dob(DateOnly? i, DateOnly? c)
    {
        static string? Text(DateOnly? d) => d?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (i is null || c is null)
        {
            return new("dob", "Date of birth", Text(i), Text(c), FieldAgreement.Missing, null);
        }

        if (i == c)
        {
            return new("dob", "Date of birth", Text(i), Text(c), FieldAgreement.Agree, null);
        }

        if (IsDayMonthSwap(i.Value, c.Value))
        {
            return new("dob", "Date of birth", Text(i), Text(c), FieldAgreement.Close, "day and month swapped");
        }

        string a = Text(i)!, b = Text(c)!;
        int differing = a.Zip(b).Count(p => p.First != p.Second);
        return differing == 1
            ? new("dob", "Date of birth", a, b, FieldAgreement.Close, "one digit different")
            : new("dob", "Date of birth", a, b, FieldAgreement.Different, null);
    }

    private static FieldComparison Sex(string? i, string? c)
    {
        bool unknownI = string.IsNullOrEmpty(i) || i == "unknown", unknownC = string.IsNullOrEmpty(c) || c == "unknown";
        if (unknownI || unknownC)
        {
            return new("sex", "Sex", i, c, FieldAgreement.Missing, null);
        }

        return new("sex", "Sex", i, c, string.Equals(i, c, StringComparison.OrdinalIgnoreCase) ? FieldAgreement.Agree : FieldAgreement.Different,
            null);
    }

    private static FieldComparison Address(MatchAddress? i, IReadOnlyList<MatchAddress> candidate)
    {
        static string Label(MatchAddress a)
            => string.Join(", ", new[] { a.Line1 + (string.IsNullOrEmpty(a.Unit) ? string.Empty : " " + a.Unit), a.City, a.State }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        var current = candidate.FirstOrDefault(a => a.IsCurrent) ?? candidate.FirstOrDefault();
        if (i is null || string.IsNullOrWhiteSpace(i.Line1) || current is null)
        {
            return new("address", "Address", i is null ? null : Label(i), current is null ? null : Label(current), FieldAgreement.Missing, null);
        }

        string key = MatchKeys.AddressKey(i.Line1, i.Unit, i.City, i.State, i.Zip5);
        string building = MatchKeys.BuildingKey(i.Line1, i.City, i.State, i.Zip5);
        string Key(MatchAddress a) => MatchKeys.AddressKey(a.Line1, a.Unit, a.City, a.State, a.Zip5);
        string Building(MatchAddress a) => MatchKeys.BuildingKey(a.Line1, a.City, a.State, a.Zip5);

        if (Key(current) == key)
        {
            return new("address", "Address", Label(i), Label(current), FieldAgreement.Agree, null);
        }

        var past = candidate.FirstOrDefault(a => Key(a) == key);
        if (past is not null)
        {
            return new("address", "Address", Label(i), Label(past), FieldAgreement.Close, "a past or other address");
        }

        var sameBuilding = candidate.FirstOrDefault(a => Building(a) == building);
        return sameBuilding is not null
            ? new("address", "Address", Label(i), Label(sameBuilding), FieldAgreement.Close, "same building, different unit")
            : new("address", "Address", Label(i), Label(current), FieldAgreement.Different, null);
    }

    private static int Score(IReadOnlyList<FieldComparison> fields)
    {
        int score = 0;
        foreach (var f in fields)
        {
            (int agree, int close, int different) = f.Field switch
            {
                "lastName" => (30, 18, -10),
                "firstName" => (30, 18, -20),
                "middleName" => (5, 3, -5),
                "dob" => (40, 22, -30),
                "sex" => (5, 0, -20),
                "address" => (20, 10, 0),
                _ => (0, 0, 0),
            };
            score += f.Agreement switch
            {
                FieldAgreement.Agree => agree,
                FieldAgreement.Close => close,
                FieldAgreement.Different => different,
                _ => 0,
            };
        }

        return score;
    }

    /// <summary>One line: what agrees, then each close field with why, then what differs.</summary>
    private static string Explain(IReadOnlyList<FieldComparison> fields)
    {
        static string Short(FieldComparison f) => f.Field switch
        {
            "dob" => "DOB",
            "lastName" => "last name",
            "firstName" => "first name",
            "middleName" => "middle name",
            _ => f.Label.ToLowerInvariant(),
        };

        var parts = new List<string>();
        var agree = fields.Where(f => f.Agreement == FieldAgreement.Agree && f.Field != "middleName").Select(Short).ToList();
        if (agree.Count > 0)
        {
            parts.Add("same " + JoinAnd(agree));
        }

        parts.AddRange(fields.Where(f => f.Agreement == FieldAgreement.Close).Select(f =>
            $"{Short(f)} \"{f.Incoming}\" vs. \"{f.Candidate}\" ({f.Note})"));

        var different = fields.Where(f => f.Agreement == FieldAgreement.Different && f.Field != "address").Select(Short).ToList();
        if (different.Count > 0)
        {
            parts.Add("different " + JoinAnd(different));
        }

        if (parts.Count == 0)
        {
            return "Suggested by name and date of birth search.";
        }

        string line = string.Join("; ", parts);
        return char.ToUpperInvariant(line[0]) + line[1..] + ".";
    }

    private static string JoinAnd(IReadOnlyList<string> items)
        => items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static FieldComparison Get(IReadOnlyList<FieldComparison> fields, string name) => fields.First(f => f.Field == name);

    private static Dictionary<string, HashSet<int>> BuildGroups()
    {
        var map = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (int g = 0; g < NicknameGroups.Length; g++)
        {
            foreach (var name in NicknameGroups[g])
            {
                if (!map.TryGetValue(name, out var set))
                {
                    map[name] = set = [];
                }

                set.Add(g);
            }
        }

        return map;
    }
}
