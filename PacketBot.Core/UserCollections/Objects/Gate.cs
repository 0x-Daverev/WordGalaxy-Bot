using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FunnyProject.UserCollections.Objects
{
    public class Gate
    {
        public int ID { get; set; }
        public int FactionID { get; set; }
        public int PortalType { get; set; }
        public int X { get; set; }
        public int Y { get; set; }

        //create constructor
        public Gate(int id, int factionID, int x, int y, int portalType = 0)
        {
            ID = id;
            PortalType = portalType;
            FactionID = factionID;
            X = x;
            Y = y;
        }
    }
}
