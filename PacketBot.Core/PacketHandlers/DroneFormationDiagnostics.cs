using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FunnyProject.PacketHandlers
{
    // Reads formation IDs from the menu initialization packet (30106).
    internal static class DroneFormationDiagnostics
    {
        private static readonly byte[] FormationPrefix =
            Encoding.ASCII.GetBytes("drone_formation_");

        private static IEnumerable<string> ReadFormationIds(byte[] payload)
        {
            for (int offset = 2; offset <= payload.Length - FormationPrefix.Length; offset++)
            {
                bool matches = true;
                for (int j = 0; j < FormationPrefix.Length; j++)
                {
                    if (payload[offset + j] != FormationPrefix[j])
                    {
                        matches = false;
                        break;
                    }
                }
                if (!matches) continue;

                // The two bytes immediately before the string encode its
                // UTF-8 byte length, as in ActionScript IDataInput.readUTF().
                int length = (payload[offset - 2] << 8) | payload[offset - 1];
                if (length < FormationPrefix.Length || length > 256 ||
                    offset + length > payload.Length)
                    continue;

                string id = Encoding.UTF8.GetString(payload, offset, length);
                if (!id.StartsWith("drone_formation_", StringComparison.Ordinal))
                    continue;
                if (!id.All(c => (c >= 'a' && c <= 'z') ||
                                 (c >= 'A' && c <= 'Z') ||
                                 (c >= '0' && c <= '9') ||
                                 c == '_' || c == '-'))
                    continue;
                yield return id;
                offset += length - 1;
            }
        }

        public static string[] ExtractMenuFormationIds(byte[] payload) =>
            ReadFormationIds(payload)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
    }
}
