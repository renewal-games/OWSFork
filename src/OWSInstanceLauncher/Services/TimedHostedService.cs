using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using SimpleInjector.Lifestyles;
using Serilog;


namespace OWSInstanceLauncher.Services
{
    public class TimedHostedService<TService> : IHostedService, IDisposable where TService : class
    {
        private readonly SimpleInjector.Container container;
        private readonly Settings settings;
        //private readonly ILogger logger;
        private readonly Timer timer;
        //1 while a callback is running the action; overlapping ticks see it and skip.
        private int running;

        public TimedHostedService(SimpleInjector.Container container, Settings settings/*, ILogger logger*/)
        {
            this.container = container;
            this.settings = settings;
            //this.logger = logger;
            this.timer = new Timer(callback: _ => this.DoWork());
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Verify that TService can be resolved
            this.container.GetRegistration(typeof(TService), true);
            // Start the timer
            this.timer.Change(dueTime: TimeSpan.Zero, period: this.settings.Interval);
            return Task.CompletedTask;
        }

        private void DoWork()
        {
            //The timer starts a new callback every period even while the last one is still running, and the jobs are
            //singletons with unsynchronized state (e.g. the health monitor's orphan miss counts). A tick that arrives
            //while a run is in progress is skipped; the next tick after the run finishes runs normally.
            if (Interlocked.CompareExchange(ref this.running, 1, 0) != 0)
            {
                Log.Warning("{Job} is still running from an earlier tick; skipping this tick.", typeof(TService).Name);
                return;
            }

            try
            {
                using (AsyncScopedLifestyle.BeginScope(this.container))
                {
                    var service = this.container.GetInstance<TService>();
                    this.settings.Action(service);
                    if (settings.RunOnce)
                    {
                        this.timer.Change(Timeout.Infinite, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Job} failed: {Message}", typeof(TService).Name, ex.Message);
            }
            finally
            {
                Volatile.Write(ref this.running, 0);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            this.timer.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        //public void Dispose() => this.timer.Dispose();

        public void Dispose()
        {
            try
            {
                using (AsyncScopedLifestyle.BeginScope(this.container))
                {
                    var service = this.container.GetInstance<TService>();
                    this.settings.Dispose(service);
                }
            }
            catch (Exception ex)
            {
                //this.logger.LogError(ex, ex.Message);
            }

            this.timer.Dispose();
        }

        public class Settings
        {
            public readonly TimeSpan Interval;
            public readonly bool RunOnce;
            public readonly Action<TService> Action;
            public readonly Action<TService> Dispose;

            public Settings(TimeSpan interval, bool runOnce, Action<TService> action, Action<TService> dispose)
            {
                this.Interval = interval;
                this.RunOnce = runOnce;
                this.Action = action;
                this.Dispose = dispose;
            }
        }
    }
}
