using System.Text.Encodings.Web;
using System.Xml.Linq;
using Merit.Web.Mapping;
using Merit.Web.Models;

namespace Merit.Tests;

public sealed class TrendChartTests
{
    [Fact]
    public void DenseLevelsKeepTheirValuesWithoutOverlappingTextAndPointsExposeExactDetails()
    {
        var id = Guid.NewGuid();
        var chart = new TrendChart("PB", "Lead <blood>", "ug/dL",
            [new ChartPoint(new DateOnly(2026, 7, 1), 45, "venous", "<", id)], [("Case <level> 3.5", 3.5m), ("Action 5", 5m)]);
        using var writer = new StringWriter();
        TrendChartSvg.Render(chart, "trend-test").WriteTo(writer, HtmlEncoder.Default);
        var svg = XElement.Parse(writer.ToString());
        Assert.Equal("group", svg.Attribute("role")?.Value);
        var link = Assert.Single(svg.Elements("a"));
        Assert.Equal($"/results/{id}", link.Attribute("href")?.Value);
        Assert.Contains("2026-07-01: <45 ug/dL; venous", link.Attribute("aria-label")?.Value);
        Assert.Equal("2026-07-01: <45 ug/dL; venous", link.Element("circle")?.Element("title")?.Value);
        var lines = svg.Elements("line").Where(e => e.Attribute("class")?.Value == "level").ToList();
        Assert.Equal(2, lines.Count);
        Assert.NotEqual(lines[0].Attribute("y1")?.Value, lines[1].Attribute("y1")?.Value);
        Assert.Contains("Case <level> 3.5", lines[0].Element("title")?.Value);
        Assert.DoesNotContain(svg.Elements("text"), e => e.Value.Contains("Case <level>"));
        Assert.Contains(svg.Elements("text"), e => e.Value == "Jun 16, 2026");
        Assert.Contains(svg.Elements("text"), e => e.Value == "Jul 16, 2026");
        Assert.DoesNotContain("<blood>", writer.ToString());
    }
}
