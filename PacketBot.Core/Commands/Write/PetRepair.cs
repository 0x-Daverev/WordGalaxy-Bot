using Krypton_Core.Managers;

namespace FunnyProject.Commands.Write
{
    // Captured TCP payload: 00 04 02 85 00 04 (length, command 645, value 4).
    public sealed class PetRepair : Command
    {
        public const short ID = 645;
        public PetRepair()
        {
            param1.writeShort(4);
            param1.writeShort(ID);
            param1.writeShort(4);
        }
    }
}
