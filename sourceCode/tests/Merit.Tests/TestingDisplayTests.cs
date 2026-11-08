using Merit.Application.Data;

namespace Merit.Tests;

public sealed class TestingDisplayTests
{
    [Theory]
    [InlineData(-1,"Overdue")]
    [InlineData(0,"Due today")]
    [InlineData(1,"Due within 7 days")]
    [InlineData(7,"Due within 7 days")]
    [InlineData(8,"Scheduled")]
    public void WarningUsesInclusiveCalendarBoundary(int offset,string expected)
    {
        var today=new DateOnly(2026,12,28);
        Assert.Equal(expected,TestingDisplay.Status("open",today.AddDays(offset),today));
    }

    [Theory]
    [InlineData("done","Completed")]
    [InlineData("dismissed","Dismissed")]
    public void ResolvedPlansNeverCarryOpenWorkWarnings(string status,string expected)
        => Assert.Equal(expected,TestingDisplay.Status(status,new DateOnly(2020,1,1),new DateOnly(2026,11,8)));

    [Fact]
    public void MissingTargetRequiresReviewRatherThanSuggestingNoWork()
        => Assert.Equal("Clinical review required",TestingDisplay.Status("open",null,new DateOnly(2026,11,8)));
}
