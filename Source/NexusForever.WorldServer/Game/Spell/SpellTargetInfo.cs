using System.Collections.Generic;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Game.Spell
{
    public class SpellTargetInfo
    {
        public class SpellTargetEffectInfo
        {
            public uint EffectId { get; }
            public Spell4EffectsEntry Entry { get; }
            public DamageDescription Damage { get; private set; }

            public SpellTargetEffectInfo(uint effectId, Spell4EffectsEntry entry)
            {
                EffectId = effectId;
                Entry    = entry;
            }

            public void AddDamage(DamageType damageType, uint damage)
            {
                // TODO: handle this correctly
                Damage = new DamageDescription
                {
                    DamageType      = damageType,
                    RawDamage       = damage,
                    RawScaledDamage = damage,
                    AdjustedDamage  = damage
                };
            }
        }

        public SpellEffectTargetFlags Flags { get; }
        public UnitEntity Entity { get; }
        public float Distance { get; }
        public List<SpellTargetEffectInfo> Effects { get; } = new List<SpellTargetEffectInfo>();

        public SpellTargetInfo(SpellEffectTargetFlags flags, UnitEntity entity)
        {
            Flags  = flags;
            Entity = entity;
        }

        public SpellTargetInfo(SpellEffectTargetFlags flags, UnitEntity entity, float distance)
        {
            Flags    = flags;
            Entity   = entity;
            Distance = distance;
        }
    }
}
