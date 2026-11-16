using Krypton_Core.Packets.Bytes;
using System;

namespace FunnyProject.Commands.Read
{
    // WordGalaxy_main.swf: scripts/§_-G4V§/§_-dj§.as (ID 31208).
    public sealed class PetCooldownUpdate
    {
        public const short ID = 31208;
        public double Time { get; }
        public string Ability { get; }
        public int State { get; }
        public bool Activatable { get; }
        public double MaxTime { get; }

        public PetCooldownUpdate(EndianBinaryReader reader)
        {
            Time = reader.ReadDouble();
            Ability = reader.ReadString();
            reader.ReadInt16(); // nested timerState command id (686)
            reader.ReadInt16(); // nested state prefix
            State = reader.ReadInt16();
            Activatable = reader.ReadBoolean();
            MaxTime = reader.ReadDouble();
        }
    }
}
