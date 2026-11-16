using Krypton_Core.Managers;
namespace FunnyProject.Commands.Write
{
    public sealed class PortalSelect : Command
    {
        public const short ID = 15665;
        public PortalSelect(int portalId)
        {
            param1.writeShort(8);
            param1.writeShort(ID);
            param1.writeShort(24883);
            param1.writeInt((int)(((uint)portalId >> 15) | ((uint)portalId << 17)));
        }
    }
    public sealed class PortalJump : Command
    {
        public const short ID = 9253;
        public PortalJump()
        {
            param1.writeShort(2);
            param1.writeShort(ID);
        }
    }
    public sealed class GroupAccept : Command
    {
        public const short ID = 11175;
        public GroupAccept(int inviteeId)
        {
            param1.writeShort(8);
            param1.writeShort(ID);
            param1.writeInt((int)(((uint)inviteeId << 14) | ((uint)inviteeId >> 18)));
            param1.writeShort(30637);
        }
    }
}
