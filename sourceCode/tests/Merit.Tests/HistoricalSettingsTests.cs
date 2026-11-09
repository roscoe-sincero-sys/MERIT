using System.Reflection;
using Merit.Application;
using Merit.Application.Data;
using Merit.Application.Reference;
using Merit.Application.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Merit.Tests;

public sealed class HistoricalSettingsTests
{
    [Fact]
    public async Task CachedHistoryResolvesEachPeriodWithoutFutureFallbackAndInvalidates()
    {
        var data=DispatchProxy.Create<IConfigData,History>();
        var history=(History)(object)data;
        history.Rows=[new() {SettingKey="adultAgeCutoffYears",SettingValue="18",EffectiveFrom=new(2020,1,1),EffectiveTo=new(2024,12,31)},
            new() {SettingKey="adultAgeCutoffYears",SettingValue="21",EffectiveFrom=new(2025,1,1)}];
        using var cache=new MemoryCache(new MemoryCacheOptions());
        var provider=new SettingsProvider(data,cache,new ScreenUsageTests.Actor(new HashSet<string>()),new Clock());
        Assert.Equal(18,(await provider.GetAsOfAsync(new(2020,8,1))).AdultAgeCutoffYears);
        Assert.Equal(21,(await provider.GetAsync()).AdultAgeCutoffYears);
        Assert.Equal(18,(await provider.GetAsOfAsync(new(2020,8,1))).AdultAgeCutoffYears);
        Assert.Null((await provider.GetAsOfAsync(new(2019,1,1))).Text("adultAgeCutoffYears"));
        Assert.Equal(1,history.Calls);
        provider.Invalidate();
        await provider.GetAsync(); Assert.Equal(2,history.Calls);
    }
    public class History : DispatchProxy
    {
        public IReadOnlyList<SettingRow> Rows {get;set;}=[];
        public int Calls {get;private set;}
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            Assert.Equal(nameof(IConfigData.GetSettingsAsync),method?.Name);
            Assert.Null(args![0]); Assert.Equal(true,args[1]); Calls++;
            return Task.FromResult(Rows);
        }
    }
    private sealed class Clock : IClock
    {
        public DateOnly Today=>new(2026,1,1);
        public TimeZoneInfo Zone=>TimeZoneInfo.Utc;
        public DateTime ToLocal(DateTime utc)=>utc;
    }
}
