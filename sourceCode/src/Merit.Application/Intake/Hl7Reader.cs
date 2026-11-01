using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Merit.Domain.Identifiers;

namespace Merit.Application.Intake;

/// <summary>An actionable location only: never include source values or patient data in parsing errors.</summary>
public sealed class Hl7ParseException(string message) : Exception(message);

public sealed record Hl7PatientIdentifier(string Type, string Value, string? Authority);

/// <summary>
/// HL7 ER7 ORU^R01 intake for 2.3.1 and 2.5.1. Emits one staging row per OBX-5 repetition.
/// Parsing is all-or-nothing; clinical validation and person matching stay in IntakeProcessor.
/// Raw input remains in the batch, with message/order/observation coordinates in each reported snapshot.
/// </summary>
public static class Hl7Reader
{
    public static IReadOnlyList<Dictionary<string, string?>> Read(string content, string? senderClia = null)
    {
        var rows = new List<Dictionary<string, string?>>();
        var message = new List<string>();
        int number = 0;
        foreach (var source in content.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = source.Trim('\u000b', '\u001c'); // File exports may retain MLLP frame boundaries.
            if (line.Length == 0) continue;
            if (line.Length > 65536) throw new Hl7ParseException("HL7 segment exceeds the 65,536-character intake limit.");
            var tag = line.Length >= 3 ? line[..3] : "";
            if (tag is "FHS" or "BHS" or "BTS" or "FTS") continue;
            if (tag == "MSH")
            {
                if (message.Count > 0) ParseMessage(message, number, senderClia, rows);
                message = [];
                number++;
            }
            if (number == 0) throw new Hl7ParseException("HL7 file must begin with an MSH message header (optional batch envelopes are allowed).");
            message.Add(line);
        }
        if (message.Count > 0) ParseMessage(message, number, senderClia, rows);
        if (rows.Count == 0) throw new Hl7ParseException("HL7 file contains no OBX observations.");
        return rows;
    }

