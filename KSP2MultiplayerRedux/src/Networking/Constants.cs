namespace KSP2MultiplayerRedux.Networking
{
    public static class Constants
    {
        public const string VERSION = "0.2.5";
        // Protocol version is part of the key: mismatched clients are rejected at handshake.
        public const string PROTOCOL_VERSION = "3";
        public const string CONNECTION_KEY = "KSP2_MP_REDUX_proto" + PROTOCOL_VERSION;
    }
}
