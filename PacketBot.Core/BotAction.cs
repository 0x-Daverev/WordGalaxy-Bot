using System.Threading;

namespace FunnyProject
{
    // Current high-level intent of one game instance, not the last TCP packet.
    public enum BotAction
    {
        Idle,
        Run,
        Attack
    }

    public sealed class BotActionState
    {
        private int current = (int)BotAction.Idle;
        public BotAction Current
        {
            get => (BotAction)Volatile.Read(ref current);
            set => Volatile.Write(ref current, (int)value);
        }
    }
}
