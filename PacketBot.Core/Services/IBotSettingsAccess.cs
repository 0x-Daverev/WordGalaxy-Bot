using System;

namespace FunnyProject.Services
{
    // How Core services read and change the account's BotSettings.
    // Windows: the window's in-memory settings, saved with its debounce.
    // Server: the settings file, with serialized read-modify-write.
    internal interface IBotSettingsAccess
    {
        // Must not be modified by the caller; use Update.
        BotSettings Read();
        // Applies and persists a change before returning.
        void Update(Action<BotSettings> change);
    }
}
