using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Threading;

namespace OWSShared.Messages
{
    /// <summary>
    /// Thrown when the broker did not take responsibility for a launcher command: nothing was bound to receive it
    /// (no launcher queue for that World Server) or the publish was not confirmed.
    /// </summary>
    public class LauncherCommandNotDeliveredException : Exception
    {
        public LauncherCommandNotDeliveredException(string message, Exception innerException = null, bool certainlyNotQueued = false)
            : base(message, innerException)
        {
            CertainlyNotQueued = certainlyNotQueued;
        }

        /// <summary>
        /// True only when no launcher can receive the command: it never reached the broker, or the broker returned it as
        /// unroutable. False when it may still be queued, such as a confirm that timed out or was lost with the
        /// connection; a launcher may then start a server for it.
        /// </summary>
        public bool CertainlyNotQueued { get; }
    }

    /// <summary>
    /// The RabbitMQ topology shared by Instance Management (publisher) and the Instance Launcher (consumer) for the
    /// spin-up and shut-down commands.
    ///
    /// Each World Server has one durable, named queue per command. A launcher that crashes or reconnects leaves its
    /// unacknowledged commands in that queue, so they are delivered again instead of being lost with an exclusive,
    /// auto-delete queue. Publishes are mandatory and confirmed, so a command that reached no queue, or that the
    /// broker never confirmed, is reported to the caller as a failure instead of a success.
    /// </summary>
    public static class LauncherCommandChannel
    {
        public const string SpinUpExchange = "ows.serverspinup";
        public const string ShutDownExchange = "ows.servershutdown";

        //A queue no launcher has consumed from for this long is deleted by the broker. After that, publishes for its
        //World Server are unroutable and fail, instead of piling up for a launcher that is gone.
        public const int LauncherQueueExpiresMilliseconds = 10 * 60 * 1000;

        public static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromSeconds(10);

        public static string RoutingKey(string exchange, int worldServerID) => $"{exchange}.{worldServerID}";

        public static string QueueName(string exchange, int worldServerID) => $"{exchange}.{worldServerID}.launcher";

        //Exchanges stay non-durable, as they always were: redeclaring an existing exchange with a different
        //durability fails, which would break publishers and launchers of different versions sharing one broker.
        public static void DeclareExchange(IModel channel, string exchange)
        {
            channel.ExchangeDeclare(exchange: exchange, type: "direct", durable: false, autoDelete: false);
        }

        /// <summary>Declares and binds the launcher's queue for one command exchange. Returns the queue name.</summary>
        public static string DeclareLauncherQueue(IModel channel, string exchange, int worldServerID)
        {
            DeclareExchange(channel, exchange);

            string queueName = QueueName(exchange, worldServerID);

            channel.QueueDeclare(queue: queueName,
                                 durable: true,
                                 exclusive: false,
                                 autoDelete: false,
                                 arguments: new Dictionary<string, object>
                                 {
                                     { "x-expires", LauncherQueueExpiresMilliseconds }
                                 });
            channel.QueueBind(queueName, exchange, RoutingKey(exchange, worldServerID));

            return queueName;
        }

        /// <summary>
        /// Publishes a command to the launcher of one World Server and returns only once the broker has confirmed it
        /// was routed to a queue. Throws <see cref="LauncherCommandNotDeliveredException"/> otherwise.
        /// </summary>
        public static void Publish(IModel channel, string exchange, int worldServerID, byte[] body, TimeSpan? confirmTimeout = null)
        {
            string routingKey = RoutingKey(exchange, worldServerID);

            IBasicProperties properties;
            try
            {
                DeclareExchange(channel, exchange);
                channel.ConfirmSelect();
                properties = channel.CreateBasicProperties();
                properties.Persistent = true;
            }
            catch (Exception ex)
            {
                //Nothing has been published yet.
                throw new LauncherCommandNotDeliveredException($"Could not prepare the {exchange} command for World Server {worldServerID}.", ex, certainlyNotQueued: true);
            }

            //For an unroutable mandatory message the broker sends basic.return before basic.ack, and the client handles
            //both on its connection thread in that order, so the flag is set before WaitForConfirmsOrDie returns.
            int returned = 0;
            string returnReason = null;
            EventHandler<BasicReturnEventArgs> onReturn = (_, args) =>
            {
                returnReason = $"{args.ReplyCode} {args.ReplyText}";
                Interlocked.Exchange(ref returned, 1);
            };

            channel.BasicReturn += onReturn;
            try
            {
                //From here on a failure is ambiguous: the broker may already have queued the command.
                channel.BasicPublish(exchange: exchange,
                                     routingKey: routingKey,
                                     mandatory: true,
                                     basicProperties: properties,
                                     body: body);

                try
                {
                    channel.WaitForConfirmsOrDie(confirmTimeout ?? DefaultConfirmTimeout);
                }
                catch (Exception ex)
                {
                    //A timed-out, negative or lost confirm does not say the command missed the queue.
                    throw new LauncherCommandNotDeliveredException($"RabbitMQ did not confirm the {exchange} command for World Server {worldServerID}.", ex,
                        certainlyNotQueued: Volatile.Read(ref returned) == 1);
                }
            }
            finally
            {
                channel.BasicReturn -= onReturn;
            }

            if (Volatile.Read(ref returned) == 1)
            {
                //basic.return on a mandatory publish means no queue received it.
                throw new LauncherCommandNotDeliveredException($"No launcher is listening for World Server {worldServerID} ({routingKey}): {returnReason}.",
                    certainlyNotQueued: true);
            }
        }

        /// <summary>
        /// Connects to the broker and <see cref="Publish"/>es one command. A failure to connect or open a channel is reported as
        /// <see cref="LauncherCommandNotDeliveredException.CertainlyNotQueued"/>, since nothing was sent.
        /// </summary>
        public static void ConnectAndPublish(IConnectionFactory factory, string exchange, int worldServerID, byte[] body, TimeSpan? confirmTimeout = null)
        {
            IConnection connection;
            try
            {
                connection = factory.CreateConnection();
            }
            catch (Exception ex)
            {
                throw new LauncherCommandNotDeliveredException($"Could not connect to RabbitMQ to send the {exchange} command for World Server {worldServerID}.", ex, certainlyNotQueued: true);
            }

            using (connection)
            {
                IModel channel;
                try
                {
                    channel = connection.CreateModel();
                }
                catch (Exception ex)
                {
                    throw new LauncherCommandNotDeliveredException($"Could not open a RabbitMQ channel for the {exchange} command for World Server {worldServerID}.", ex, certainlyNotQueued: true);
                }

                using (channel)
                {
                    Publish(channel, exchange, worldServerID, body, confirmTimeout);
                }
            }
        }

        /// <summary>Whether a failed publish certainly left the command unqueued. Any other exception counts as maybe queued.</summary>
        public static bool CertainlyNotQueued(Exception ex) =>
            ex is LauncherCommandNotDeliveredException { CertainlyNotQueued: true };
    }
}
