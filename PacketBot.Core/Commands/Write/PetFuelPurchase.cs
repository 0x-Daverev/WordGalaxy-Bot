using Krypton_Core.Managers;

namespace FunnyProject.Commands.Write
{
    // Captured purchase command: 00 04 02 85 00 06.
    // Same command family as PET repair (645), subcommand 6.
    public sealed class PetFuelPurchase : Command
    {
        public const short ID = 645;
        public PetFuelPurchase()
        {
            param1.writeShort(4);
            param1.writeShort(ID);
            param1.writeShort(6);
        }
    }
}
