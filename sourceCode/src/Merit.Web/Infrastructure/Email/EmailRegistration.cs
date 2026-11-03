using System.Security.Cryptography;
using Merit.Application.Email;
using Merit.Application.Security;
using Merit.Application.Services;
using Microsoft.Extensions.Options;

namespace Merit.Web.Infrastructure.Email;

public static class EmailRegistration
{
    /// <summary>
    /// The mail sender for the configured provider (Disabled unless Email:Provider is Smtp) and the one-time code key.
    /// The key (EmailCodes:VerifierKey, base64, 32 bytes or more) must be the same on every web copy and is required
    /// outside Development whenever mail is configured. In Development a missing key is replaced by a random one for
    /// this process, so codes do not survive a restart.
    /// </summary>
    public static IServiceCollection AddMeritEmail(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();
        services.Configure<EmailCodeOptions>(configuration.GetSection(EmailCodeOptions.Section));

        var provider = configuration.GetSection(EmailOptions.Section).GetValue(nameof(EmailOptions.Provider), EmailProvider.Disabled);
        if (provider == EmailProvider.Smtp)
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, DisabledEmailSender>();
        }

        string? configured = configuration.GetSection(EmailCodeOptions.Section)[nameof(EmailCodeOptions.VerifierKey)];
        byte[] key;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                key = Convert.FromBase64String(configured);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("EmailCodes:VerifierKey must be base64.", ex);
            }

            if (key.Length < HmacOneTimeCodeHasher.MinimumKeyBytes)
            {
                throw new InvalidOperationException($"EmailCodes:VerifierKey must decode to at least {HmacOneTimeCodeHasher.MinimumKeyBytes} bytes.");
            }
        }
        else if (provider == EmailProvider.Smtp && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "EmailCodes:VerifierKey is required when Email:Provider is Smtp. Set it from a secret store or the environment " +
                "(EmailCodes__VerifierKey) to the same base64 value of at least 32 random bytes on every web server.");
        }
        else
        {
            key = RandomNumberGenerator.GetBytes(HmacOneTimeCodeHasher.MinimumKeyBytes);
        }

        services.AddSingleton<IOneTimeCodeHasher>(new HmacOneTimeCodeHasher(key));
        return services;
    }
}
