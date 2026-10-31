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
        public LauncherCommandNotDeliveredException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
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

            DeclareExchange(channel, exchange);
            channel.ConfirmSelect();

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
                IBasicProperties properties = channel.CreateBasicProperties();
                properties.Persistent = true;

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
                    throw new LauncherCommandNotDeliveredException($"RabbitMQ did not confirm the {exchange} command for World Server {worldServerID}.", ex);
                }
            }
            finally
            {
                channel.BasicReturn -= onReturn;
            }

            if (Volatile.Read(ref returned) == 1)
            {
                throw new LauncherCommandNotDeliveredException($"No launcher is listening for World Server {worldServerID} ({routingKey}): {returnReason}.");
            }
        }
    }
}
