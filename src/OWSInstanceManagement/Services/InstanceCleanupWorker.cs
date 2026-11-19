using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OWSData.Repositories.Interfaces;
using Serilog;
using SimpleInjector;
using SimpleInjector.Lifestyles;

namespace OWSInstanceManagement.Services
{
    /// <summary>Reaps stale instances even when no launcher or join request is active.</summary>
    public class InstanceCleanupWorker : BackgroundService
    {
        private readonly Container _container;
        private readonly TimeSpan _interval;

        public InstanceCleanupWorker(Container container) : this(container, TimeSpan.FromSeconds(60)) { }

        public InstanceCleanupWorker(Container container, TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            _container = container;
            _interval = interval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (AsyncScopedLifestyle.BeginScope(_container))
                    {
                        var customers = await _container.GetInstance<IConsoleQueriesRepository>().GetCustomerGUIDs();
                        var characters = _container.GetInstance<ICharactersRepository>();
                        foreach (Guid customer in customers)
                        {
                            if (stoppingToken.IsCancellationRequested) return;
                            try
                            {
                                await characters.CleanUpInstances(customer);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Instance cleanup failed for {CustomerGUID}; will retry next sweep", customer);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Instance cleanup sweep failed; will retry next sweep");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
