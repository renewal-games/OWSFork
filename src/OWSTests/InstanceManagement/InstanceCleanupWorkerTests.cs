using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OWSData.Repositories.Interfaces;
using OWSInstanceManagement.Services;
using SimpleInjector;
using SimpleInjector.Lifestyles;
using Xunit;

namespace OWSTests.InstanceManagement
{
    public class InstanceCleanupWorkerTests
    {
        [Fact]
        public async Task SweepsAllCustomersAgainAfterFailure_WithoutLauncherOrJoin()
        {
            Guid failing = Guid.NewGuid(), healthy = Guid.NewGuid();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int passes = 0, scopes = 0;
            using var container = new Container();
            container.Options.DefaultScopedLifestyle = new AsyncScopedLifestyle();
            container.Register<IConsoleQueriesRepository>(() => Proxy<IConsoleQueriesRepository>(method =>
            {
                Assert.Equal("GetCustomerGUIDs", method.Name);
                return Task.FromResult<IEnumerable<Guid>>(new[] { failing, healthy });
            }), Lifestyle.Scoped);
            container.Register<ICharactersRepository>(() =>
            {
                Interlocked.Increment(ref scopes);
                return Proxy<ICharactersRepository>((method, args) =>
                {
                    Assert.Equal("CleanUpInstances", method.Name);
                    if ((Guid)args[0] == failing) throw new InvalidOperationException("Temporary failure");
                    Assert.Equal(healthy, (Guid)args[0]);
                    if (Interlocked.Increment(ref passes) == 2) completed.TrySetResult();
                    return Task.CompletedTask;
                });
            }, Lifestyle.Scoped);
            using var worker = new InstanceCleanupWorker(container, TimeSpan.FromMilliseconds(10));
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
            Assert.True(scopes >= 2);
            Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        }

        private static T Proxy<T>(Func<MethodInfo, object> invoke) where T : class =>
            Proxy<T>((method, _) => invoke(method));

        private static T Proxy<T>(Func<MethodInfo, object[], object> invoke) where T : class
        {
            T proxy = DispatchProxy.Create<T, RepositoryProxy>();
            ((RepositoryProxy)(object)proxy).InvokeMethod = invoke;
            return proxy;
        }

        public class RepositoryProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> InvokeMethod { get; set; }
            protected override object Invoke(MethodInfo targetMethod, object[] args) => InvokeMethod(targetMethod, args);
        }
    }
}
