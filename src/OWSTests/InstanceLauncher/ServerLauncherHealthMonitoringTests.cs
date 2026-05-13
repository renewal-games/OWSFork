using OWSData.Models.StoredProcs;
using OWSInstanceLauncher.Services;
using System;
using Xunit;

namespace OWSTests.InstanceLauncher
{
    public class ServerLauncherHealthMonitoringTests
    {
        [Fact]
        public void ShouldShutdownZoneInstance_ReturnsFalse_WhenTimeoutIsDisabled()
        {
            var zoneInstance = ReadyEmptyZoneInstance();
            zoneInstance.MinutesToShutdownAfterEmpty = 0;

            bool result = ServerLauncherHealthMonitoring.ShouldShutdownZoneInstance(zoneInstance);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShutdownZoneInstance_ReturnsFalse_WhenLastEmptyDateIsMissing()
        {
            var zoneInstance = ReadyEmptyZoneInstance();
            zoneInstance.LastServerEmptyDate = null;

            bool result = ServerLauncherHealthMonitoring.ShouldShutdownZoneInstance(zoneInstance);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShutdownZoneInstance_ReturnsFalse_WhenPlayersAreConnected()
        {
            var zoneInstance = ReadyEmptyZoneInstance();
            zoneInstance.NumberOfReportedPlayers = 1;

            bool result = ServerLauncherHealthMonitoring.ShouldShutdownZoneInstance(zoneInstance);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShutdownZoneInstance_ReturnsFalse_WhenZoneIsNotReady()
        {
            var zoneInstance = ReadyEmptyZoneInstance();
            zoneInstance.Status = 1;

            bool result = ServerLauncherHealthMonitoring.ShouldShutdownZoneInstance(zoneInstance);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShutdownZoneInstance_ReturnsFalse_WhenZoneIsAlreadyShuttingDown()
        {
            var zoneInstance = ReadyEmptyZoneInstance();
            zoneInstance.Status = 3;

            bool result = ServerLauncherHealthMonitoring.ShouldShutdownZoneInstance(zoneInstance);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShutdownZoneInstance_ReturnsFalse_WhenEmptyTimeoutHasNotElapsed()
        {
            var zoneInstance = ReadyEmptyZoneInstance();
            zoneInstance.MinutesToShutdownAfterEmpty = 5;
            zoneInstance.MinutesServerHasBeenEmpty = 4;

            bool result = ServerLauncherHealthMonitoring.ShouldShutdownZoneInstance(zoneInstance);

            Assert.False(result);
        }

        [Fact]
        public void ShouldShutdownZoneInstance_ReturnsTrue_WhenEmptyTimeoutHasElapsed()
        {
            var zoneInstance = ReadyEmptyZoneInstance();
            zoneInstance.MinutesToShutdownAfterEmpty = 5;
            zoneInstance.MinutesServerHasBeenEmpty = 6;

            bool result = ServerLauncherHealthMonitoring.ShouldShutdownZoneInstance(zoneInstance);

            Assert.True(result);
        }

        private static GetZoneInstancesForWorldServer ReadyEmptyZoneInstance()
        {
            return new GetZoneInstancesForWorldServer
            {
                MapInstanceID = 1,
                WorldServerID = 1,
                Status = 2,
                NumberOfReportedPlayers = 0,
                LastServerEmptyDate = DateTime.Now.AddMinutes(-10),
                MinutesToShutdownAfterEmpty = 5,
                MinutesServerHasBeenEmpty = 10
            };
        }
    }
}
