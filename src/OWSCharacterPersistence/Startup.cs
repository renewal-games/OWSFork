using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using OWSData.Health;
using OWSData.Repositories.Interfaces;
using OWSShared;
using OWSShared.Implementations;
using OWSShared.Interfaces;
using OWSShared.Middleware;
using SimpleInjector;

namespace OWSCharacterPersistence
{
    public class Startup
    {
        //Container container;
        private Container container = new SimpleInjector.Container();

        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            container.Options.ResolveUnregisteredConcreteTypes = false;

            Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddEnvironmentVariables()
                .Build();
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo("./temp/DataProtection-Keys"));
            services.AddHealthChecks().AddOWSPostgres(Configuration);

            services.AddHttpContextAccessor();
            services.AddHttpClient("Discord", client => client.Timeout = TimeSpan.FromSeconds(10));
            services.AddHostedService<Services.StaffAlertsWorker>();
            // The gift alert's threshold; the same key the console reads.
            OWSData.Repositories.Implementations.Postgres.ConsoleQueriesRepository.LargeJuriChange = Configuration.GetValue(
                OWSShared.Options.ManagementOptions.SectionName + ":LargeJuriChange",
                OWSData.Repositories.Implementations.Postgres.ConsoleQueriesRepository.LargeJuriChange);

            services.AddMvcCore(config =>
            {
                config.EnableEndpointRouting = false;
                //IHttpRequestStreamReaderFactory readerFactory = services.BuildServiceProvider().GetRequiredService<IHttpRequestStreamReaderFactory>();
                //config.ModelBinderProviders.Insert(0, new Microsoft.AspNetCore.Mvc.ModelBinding.Binders.BodyModelBinderProvider(config.InputFormatters, readerFactory));
                //config.ModelBinderProviders.Insert(0, new QueryModelBinderProvider(container));
            })
            .AddViews()
            .AddApiExplorer();

            services.AddSimpleInjector(container, options => {
                options.AddAspNetCore()
                    .AddControllerActivation()
                    .AddViewComponentActivation();
                //.AddPageModelActivation()
                //.AddTagHelperActivation();
            });

            services.AddSwaggerGen(c => {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "Open World Server Character Persistence API", Version = "v1" });

                c.AddSecurityDefinition("X-CustomerGUID", new OpenApiSecurityScheme
                {
                    Description = "Authorization header using the X-CustomerGUID scheme",
                    Name = "X-CustomerGUID",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "X-CustomerGUID"
                });

                c.OperationFilter<SwaggerSecurityRequirementsDocumentFilter>();

                var filePath = Path.Combine(System.AppContext.BaseDirectory, "OWSCharacterPersistence.xml");
                c.IncludeXmlComments(filePath);
            });

            services.Configure<OWSShared.Options.StorageOptions>(Configuration.GetSection(OWSShared.Options.StorageOptions.SectionName));
            services.Configure<OWSShared.Options.APIPathOptions>(Configuration.GetSection(OWSShared.Options.APIPathOptions.SectionName));
            services.Configure<OWSShared.Options.RabbitMQOptions>(Configuration.GetSection(OWSShared.Options.RabbitMQOptions.SectionName));
            services.Configure<OWSShared.Options.EconomyOptions>(Configuration.GetSection(OWSShared.Options.EconomyOptions.SectionName));

            InitializeContainer(services);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseSimpleInjector(container);

            app.UseHealthChecks("/health");
            app.UseMiddleware<StoreCustomerGUIDMiddleware>(container);

            // X-CustomerGUID only names the tenant and every game client sends it, and these routes write gold, inventory,
            // positions and the quest catalog by caller-supplied name, so they also need the service key, which the zone
            // server sends on its persistence calls. Unlike InstanceManagement there is no off switch: OWS_REQUIRE_SERVICE_KEY
            // is not read here. A process with no key rejects every caller, except in Development, where it lets them through.
            var serviceKeyGate = new ServiceKeyGateSettings
            {
                Required = true,
                ConfiguredKey = SamsaraServiceKeyCheck.ConfiguredKey,
                IsDevelopment = SamsaraServiceKeyCheck.IsDevelopment
            };
            string serviceKeyWarning = serviceKeyGate.PostureWarning("CharacterPersistence");
            if (serviceKeyWarning != null) Serilog.Log.Warning(serviceKeyWarning);
            app.UseMiddleware<RequireServiceKeyMiddleware>(serviceKeyGate);

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            //app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            //app.UseAuthorization();

            app.UseMvc();

            app.UseSwagger(/*c =>
            {
                c.RouteTemplate =
                    "api-docs/{documentName}/swagger.json";
            }*/);
            app.UseSwaggerUI(c =>
            {
                //c.RoutePrefix = "api-docs";
                c.SwaggerEndpoint("./v1/swagger.json", "Open World Server Character Persistence API");
            });

            container.Verify();
        }

        private void InitializeContainer(IServiceCollection services)
        {
            var OWSStorageConfig = Configuration.GetSection("OWSStorageConfig");
            if (OWSStorageConfig.Exists())
            {
                string dbBackend = OWSStorageConfig.GetValue<string>("OWSDBBackend");

                switch (dbBackend)
                {
                    case "postgres":
                        container.Register<ICharactersRepository, OWSData.Repositories.Implementations.Postgres.CharactersRepository>(Lifestyle.Scoped);
                        container.Register<IUsersRepository, OWSData.Repositories.Implementations.Postgres.UsersRepository>(Lifestyle.Scoped);
                        container.Register<IPlayerShopsRepository, OWSData.Repositories.Implementations.Postgres.PlayerShopsRepository>(Lifestyle.Scoped);
                        break;
                    default: // Default to MSSQL
                        container.Register<ICharactersRepository, OWSData.Repositories.Implementations.MSSQL.CharactersRepository>(Lifestyle.Scoped);
                        container.Register<IUsersRepository, OWSData.Repositories.Implementations.MSSQL.UsersRepository>(Lifestyle.Scoped);
                        // Player shops are Postgres-only; register the Postgres impl regardless so the
                        // controller resolves. (Deployments run Postgres.)
                        container.Register<IPlayerShopsRepository, OWSData.Repositories.Implementations.Postgres.PlayerShopsRepository>(Lifestyle.Scoped);
                        break;
                }
                // AuditLog, join tickets and character owners are Postgres-only, like player shops.
                container.Register<IAuditRepository, OWSData.Repositories.Implementations.Postgres.AuditRepository>(Lifestyle.Scoped);
                container.Register<IJoinTicketsRepository, OWSData.Repositories.Implementations.Postgres.JoinTicketsRepository>(Lifestyle.Scoped);
                container.Register<ICharacterOwnersRepository, OWSData.Repositories.Implementations.Postgres.CharacterOwnersRepository>(Lifestyle.Scoped);
                container.Register<IZoneOpsRepository, OWSData.Repositories.Implementations.Postgres.ZoneOpsRepository>(Lifestyle.Scoped);
                container.Register<ISanctionsRepository, OWSData.Repositories.Implementations.Postgres.SanctionsRepository>(Lifestyle.Scoped);
                container.Register<IConsoleSettingsRepository, OWSData.Repositories.Implementations.Postgres.ConsoleSettingsRepository>(Lifestyle.Scoped);
                container.Register<IPlayerReportsRepository, OWSData.Repositories.Implementations.Postgres.PlayerReportsRepository>(Lifestyle.Scoped);
            }
            container.Register<IHeaderCustomerGUID, HeaderCustomerGUID>(Lifestyle.Scoped);

            var provider = services.BuildServiceProvider();
            container.RegisterInstance<IServiceProvider>(provider);
        }
    }
}