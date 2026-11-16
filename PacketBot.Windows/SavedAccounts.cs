using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FunnyProject
{
    internal static class SavedAccounts
    {
        private sealed class Entry
        {
            public string Username { get; set; } = "";
            public string PasswordProtected { get; set; } = "";
        }

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordGalaxyPacketBot", "accounts.json");

        private static List<Entry> Read()
        {
            if (!File.Exists(FilePath)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new List<Entry>();
        }

        public static string[] Names() => Read().Select(x => x.Username)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        public static string Load(string username)
        {
            var entry = Read().FirstOrDefault(x =>
                string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new InvalidOperationException("Cuenta no encontrada.");
            byte[] protectedBytes = Convert.FromBase64String(entry.PasswordProtected);
            byte[] clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }

        public static void Save(string username, string password)
        {
            username = username.Trim();
            if (username.Length == 0 || password.Length == 0)
                throw new ArgumentException("Introduce usuario y contraseña.");
            var entries = Read();
            entries.RemoveAll(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
            entries.Add(new Entry { Username = username, PasswordProtected = Convert.ToBase64String(cipher) });
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
        }

        public static void Delete(string username)
        {
            var entries = Read();
            entries.RemoveAll(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
        }
    }
}
