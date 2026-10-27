using Merit.Application.Extracts;
using Merit.Application.Intake;
using Merit.Application.Jobs;
using Merit.Application.Reference;
using Merit.Application.Security;
using Merit.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Merit.Application;

public static class DependencyInjection
{
    /// <summary>Application services, shared by the web application and the worker. Both register ICurrentSession.</summary>
    public static IServiceCollection AddMeritApplication(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<IReferenceDataProvider, ReferenceDataProvider>();
        services.AddScoped<IThresholdProvider, ThresholdProvider>();
        services.AddScoped<ISettingsProvider, SettingsProvider>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IClock, JurisdictionClock>();

        services.AddScoped<AccountService>();
        services.AddScoped<AuditService>();
        services.AddScoped<SearchService>();
        services.AddScoped<ClassificationService>();
        services.AddScoped<PlaceService>();
        services.AddScoped<PersonService>();
        services.AddScoped<ResultService>();
        services.AddScoped<CaseService>();
        services.AddScoped<InvestigationService>();
        services.AddScoped<QueueService>();
        services.AddScoped<ConfigService>();
        services.AddScoped<AdminService>();
        services.AddScoped<LookupService>();
        services.AddScoped<RegistryService>();
        services.AddScoped<IntakeService>();
        services.AddScoped<IntakeProcessor>();
        services.AddScoped<ExtractService>();
        services.AddScoped<MatchReviewService>();
        services.AddScoped<ClusterService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<ExtractBuilder>();
        services.AddScoped<JobDispatcher>();
        return services;
    }
}
