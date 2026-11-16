using Krypton_Core.Managers;
using System.Text;

namespace FunnyProject.Commands.Write
{
    // Captured on the wire: 00 0B 5C 22 00 07 62 6F 6F 73 74 65 72
    // The server opens the booster window with this command.
    internal sealed class OpenBoosters : Command
    {
        public const short ID = 23586;

        public OpenBoosters()
        {
            const string action = "booster";
            param1.writeShort((short)(4 + Encoding.UTF8.GetByteCount(action)));
            param1.writeShort(ID);
            param1.writeUTF(action);
        }
    }

    // Captured on the wire: 00 12 10 80 00 0E "WG_BOOSTERS|v1"
    // The custom WordGalaxy booster snapshot request, normally every 5s.
    internal sealed class RequestBoosters : Command
    {
        public const short ID = 4224;

        public RequestBoosters()
        {
            const string action = "WG_BOOSTERS|v1";
            param1.writeShort((short)(4 + Encoding.UTF8.GetByteCount(action)));
            param1.writeShort(ID);
            param1.writeUTF(action);
        }
    }
}
