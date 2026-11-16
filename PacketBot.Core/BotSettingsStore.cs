using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FunnyProject
{
    internal static class BotSettingsStore
    {
        private static readonly object Sync = new object();
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordGalaxyPacketBot", "settings");

        // Same file name on Windows and Linux: settings can be copied between them.
        internal static string PathFor(string? server, string? username)
        {
            string identity = (server ?? "").Trim().ToLowerInvariant() + ":" +
                              (username ?? "").Trim().ToLowerInvariant();
            using var sha = SHA256.Create();
            return Path.Combine(Folder,
                Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))) + ".json");
        }

        public static BotSettings Load(string? server, string? username)
        {
            lock (Sync)
            {
                try
                {
                    string path = PathFor(server, username);
                    if (!File.Exists(path)) return new BotSettings();
                    var value = JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(path))
                                ?? new BotSettings();
                    if (value.Spins != 1 && value.Spins != 5 && value.Spins != 10 && value.Spins != 100)
                        value.Spins = 100;
                    if (!BotSettings.IsValidLaser(value.Laser))
                        value.Laser = "ucb-100";
                    if (!BotSettings.IsValidLaser(value.NpcKillerLaser))
                        value.NpcKillerLaser = "ucb-100";
                    if (value.AttackMode != "laser" && value.AttackMode != "kami" && value.AttackMode != "both")
                        value.AttackMode = "both";
                    if (value.AllowedInviterId <= 0) value.AllowedInviterId = null;
                    return value;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Settings] Error leyendo preferencias: " + ex.Message);
                    return new BotSettings();
                }
            }
        }

        public static void Save(string? server, string? username, BotSettings settings)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    string path = PathFor(server, username);
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(settings,
                        new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, path, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[Settings] Error guardando preferencias: " + ex.Message);
                }
            }
        }
    }
}
