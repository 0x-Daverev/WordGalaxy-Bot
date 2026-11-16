using Krypton_Core.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.Commands.Write
{
    public class CollectBox : Command
    {
        public const short ID = 6532;
        public CollectBox(string hash , int playerX , int playerY , int boxX , int boxY) => Write(hash ,playerX , playerY , boxX , boxY);


        private void Write(string hash , int playerX , int playerY , int boxX , int boxY)
        {
            hash ??= "";
            // Length counts every byte after itself: ID (2) + 4 coordinates (16)
            // + string length (2) + hash bytes. A wrong length desynchronises the
            // server's reader and makes it close the connection.
            int length = 20 + System.Text.Encoding.UTF8.GetByteCount(hash);
            param1.writeShort((short)length);
            param1.writeShort(ID);
            param1.writeInt((int)((uint)playerX << 3 | (uint)playerX >> 29));
            param1.writeInt((int)((uint)playerY << 2 | (uint)playerY >> 30));
            param1.writeInt((int)((uint)boxX >> 14 | (uint)boxX << 18));
            param1.writeInt((int)((uint)boxY << 1 | (uint)boxY >> 31));
            param1.writeUTF(hash);
            
        }

    }
}
