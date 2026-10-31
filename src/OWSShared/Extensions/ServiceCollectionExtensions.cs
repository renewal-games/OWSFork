using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OWSShared.Implementations;
using OWSShared.Interfaces;
using System.Linq;
using Serilog;

namespace OWSShared.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection ConfigureAndValidate<T>(this IServiceCollection @this, IConfiguration config) where T : class => @this
            .Configure<T>(config)
            .PostConfigure<T>(settings =>
            {
                var configErrors = settings.ValidationErrors().ToArray();
                if (configErrors.Any())
                {
                    var aggrErrors = string.Join(",", configErrors);
                    var count = configErrors.Length;
                    var configType = typeof(T).Name;
                    Log.Error($"Found {count} configuration error(s) in {configType}: {aggrErrors}");
                }
            });

        public static IServiceCollection ConfigureAndValidate<T>(this IServiceCollection @this, string name, IConfiguration config) where T : class => @this
            .Configure<T>(name, config)
            .PostConfigure<T>(name, settings =>
            {
                var configErrors = settings.ValidationErrors().ToArray();
                if (configErrors.Any())
                {
                    var aggrErrors = string.Join(",", configErrors);
                    var count = configErrors.Length;
                    var configType = typeof(T).Name;
                    Log.Error($"Found {count} configuration error(s) in {configType}: {aggrErrors}");
                }
            });

        /// <summary>
        /// Binds <see cref="Options.UserSessionCacheOptions"/> and refuses to start the host when they are
        /// unusable (see <see cref="Options.UserSessionCacheOptions.ValidationError"/>).
        /// </summary>
        public static IServiceCollection AddUserSessionCacheOptions(this IServiceCollection @this, IConfiguration config)
        {
            @this.AddOptions<Options.UserSessionCacheOptions>()
                .Bind(config.GetSection(Options.UserSessionCacheOptions.SectionName))
                .Validate(options => options.ValidationError() == null, "UserSessionCacheOptions:SessionTtlSeconds must be greater than 0 while session caching is enabled.")
                .ValidateOnStart();
            return @this;
        }

        public static void ConfigureWritable<T>(this IServiceCollection services, IConfigurationSection section, string file = "appsettings.json") where T : class, new()
        {
            services.Configure<T>(section);
            services.AddSingleton<IWritableOptions<T>>(provider =>
            {
                var configuration = (IConfigurationRoot)provider.GetService<IConfiguration>();
                var environment = provider.GetService<IWebHostEnvironment>();
                var options = provider.GetService<IOptionsMonitor<T>>();
                return new WritableOptions<T>(environment, options, configuration, section.Key, file);
            });
        }
    }
}
