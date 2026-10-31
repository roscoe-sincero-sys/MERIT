using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Intake;

namespace Merit.Application.Services;

public static class EnvironmentalSamples
{
    public static readonly IReadOnlyDictionary<string, string[]> DefaultUnits = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["dustWipe"] = ["ug/ft2"], ["surfaceWipe"] = ["ug/ft2"], ["xrfPaint"] = ["mg/cm2"],
        ["soil"] = ["ppm", "mg/kg"], ["product"] = ["ppm", "mg/kg"], ["water"] = ["ug/L", "mg/L"], ["air"] = ["ug/m3", "mg/m3"],
    };

    public static void Validate(EnvironmentalSampleSave sample, DateOnly today, IReadOnlyDictionary<string, string[]> units)
    {
        var v = new Validator();
        v.When(string.IsNullOrEmpty(sample.SampleType) || !units.TryGetValue(sample.SampleType, out var accepted) || !accepted.Contains(sample.Unit), "unit", "Choose a unit compatible with this sample type.");
        v.When(sample.SampleValue < 0, "sampleValue", "The value cannot be negative.");
        v.When(sample.SampleValue > 999999999999.999999m || decimal.Round(sample.SampleValue, 6) != sample.SampleValue, "sampleValue", "Use up to 12 whole digits and 6 decimal places.");
        v.When(sample.SampleDate == default, "sampleDate", "Enter the sample date.");
        v.When(sample.SampleDate > today, "sampleDate", "The date cannot be in the future.");
        v.When(sample.SampleType is not ("dustWipe" or "surfaceWipe") && !string.IsNullOrEmpty(sample.Surface), "surface", "Use no surface for this sample type; put the component in Room or component.");
        v.MaxLength("notes", sample.Notes, 1000, "Notes");
        v.MaxLength("roomOrComponent", sample.RoomOrComponent, 120, "Room or component");
        v.MaxLength("surface", sample.Surface, 60, "Surface");
        v.ThrowIfInvalid();
    }

    /// <summary>Parse and validate the entire file before any write; stable ids make a repeated submission harmless.</summary>
    public static IReadOnlyList<EnvironmentalSampleSave> ParseCsv(Guid investigationId, Guid importId, string csv,
        DateOnly today, IReadOnlyDictionary<string, string[]> units)
    {
        if (importId == Guid.Empty) throw new ValidationException("file", "Reload the sample import form.");
        var rows = CsvReader.Read(csv, ',');
        if (rows.Count < 2 || rows.Count > 501) throw new ValidationException("file", "Upload 1 to 500 readings with a header row.");
        var headers = rows[0].Select(h => h.Trim().TrimStart('\uFEFF')).ToList();
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count) throw new ValidationException("file", "CSV headers must be unique.");
        int Index(string key) => headers.FindIndex(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase));
        foreach (var key in new[] { "sampleType", "sampleValue", "sampleDate", "unit" })
            if (Index(key) < 0) throw new ValidationException("file", $"The CSV needs a {key} column.");
        var samples = new List<EnvironmentalSampleSave>();
        var errors = new List<string>();
        for (int n = 1; n < rows.Count; n++)
        {
            var row = rows[n];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            string Value(string key) { int i = Index(key); return i >= 0 && i < row.Count ? row[i].Trim() : ""; }
            if (row.Count != headers.Count || !decimal.TryParse(Value("sampleValue"), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                || !DateOnly.TryParseExact(Value("sampleDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { errors.Add($"Row {n + 1}: check its column count, numeric value and yyyy-MM-dd date."); continue; }
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{investigationId:N}:{importId:N}:{n}"));
            var sample = new EnvironmentalSampleSave(new Guid(bytes[..16]), investigationId, Value("sampleType"), Value("roomOrComponent"),
                string.IsNullOrEmpty(Value("surface")) ? null : Value("surface"), value, Value("unit"), date, Value("notes"), false);
            try { Validate(sample, today, units); samples.Add(sample); }
            catch (ValidationException ex) { errors.Add($"Row {n + 1}: {string.Join(" ", ex.Errors.Values.SelectMany(e => e))}"); }
        }
        if (errors.Count > 0) throw new ValidationException("file", string.Join(" ", errors.Take(20)));
        if (samples.Count == 0) throw new ValidationException("file", "The file has no readings.");
        return samples;
    }
}
