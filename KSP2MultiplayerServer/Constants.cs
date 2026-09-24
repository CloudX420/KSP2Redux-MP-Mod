namespace KSP2MultiplayerServer
{
    public static class Constants
    {
        public const string VERSION = "0.2.5";
        // Must match KSP2MultiplayerRedux.Networking.Constants.CONNECTION_KEY
        public const string PROTOCOL_VERSION = "3";
        public const string CONNECTION_KEY = "KSP2_MP_REDUX_proto" + PROTOCOL_VERSION;
    }
}
