using System;
using System.Collections.Generic;
using OWSCharacterPersistence.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace OWSTests.CharacterPersistence
{
    /// <summary>
    /// The revision-less save count has to be visible without one log line per autosave: the first save logs at
    /// once, the rest as one summary per interval.
    /// </summary>
    public class RevisionlessSaveMonitorTests
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        [Fact]
        public void FirstSaveLogsAtOnceAndLaterOnesAreSummarisedPerInterval()
        {
            var sink = new CapturingSink();
            DateTime now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
            var monitor = new RevisionlessSaveMonitor(new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger(), () => now, Interval);

            monitor.Record(RevisionlessSaveMonitor.Inventory, refused: false);
            Assert.Single(sink.Events);
            Assert.Equal(LogEventLevel.Warning, sink.Events[0].Level);
            Assert.Equal(1L, Scalar(sink.Events[0], "Count"));

            now += TimeSpan.FromMinutes(1);
            monitor.Record(RevisionlessSaveMonitor.Currency, refused: false);
            monitor.Record(RevisionlessSaveMonitor.CharacterData, refused: true);
            Assert.Single(sink.Events);

            now += Interval;
            monitor.Record(RevisionlessSaveMonitor.Inventory, refused: true);
            Assert.Equal(2, sink.Events.Count);
            LogEvent summary = sink.Events[1];
            Assert.Equal(3L, Scalar(summary, "Count"));
            Assert.Equal(1L, Scalar(summary, "Inventory"));
            Assert.Equal(1L, Scalar(summary, "Currency"));
            Assert.Equal(1L, Scalar(summary, "CharacterData"));
            Assert.Equal(2L, Scalar(summary, "Refused"));
            Assert.Equal(4L, monitor.Total);
        }

        private static object Scalar(LogEvent logEvent, string property) =>
            ((ScalarValue)logEvent.Properties[property]).Value;

        private sealed class CapturingSink : ILogEventSink
        {
            public readonly List<LogEvent> Events = new List<LogEvent>();
            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }
}