    private static void ParseMessage(List<string> lines, int number, string? senderClia, List<Dictionary<string, string?>> output)
    {
        void Fail(string reason) => throw new Hl7ParseException($"HL7 message {number}: {reason}");
        var header = lines[0];
        if (header.Length < 9) Fail("MSH is incomplete.");
        char field = header[3];
        var parts = header.Split(field);
        if (parts.Length < 12 || parts[1].Length != 4) Fail("MSH-2 must declare four encoding characters and MSH-12 must declare a version.");
        var delimiters = new[] { field }.Concat(parts[1]).ToArray();
        if (delimiters.Distinct().Count() != 5 || delimiters.Any(c => !char.IsAscii(c) || char.IsLetterOrDigit(c) || char.IsControl(c) || char.IsWhiteSpace(c)))
            Fail("MSH delimiters must be five distinct printable punctuation characters.");
        var syntax = new Syntax(field, parts[1][0], parts[1][1], parts[1][2], parts[1][3], number);
        Segment msh = new(header, syntax, true);
        var kind = syntax.Components(msh.Raw(9));
        if (kind.ElementAtOrDefault(0) != "ORU" || kind.ElementAtOrDefault(1) != "R01"
            || (kind.Length > 2 && kind[2] is not ("" or "ORU_R01"))) Fail("MSH-9 must be ORU^R01 (ORU_R01).");
        var version = msh.Component(12);
        if (version is not ("2.3.1" or "2.5.1")) Fail("MSH-12 must be version 2.3.1 or 2.5.1.");
        if (msh.Text(10).Length == 0) Fail("MSH-10 message control ID is required.");
        if (msh.Text(18) is not ("" or "ASCII" or "UNICODE UTF-8" or "UTF-8")) Fail("MSH-18 character set must be ASCII or UTF-8.");
        var sendingFacility = msh.Component(4);
        if (ProviderIdentifiers.IsValidClia(sendingFacility) && !string.IsNullOrEmpty(senderClia)
            && !string.Equals(sendingFacility, senderClia, StringComparison.OrdinalIgnoreCase)) Fail("MSH-4 laboratory does not match the selected sender profile.");

        Segment? patient = null, orc = null;
        Order? order = null;
        var orders = new List<Order>();
        int patients = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length < 4 || line[3] != field || !line[..3].All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))) Fail("A segment is malformed or uses a different field separator.");
            var segment = new Segment(line, syntax);
            switch (segment.Tag)
            {
                case "PID":
                    if (patient is not null && (order is null || order.Patient != patient)) Fail("PID has no associated OBR/OBX group.");
                    patient = segment;
                    order = null;
                    orc = null;
                    patients++;
                    break;
                case "ORC": orc = segment; break;
                case "OBR":
                    if (patient is null) Fail("OBR requires a preceding PID.");
                    order = new Order(patient!, segment, orc);
                    orders.Add(order);
                    break;
                case "OBX":
                    if (order is null) Fail("OBX requires a preceding PID and OBR.");
                    order!.Observations.Add(segment);
                    break;
                case "SPM":
                    if (order is null) Fail("SPM requires a preceding OBR.");
                    if (order!.Specimen is not null) Fail("Multiple SPM specimens per OBR are ambiguous; split them into separate OBR groups.");
                    order.Specimen = segment;
                    break;
                case "DSC": case "ADD": Fail("Continuation messages must be assembled into complete messages before upload."); break;
                // Additional segments and all repetitions remain available in the unchanged batch source.
            }
        }
        if (patients == 0 || orders.Count == 0 || order is null) Fail("A PID and at least one OBR/OBX group are required.");
        int orderIndex = 0;
        foreach (var group in orders)
        {
            orderIndex++;
            if (group.Observations.Count == 0) Fail("An OBR group has no OBX observations.");
            var observationKeys = new HashSet<string>(StringComparer.Ordinal);
            var pid = group.Patient;
            var names = syntax.Repetitions(pid.Raw(5));
            var name = names.FirstOrDefault(n => syntax.Components(n).ElementAtOrDefault(6) == "L") ?? names[0];
            var addresses = syntax.Repetitions(pid.Raw(11));
            var address = addresses.FirstOrDefault(a => syntax.Components(a).ElementAtOrDefault(6) == "H") ?? addresses[0];
            string RawName(int index) => syntax.Components(name).ElementAtOrDefault(index) ?? "";
            string RawAddress(int index) => syntax.Components(address).ElementAtOrDefault(index) ?? "";
            string Name(int index) => syntax.Value(RawName(index));
            string Address(int index) => syntax.Value(RawAddress(index));
            var identifiers = syntax.Repetitions(pid.Raw(3)).Where(s => s.Length > 0).Select(raw =>
            {
                var cx = syntax.Components(raw);
                string Part(int index) => syntax.Value(cx.ElementAtOrDefault(index) ?? "");
                var type = Part(4) switch { "MR" => "mrn", "MA" => "medicaid", "SS" => "other", _ => "other" };
                return new Hl7PatientIdentifier(type, Part(0), Part(3).Length > 0 ? Part(3) : sendingFacility);
            }).Where(i => i.Value.Length > 0).ToArray();
            if (identifiers.Any(i => i.Value.Length > 60 || i.Authority?.Length > 120)) Fail("PID-3 identifier or assigning authority exceeds MERIT's identifier limits.");
            int observationIndex = 0;
            foreach (var observation in group.Observations)
            {
                observationIndex++;
                var values = syntax.Repetitions(observation.Raw(5));
                foreach (var (rawValue, repetition) in values.Select((v, i) => (v, i + 1)))
                {
                    if (output.Count >= 10000) Fail("The file exceeds the 10,000-observation intake limit; split the file.");
                    var valueType = observation.Text(2);
                    string value;
                    if (valueType == "SN")
                    {
                        var sn = syntax.Components(rawValue);
                        var comparator = syntax.Value(sn.ElementAtOrDefault(0) ?? "");
                        if (comparator is not ("" or "=" or "<" or ">") || sn.Skip(2).Any(s => s.Length > 0))
                            Fail("OBX-5 structured numeric ranges, ratios, and non-scalar comparators are unsupported.");
                        value = (comparator == "=" ? "" : comparator) + syntax.Value(sn.ElementAtOrDefault(1) ?? "");
                    }
                    else if (valueType is "NM" or "ST" or "TX") value = syntax.Value(rawValue);
                    else { Fail("OBX-2 must be NM, SN, ST, or TX; other observation types require a separate interface mapping."); return; }
                    var status = observation.Text(11) switch
                    {
                        "F" => "final", "P" or "R" or "S" or "I" => "preliminary", "C" => "corrected", "D" or "X" => "cancelled", _ => "",
                    };
                    if (status.Length == 0) Fail("OBX-11 status is missing or unsupported; supply F, P, R, S, I, C, D, or X with a result value.");
                    var coding = syntax.Components(observation.Raw(3));
                    var loinc = coding.ElementAtOrDefault(2) is "LN" or "LOINC" ? observation.Component(3)
                        : coding.ElementAtOrDefault(5) is "LN" or "LOINC" ? observation.Component(3, 4)
                        : observation.Component(3, 3) + ":" + observation.Component(3);
                    var observationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new[] { loinc, observation.Text(4), repetition.ToString(CultureInfo.InvariantCulture) }))));
                    if (!observationKeys.Add(observationKey)) Fail("Repeated OBX identifiers in one OBR require distinct OBX-4 observation sub-IDs.");
                    var specimen = group.Specimen?.Component(4) ?? group.Request.Component(15);
                    var sampleType = specimen?.ToUpperInvariant() switch { "BLDV" or "VEN" => "venous", "BLDC" or "CAP" => "capillary", _ => "unknown" };
                    var performingClia = observation.Component(23, 10);
                    var county = Address(8);
                    var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["lastName"] = syntax.Value(RawName(0).Split(syntax.Subcomponent)[0]), ["firstName"] = Name(1), ["middleName"] = Name(2),
                        ["dob"] = Date(pid.Component(7)), ["sex"] = pid.Text(8) switch { "M" => "male", "F" => "female", _ => "unknown" },
                        ["addressLine1"] = syntax.Value(RawAddress(0).Split(syntax.Subcomponent)[0]), ["addressUnit"] = Address(1),
                        ["city"] = Address(2), ["state"] = Address(3), ["zip5"] = Address(4).Split('-')[0],
                        ["countyFips"] = county.Length == 5 && county.All(char.IsAsciiDigit) ? county[2..] : county,
                        ["loinc"] = loinc, ["value"] = value, ["unit"] = observation.Component(6), ["sampleType"] = sampleType,
                        ["collectedDate"] = Date(First(group.Specimen?.Component(17), group.Request.Component(7), observation.Component(14))),
                        ["reportedDate"] = Date(First(group.Request.Component(22), msh.Text(7))),
                        ["accessionNumber"] = First(group.Request.Component(3), group.Request.Component(2), group.Control?.Component(3)),
                        ["resultStatus"] = status, ["orderingProviderNpi"] = First(group.Request.Component(16), group.Control?.Component(12)),
                        ["labCliaNumber"] = ProviderIdentifiers.IsValidClia(performingClia) ? performingClia
                            : ProviderIdentifiers.IsValidClia(sendingFacility) ? sendingFacility : null,
                        ["testMethod"] = observation.Component(17) switch { "ICPMS" => "icpMs", "GFAA" => "graphiteFurnaceAa", "ASV" => "anodicStrippingVoltammetry", "LEADCARE" => "pointOfCare", var method => method },
                        ["hl7Version"] = version, ["hl7MessageNumber"] = number.ToString(CultureInfo.InvariantCulture),
                        ["hl7MessageControlId"] = msh.Text(10), ["hl7SendingApplication"] = msh.Component(3), ["hl7SendingFacility"] = sendingFacility,
                        ["hl7OrderSetId"] = group.Request.Text(1), ["hl7ObservationSetId"] = observation.Text(1),
                        ["hl7ObservationSubId"] = observation.Text(4), ["hl7ValueRepetition"] = repetition.ToString(CultureInfo.InvariantCulture),
                        ["hl7ObservationIdentifier"] = observation.Raw(3),
                        ["hl7ObservationKey"] = observationKey,
                        ["hl7OrderIndex"] = orderIndex.ToString(CultureInfo.InvariantCulture), ["hl7ObservationIndex"] = observationIndex.ToString(CultureInfo.InvariantCulture),
                        ["hl7PatientIdentifiersJson"] = JsonSerializer.Serialize(identifiers),
                        ["hl7PatientNames"] = pid.Raw(5), ["hl7PatientAddresses"] = pid.Raw(11), ["hl7SpecimenId"] = group.Specimen?.Raw(2),
                        ["hl7SpecimenType"] = specimen,
                    };
                    // Bound values destined for SQL columns and held-record diagnostics before any rows are saved.
                    foreach (var (key, limit) in new[] { ("value", 60), ("unit", 20), ("testMethod", 30), ("accessionNumber", 60),
                                 ("loinc", 200), ("dob", 200), ("collectedDate", 200), ("reportedDate", 200) })
                        if (fields[key]?.Length > limit) Fail($"Mapped {key} exceeds the {limit}-character intake limit.");
                    output.Add(fields);
                }
            }
        }
    }

    private static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
    private static string Date(string value) => value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : value;

    private sealed class Order(Segment patient, Segment request, Segment? control)
    {
        public Segment Patient { get; } = patient;
        public Segment Request { get; } = request;
        public Segment? Control { get; } = control;
        public Segment? Specimen { get; set; }
        public List<Segment> Observations { get; } = [];
    }

    private sealed class Segment(string line, Syntax syntax, bool header = false)
    {
        private readonly string[] fields = line.Split(syntax.Field);
        public string Tag => fields[0];
        public string Raw(int number) => fields.ElementAtOrDefault(header ? number - 1 : number) ?? "";
        public string Text(int number) => syntax.Value(Raw(number));
        public string Component(int number, int component = 1) => syntax.Value(syntax.Components(syntax.Repetitions(Raw(number))[0]).ElementAtOrDefault(component - 1) ?? "");
    }

    private sealed class Syntax(char field, char component, char repetition, char escape, char subcomponent, int number)
    {
        public char Field => field;
        public char Subcomponent => subcomponent;
        public string[] Components(string value) => value.Split(component);
        public string[] Repetitions(string value) => value.Split(repetition);
        public string Value(string value)
        {
            if (value == "\"\"") return "";
            var output = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != escape) { output.Append(value[i]); continue; }
                int end = value.IndexOf(escape, i + 1);
                if (end < 0) throw new Hl7ParseException($"HL7 message {number}: unterminated escape sequence in a mapped field.");
                var token = value[(i + 1)..end];
                string decoded;
                switch (token)
                {
                    case "F": decoded = field.ToString(); break;
                    case "S": decoded = component.ToString(); break;
                    case "R": decoded = repetition.ToString(); break;
                    case "T": decoded = subcomponent.ToString(); break;
                    case "E": decoded = escape.ToString(); break;
                    case "H": case "N": decoded = ""; break;
                    case ".br": decoded = "\n"; break;
                    default:
                        try
                        {
                            if (!token.StartsWith('X')) throw new FormatException();
                            decoded = new UTF8Encoding(false, true).GetString(Convert.FromHexString(token[1..]));
                        }
                        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
                        { throw new Hl7ParseException($"HL7 message {number}: unsupported or invalid escape sequence in a mapped field."); }
                        break;
                }
                output.Append(decoded);
                i = end;
            }
            return output.ToString().Trim();
        }
    }
}
