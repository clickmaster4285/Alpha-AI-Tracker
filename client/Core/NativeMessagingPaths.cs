namespace client.Core;

/// <summary>
/// Centralized path constants for the native messaging bridge.
///
/// The Unix socket path was previously duplicated between
/// <c>NativeMessageService</c>'s constructor and
/// <c>BrowserExtensionService</c>'s constructor (and hardcoded in
/// native-host.py). Phase 1 (pure C# native host) moves it here so the
/// tracker, the C# host, and any tooling all agree on one constant.
/// </summary>
public static class NativeMessagingPaths
{
    /// <summary>
    /// Absolute path of the Unix domain socket the tracker listens on and the
    /// native messaging host forwards to. Matches native-host.py's
    /// <c>SOCKET_PATH</c> and the path <c>NativeMessageService</c> binds.
    /// </summary>
    public static string SocketPath
    {
        get
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userHome, ".local", "share", "alpha-ai-tracker", "native-messaging.sock");
        }
    }

    /// <summary>
    /// The Gecko application id from <c>extensions/firefox/manifest.json</c>
    /// (<c>browser_specific_settings.gecko.id</c>). Firefox invokes the native
    /// messaging host with this bare id as the first argument (no
    /// <c>chrome-extension://</c> prefix) — Program.cs uses it to detect
    /// host-mode for Firefox and Gecko forks (LibreWolf, Waterfox, Zen Browser).
    /// </summary>
    public const string GeckoApplicationId = "alpha-ai-tracker@alphai.com";
}
