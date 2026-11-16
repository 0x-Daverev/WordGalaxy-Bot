using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FunnyProject
{
    internal static class TelegramSettingsStore
    {
        private sealed class Data
        {
            public string ProtectedToken { get; set; } = "";
            public long ChatId { get; set; }
        }

        private static readonly string PathName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordGalaxyPacketBot", "telegram.json");

        public static (string Token, long ChatId)? Load()
        {
            if (!File.Exists(PathName)) return null;
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(PathName));
            if (data == null || string.IsNullOrEmpty(data.ProtectedToken) || data.ChatId == 0)
                return null;
            byte[] cipher = Convert.FromBase64String(data.ProtectedToken);
            string token = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                cipher, null, DataProtectionScope.CurrentUser));
            return (token, data.ChatId);
        }

        public static void Save(string token, long chatId)
        {
            token = token.Trim();
            if (string.IsNullOrWhiteSpace(token) || chatId == 0)
                throw new ArgumentException("Introduce un token y Chat ID validos.");
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            string temp = PathName + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Data
            {
                ProtectedToken = Convert.ToBase64String(encrypted),
                ChatId = chatId
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, PathName, true);
        }

        public static void Delete()
        {
            if (File.Exists(PathName)) File.Delete(PathName);
        }
    }
}
