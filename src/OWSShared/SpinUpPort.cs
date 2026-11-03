namespace OWSShared
{
    /// <summary>
    /// A zone server port arrives in spin-up requests and messages. Port 0 would let the OS pick one nobody knows about, and
    /// a port outside this range makes the launcher's port check throw.
    /// </summary>
    public static class SpinUpPort
    {
        public static bool IsValid(int port)
        {
            return port >= 1 && port <= 65535;
        }
    }
}
