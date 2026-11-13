using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using OWSShared.Messages;
using Xunit;

namespace OWSTests.InstanceLauncher
{
    public class RabbitMQDeploymentTests
    {
        private static string Source(string path)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "src", "OWS.sln")))
                directory = directory.Parent;
            Assert.NotNull(directory);
            return File.ReadAllText(Path.Combine(directory.FullName, "src", path));
        }

        [Theory]
        [InlineData(".docker/messaging.yml")]
        [InlineData("docker-compose.hetzner-dev.yml")]
        public void BrokerPortsAreLoopbackAndBootstrapCredentialsAreRequired(string path)
        {
            string compose = Source(path);
            foreach (string port in new[] { "5672", "15672" })
            {
                string[] bindings = compose.Split('\n').Where(line => line.TrimEnd().EndsWith(":" + port + "\"", StringComparison.Ordinal)).ToArray();
                Assert.Single(bindings);
                Assert.Contains("\"127.0.0.1:", bindings[0]);
            }
            Assert.Contains("RABBITMQ_DEFAULT_USER: \"${RABBITMQ_DEFAULT_USER:?", compose);
            Assert.Contains("RABBITMQ_DEFAULT_PASS: \"${RABBITMQ_DEFAULT_PASS:?", compose);
        }

        [Fact]
        public void ApplicationUserHasNoManagementTagsAndOnlyLauncherResourcePermissions()
        {
            string config = Source(".docker/rabbitmq/rabbitmq.conf");
            Assert.DoesNotMatch(@"(?m)^default_(user|pass)\s*=", config);
            Assert.Contains("default_user_tags.administrator = false", config);
            Assert.Contains("default_user_tags.management = false", config);
            foreach (string permission in new[] { "configure", "read", "write" })
            {
                string pattern = Regex.Match(config, @"(?m)^default_permissions\." + permission + @"\s*=\s*(.+)$").Groups[1].Value.Trim();
                Assert.NotEmpty(pattern);
                foreach (string exchange in new[] { LauncherCommandChannel.SpinUpExchange, LauncherCommandChannel.ShutDownExchange })
                {
                    Assert.Matches(pattern, exchange);
                    Assert.Matches(pattern, LauncherCommandChannel.QueueName(exchange, 42));
                }
                foreach (string unrelated in new[] { "unrelated", "amq.default", "ows.serverspinup.evil", "xows.serverspinup", "owsXserverspinup", "ows.serverspinup.42.launcher.extra" })
                    Assert.DoesNotMatch(pattern, unrelated);
            }
        }

        [Theory]
        [InlineData("OWSCharacterPersistence")]
        [InlineData("OWSGlobalData")]
        [InlineData("OWSInstanceManagement")]
        [InlineData("OWSInstanceLauncher")]
        public void HostSettingsHaveNoCommittedBrokerCredentials(string service)
        {
            using var json = JsonDocument.Parse(Source(service + "/appsettings.json"));
            var options = json.RootElement.GetProperty("RabbitMQOptions");
            Assert.Equal("", options.GetProperty("RabbitMQUserName").GetString());
            Assert.Equal("", options.GetProperty("RabbitMQPassword").GetString());
            Assert.Equal("127.0.0.1", options.GetProperty("RabbitMQHostName").GetString());
        }

        [Fact]
        public void ContainerClientsUseComposeDnsAndTheBootstrapCredentials()
        {
            Assert.Contains("RabbitMQHostName=\"messaging\"", Source(".env"));
            string compose = Source("docker-compose.yml");
            Assert.DoesNotContain("${RabbitMQUserName}", compose);
            Assert.DoesNotContain("${RabbitMQPassword}", compose);
            Assert.Equal(3, Regex.Matches(compose, @"RabbitMQOptions__RabbitMQPassword=\$\{RABBITMQ_DEFAULT_PASS:\?").Count);
        }
    }
}
