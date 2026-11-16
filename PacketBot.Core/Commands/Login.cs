using Krypton_Core.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.Commands
{
    public class Login : Command
    {
        public const short ID = 10996;
        // userId and SessionID come from /map; there are no default credentials.
        public Login(int userId, string SessionID, int instanceID = 563, short FactionId = 0, string version = "8.3.2")
        {
            param1.writeShort(53);
            param1.writeShort(ID);
            param1.writeInt((int)((uint)instanceID >> 8 | (uint)instanceID << 24));
            param1.writeInt((int)((uint)userId >> 8 | (uint)userId << 24));
            param1.writeShort(FactionId);
            param1.writeUTF(SessionID);
            param1.writeUTF(version);
        }
        
    }
        
    
}
