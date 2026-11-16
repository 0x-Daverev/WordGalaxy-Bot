using Krypton_Core.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.Commands.Write
{
    
    internal class Action : Command
    {
        public Action(string action) => Write(action);
        public const short ID = 18889;
        private void Write(string action)
        {
            var length = System.Text.Encoding.UTF8.GetByteCount(action) + 10;
            param1.writeShort((short)length);
            param1.writeShort(ID);
            param1.writeShort(1);
            param1.writeShort(1);
            param1.writeUTF(action);
            param1.writeShort(21922);
        }
    }
}
