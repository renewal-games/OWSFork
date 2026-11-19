using OWSInstanceLauncher.Services;
using SimpleInjector;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OWSTests.InstanceLauncher
{
    //The launcher's periodic jobs (health monitoring every RunServerHealthMonitoringFrequencyInSeconds) run through TimedHostedService.
    //A System.Threading.Timer fires its callback on a new thread pool thread every period whether or not the last one has
    //returned, so without a guard a slow run (blocking HTTP calls) overlaps the next one on the same singleton job.
    public class TimedHostedServiceTests
    {
        private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(20);
        //Many periods: long enough that an unguarded timer would certainly have started another callback.
        private static readonly TimeSpan SeveralPeriods = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(10);

        public sealed class TestJob
        {
        }

        [Fact]
        public async Task SlowRun_IsNotOverlappedByLaterTicks_AndTicksResumeAfterItFinishes()
        {
            int current = 0;
            int maxConcurrent = 0;
            int entries = 0;
            var firstEntered = new ManualResetEventSlim();
            var releaseFirst = new ManualResetEventSlim();
            var laterRunEntered = new ManualResetEventSlim();

            void Action(TestJob job)
            {
                int now = Interlocked.Increment(ref current);
                RecordMax(ref maxConcurrent, now);
                int entry = Interlocked.Increment(ref entries);
                try
                {
                    if (entry == 1)
                    {
                        firstEntered.Set();
                        releaseFirst.Wait(BoundedWait);
                    }
                    else
                    {
                        laterRunEntered.Set();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref current);
                }
            }

            using var container = NewContainer();
            var service = new TimedHostedService<TestJob>(container, new TimedHostedService<TestJob>.Settings(Period, false, Action, _ => { }));
            try
            {
                await service.StartAsync(CancellationToken.None);
                Assert.True(firstEntered.Wait(BoundedWait), "The first scheduled run never started.");

                await Task.Delay(SeveralPeriods);

                Assert.Equal(1, Volatile.Read(ref maxConcurrent));
                Assert.Equal(1, Volatile.Read(ref entries));

                releaseFirst.Set();
                Assert.True(laterRunEntered.Wait(BoundedWait), "No run started after the slow run finished.");
            }
            finally
            {
                releaseFirst.Set();
                await service.StopAsync(CancellationToken.None);
                service.Dispose();
            }

            Assert.Equal(1, Volatile.Read(ref maxConcurrent));
        }

        [Fact]
        public async Task ThrowingRun_ReleasesTheGuard_SoLaterTicksStillRun()
        {
            int entries = 0;
            var thirdEntered = new ManualResetEventSlim();

            void Action(TestJob job)
            {
                if (Interlocked.Increment(ref entries) >= 3)
                {
                    thirdEntered.Set();
                }
                throw new InvalidOperationException("Simulated job failure.");
            }

            using var container = NewContainer();
            var service = new TimedHostedService<TestJob>(container, new TimedHostedService<TestJob>.Settings(Period, false, Action, _ => { }));
            try
            {
                await service.StartAsync(CancellationToken.None);
                Assert.True(thirdEntered.Wait(BoundedWait), "Runs stopped after the job threw.");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
                service.Dispose();
            }
        }

        [Fact]
        public async Task RunOnce_RunsTheActionOnlyOnce_EvenWhenTicksElapseDuringTheRun()
        {
            int entries = 0;
            var firstEntered = new ManualResetEventSlim();
            var releaseFirst = new ManualResetEventSlim();

            void Action(TestJob job)
            {
                if (Interlocked.Increment(ref entries) == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait(BoundedWait);
                }
            }

            using var container = NewContainer();
            var service = new TimedHostedService<TestJob>(container, new TimedHostedService<TestJob>.Settings(Period, true, Action, _ => { }));
            try
            {
                await service.StartAsync(CancellationToken.None);
                Assert.True(firstEntered.Wait(BoundedWait), "The scheduled run never started.");

                await Task.Delay(SeveralPeriods);
                releaseFirst.Set();
                await Task.Delay(SeveralPeriods);

                Assert.Equal(1, Volatile.Read(ref entries));
            }
            finally
            {
                releaseFirst.Set();
                await service.StopAsync(CancellationToken.None);
                service.Dispose();
            }
        }

        private static Container NewContainer()
        {
            var container = new Container();
            container.RegisterInstance(new TestJob());
            return container;
        }

        private static void RecordMax(ref int max, int value)
        {
            int seen = Volatile.Read(ref max);
            while (value > seen)
            {
                int previous = Interlocked.CompareExchange(ref max, value, seen);
                if (previous == seen)
                {
                    return;
                }
                seen = previous;
            }
        }
    }
}
