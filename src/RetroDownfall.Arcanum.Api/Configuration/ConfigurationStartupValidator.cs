using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Configuration;

/// <summary>
/// Validates the bound <see cref="ArcanumSettings"/> once, before the request
/// pipeline begins serving, and aborts host startup with a clear message when the
/// configuration is semantically invalid. Closes the gap where an invalid
/// <c>arcanum.json</c> booted successfully and only failed later at runtime.
/// </summary>
internal sealed class ConfigurationStartupValidator : IStartupFilter
{

    private readonly IOptions<ArcanumSettings> _options;

    private readonly IConfiguration _configuration;

    private readonly ConfigurationValidator _validator;

    private readonly ILogger<ConfigurationStartupValidator> _logger;

    public ConfigurationStartupValidator(
        IOptions<ArcanumSettings> options,
        IConfiguration configuration,
        ConfigurationValidator validator,
        ILogger<ConfigurationStartupValidator> logger)
    {

        _options = options;

        _configuration = configuration;

        _validator = validator;

        _logger = logger;

    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {

        Result obsolete = _validator.RejectObsoleteKeys(_configuration);

        if (obsolete.IsFailure)
        {

            LogValidationFailure(obsolete.Error);

            throw new ConfigurationValidationException(obsolete.Error);

        }

        Result result = _validator.Validate(_options.Value);

        if (result.IsFailure)
        {

            LogValidationFailure(result.Error);

            throw new ConfigurationValidationException(result.Error);

        }

        return next;

    }

    private void LogValidationFailure(Error error)
    {

        _logger.LogCritical(
            "Arcanum configuration is invalid; aborting startup. {Code}: {Message}",
            error.Code,
            error.Message);

        if (error.Details is { Count: > 0 } details)
        {

            foreach (ConfigurationValidationError detail in details)
            {

                _logger.LogCritical("  - {Pointer}: {Detail}", detail.Pointer, detail.Detail);

            }

        }

    }

}

/// <summary>
/// Thrown during host startup when <see cref="ConfigurationValidator"/> rejects the
/// bound configuration. Aborts the host cleanly (no <c>Environment.FailFast</c>).
/// </summary>
internal sealed class ConfigurationValidationException : InvalidOperationException
{

    public ConfigurationValidationException(Error error)
        : base($"Arcanum configuration validation failed ({error.Code}): {error.Message}")
    {

        Error = error;

    }

    public Error Error { get; }

}
