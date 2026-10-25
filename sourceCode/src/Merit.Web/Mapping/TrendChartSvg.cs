using System.Globalization;
using System.Net;
using System.Text;
using Merit.Web.Models;
using Microsoft.AspNetCore.Html;

namespace Merit.Web.Mapping;

/// <summary>
/// Server-rendered SVG trend chart for the person page: values over time with the levels in force as threshold lines.
/// Venous results are filled circles and capillary or unknown are open circles, so the two differ without colour.
/// Every piece of text is HTML-encoded here; numbers are formatted invariantly.
/// </summary>
public static class TrendChartSvg
{
    private const double W = 640, H = 220, L = 48, R = 16, T = 16, B = 32;

    public static IHtmlContent Render(TrendChart chart, string id)
    {
        var pts = chart.Points;
        var minDate = pts.Min(p => p.Date).AddDays(-15);
        var maxDate = pts.Max(p => p.Date).AddDays(15);
        double span = Math.Max(maxDate.DayNumber - minDate.DayNumber, 1);
        double maxValue = (double)Math.Max(pts.Max(p => p.Value), chart.Levels.Count == 0 ? 0 : chart.Levels.Max(l => l.Value));
        double top = maxValue <= 0 ? 1 : Math.Ceiling(maxValue * 1.2);
        double X(DateOnly d) => L + ((d.DayNumber - minDate.DayNumber) / span * (W - L - R));
        double Y(double v) => T + ((1 - (v / top)) * (H - T - B));
        static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        static string E(string s) => WebUtility.HtmlEncode(s);

        var title = $"{chart.AnalyteName} trend in {chart.Unit}: " + string.Join("; ",
            pts.Select(p => $"{p.Date:yyyy-MM-dd} {(p.Operator == "=" ? string.Empty : p.Operator)}{p.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg class=\"merit-chart\" viewBox=\"0 0 {N(W)} {N(H)}\" role=\"img\" aria-labelledby=\"{E(id)}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<title id=\"{E(id)}\">{E(title)}</title>");
        for (int i = 0; i <= 4; i++)
        {
            double v = top * i / 4;
            sb.Append(CultureInfo.InvariantCulture, $"<line class=\"grid\" x1=\"{N(L)}\" x2=\"{N(W - R)}\" y1=\"{N(Y(v))}\" y2=\"{N(Y(v))}\" />");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(L - 6)}\" y=\"{N(Y(v))}\" text-anchor=\"end\" dominant-baseline=\"middle\">{N(v)}</text>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<line class=\"axis\" x1=\"{N(L)}\" x2=\"{N(L)}\" y1=\"{N(T)}\" y2=\"{N(H - B)}\" />");
        sb.Append(CultureInfo.InvariantCulture, $"<line class=\"axis\" x1=\"{N(L)}\" x2=\"{N(W - R)}\" y1=\"{N(H - B)}\" y2=\"{N(H - B)}\" />");
        foreach (var (label, value) in chart.Levels)
        {
            double y = Y((double)value);
            sb.Append(CultureInfo.InvariantCulture, $"<line class=\"level\" x1=\"{N(L)}\" x2=\"{N(W - R)}\" y1=\"{N(y)}\" y2=\"{N(y)}\" />");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(W - R - 4)}\" y=\"{N(y - 4)}\" text-anchor=\"end\">{E(label)}</text>");
        }

        if (pts.Count > 1)
        {
            sb.Append("<polyline class=\"line\" points=\"")
              .AppendJoin(' ', pts.Select(p => $"{N(X(p.Date))},{N(Y((double)p.Value))}"))
              .Append("\" />");
        }

        foreach (var p in pts)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<circle class=\"{(p.SampleType == "venous" ? "venous" : "capillary")}\" cx=\"{N(X(p.Date))}\" cy=\"{N(Y((double)p.Value))}\" r=\"5\" />");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(L)}\" y=\"{N(H - 8)}\">{minDate.ToString("MMM yyyy", CultureInfo.InvariantCulture)}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(W - R)}\" y=\"{N(H - 8)}\" text-anchor=\"end\">{maxDate.ToString("MMM yyyy", CultureInfo.InvariantCulture)}</text>");
        sb.Append("</svg>");
        return new HtmlString(sb.ToString());
    }
}
