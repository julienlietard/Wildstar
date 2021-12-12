using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Network.Message.Model
{
    [Message(GameMessageOpcode.Server07F6)]
    public class Server07F6 : IWritable
    {
        public class UnknownStructure3 : IWritable
        {
            public uint Unknown0 { get; set; } = 0;
            public uint Unknown1 { get; set; } = 0;
            public uint Unknown2 { get; set; } = 0;
            public uint Unknown3 { get; set; } = 0;
            public uint Unknown4 { get; set; } = 0;
            public uint Unknown5 { get; set; } = 0;
            public uint Unknown6 { get; set; } = 0;
            public byte Unknown7 { get; set; } = 0;

            public void Write(GamePacketWriter writer)
            {
                writer.Write(Unknown0);
                writer.Write(Unknown1);
                writer.Write(Unknown2);
                writer.Write(Unknown3);
                writer.Write(Unknown4);
                writer.Write(Unknown5);
                writer.Write(Unknown6);
                writer.Write(Unknown7, 3u);
            }
        }

        public uint ServerUniqueId { get; set; }
        public uint Spell4EffectId { get; set; } = 0;
        public uint UnitId { get; set; } = 0;
        public uint TargetId { get; set; } = 0;

        public DamageDescription DamageDescriptionData { get; set; } = new DamageDescription();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(ServerUniqueId);
            writer.Write(Spell4EffectId, 19);
            writer.Write(UnitId);
            writer.Write(TargetId);
            DamageDescriptionData.Write(writer);
        }
    }
}
