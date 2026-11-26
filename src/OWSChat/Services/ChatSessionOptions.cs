using System;

namespace OWSChat.Service
{
    public sealed class ChatSessionOptions
    {
        public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    }
}
