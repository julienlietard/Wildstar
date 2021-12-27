using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared;
using NexusForever.Shared.Game;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Prerequisite;
using NexusForever.WorldServer.Game.Spell.Event;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NLog;

namespace NexusForever.WorldServer.Game.Spell
{
    public abstract partial class Spell : IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public uint CastingId { get; }
        public uint Spell4Id => parameters.SpellInfo.Entry.Id;
        public CastMethod CastMethod { get; protected set; }

        public bool IsCasting => _IsCasting();
        public bool IsFinished => status == SpellStatus.Finished || status == SpellStatus.Failed;
        public bool IsFailed => status == SpellStatus.Failed;
        public bool IsWaiting => status == SpellStatus.Waiting;
        public bool HasGroup(uint groupId) => parameters.SpellInfo.GroupList?.SpellGroupIds.Contains(groupId) ?? false;

        protected readonly UnitEntity caster;
        protected readonly SpellParameters parameters;

        protected SpellStatus status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;

                var previousStatus = _status;
                _status = value;
                OnStatusChange(previousStatus, value);
            }
        }
        private SpellStatus _status;

        protected byte currentPhase = 255;
        protected uint duration = 0;

        protected readonly SpellEventManager events = new();
        
        protected readonly List<SpellTargetInfo> targets = new();
        protected readonly List<Telegraph> telegraphs = new();
        protected readonly List<Proxy> proxies = new();
        protected Dictionary<uint /*effectId*/, uint/*count*/> effectTriggerCount = new();
        protected Dictionary<uint /*effectId*/, double/*effectTimer*/> effectRetriggerTimers = new();

        private UpdateTimer persistCheck = new(0.1d);

        protected Spell(UnitEntity caster, SpellParameters parameters, CastMethod castMethod)
        {
            this.caster = caster;
            this.parameters = parameters;

            CastingId       = GlobalSpellManager.Instance.NextCastingId;
            status          = SpellStatus.Initiating;
            CastMethod      = castMethod;

            if (parameters.RootSpellInfo == null)
                parameters.RootSpellInfo = parameters.SpellInfo;

            if (this is not SpellThreshold && parameters.SpellInfo.Thresholds.Count > 0)
                throw new NotImplementedException();
        }

        /// <summary>
        /// Invoked each world tick with the delta since the previous tick occurred.
        /// </summary>
        public virtual void Update(double lastTick)
        {
            if (status == SpellStatus.Initiating)
                return;

            events.Update(lastTick);
            CheckPersistance(lastTick);
        }

        /// <summary>
        /// Invoked each world tick, after Update() for this <see cref="Spell"/>, with the delta since the previous tick occurred.
        /// </summary>
        public void LateUpdate(double lastTick)
        {
            if (CanFinish())
            {
                status = SpellStatus.Finished;

                if (parameters.PositionalUnitId > 0)
                    caster.GetVisible<WorldEntity>(parameters.PositionalUnitId)?.RemoveFromMap();

                foreach (SpellTargetInfo target in targets)
                    RemoveEffects(target);

                SendSpellFinish();
                log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has finished.");
            }
        }

        /// <summary>
        /// Checks prerequisites, Caster state, and resource values, to confirm whether spell is castable.
        /// </summary>
        protected bool CanCast()
        {
            if (status != SpellStatus.Initiating)
                throw new InvalidOperationException();

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started initating.");

            CastResult result = CheckCast();
            if (result != CastResult.Ok)
            {
                // Swallow Proxy CastResults
                if (parameters.IsProxy)
                    return false;

                if (caster is Player)
                    (caster as Player).SpellManager.SetAsContinuousCast(null);

                SendSpellCastResult(result);
                status = SpellStatus.Failed;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Begin cast, checking prerequisites before initiating.
        /// </summary>
        public virtual bool Cast()
        {
            if (!CanCast())
                return false;

            // TODO: Handle all GlobalCooldownEnums. It looks like it's just a "Type" that the GCD is stored against. Each spell checks the GCD for its type.
            if (caster is Player player)
            {
                if (parameters.SpellInfo.GlobalCooldown != null && !parameters.IsProxy)
                    player.SpellManager.SetGlobalSpellCooldown(parameters.SpellInfo.Entry.GlobalCooldownEnum, parameters.SpellInfo.GlobalCooldown.CooldownTime / 1000d);
                else if (parameters.IsProxy)
                    player.SpellManager.SetSpellCooldown(parameters.SpellInfo, parameters.CooldownOverride / 1000d);
            }

            // It's assumed that non-player entities will be stood still to cast (most do). 
            // TODO: There are a handful of telegraphs that are attached to moving units (specifically rotating units) which this needs to be updated to account for.
            if (!(caster is Player))
                InitialiseTelegraphs();

            // TODO: Fire Script's OnCast

            return true;
        }

        /// <summary>
        /// Returns a <see cref="CastResult"/> describing whether or not this <see cref="Spell"/> can be cast by its caster.
        /// </summary>
        protected CastResult CheckCast()
        {
            CastResult preReqResult = CheckPrerequisites();
            if (preReqResult != CastResult.Ok)
                return preReqResult;

            CastResult ccResult = CheckCCConditions();
            if (ccResult != CastResult.Ok)
                return ccResult;

            if (caster is Player player)
            {
                if (IsCasting && parameters.UserInitiatedSpellCast && !parameters.IsProxy)
                    return CastResult.SpellAlreadyCasting;

                // TODO: Some spells can be cast during other spell casts. Reflect that in this check
                if (caster.IsCasting() && parameters.UserInitiatedSpellCast && !parameters.IsProxy)
                    return CastResult.SpellAlreadyCasting;

                if (player.SpellManager.GetSpellCooldown(parameters.SpellInfo.Entry.Id) > 0d &&
                    parameters.UserInitiatedSpellCast &&
                    !parameters.IsProxy)
                    return CastResult.SpellCooldown;

                foreach (SpellCoolDownEntry coolDownEntry in parameters.SpellInfo.Cooldowns)
                {
                    if (player.SpellManager.GetSpellCooldownByCooldownId(coolDownEntry.Id) > 0d &&
                        parameters.UserInitiatedSpellCast &&
                        !parameters.IsProxy)
                        return CastResult.SpellCooldown;
                }

                if (player.SpellManager.GetGlobalSpellCooldown(parameters.SpellInfo.Entry.GlobalCooldownEnum) > 0d && 
                    !parameters.IsProxy && 
                    parameters.UserInitiatedSpellCast)
                    return CastResult.SpellGlobalCooldown;

                if (parameters.CharacterSpell?.MaxAbilityCharges > 0 && parameters.CharacterSpell?.AbilityCharges == 0)
                    return CastResult.SpellNoCharges;

                CastResult resourceConditions = CheckResourceConditions();
                if (resourceConditions != CastResult.Ok)
                {
                    if (parameters.UserInitiatedSpellCast && !parameters.IsProxy)
                        player.SpellManager.SetAsContinuousCast(null);

                    return resourceConditions;
                }
            }

            return CastResult.Ok;
        }

        private CastResult CheckPrerequisites()
        {
            // TODO: Remove below line and evaluate PreReq's for Non-Player Entities
            if (!(caster is Player player))
                return CastResult.Ok;

            // Runners override the Caster Check, allowing the Caster to Cast the spell due to this Prerequisite being met
            if (parameters.SpellInfo.CasterCastPrerequisite != null && !CheckRunnerOverride(player))
            {
                if (!PrerequisiteManager.Instance.Meets(player, parameters.SpellInfo.CasterCastPrerequisite.Id))
                    return CastResult.PrereqCasterCast;
            }

            // not sure if this should be for explicit and/or implicit targets
            if (parameters.SpellInfo.TargetCastPrerequisites != null)
            {
            }

            // this probably isn't the correct place, name implies this should be constantly checked
            if (parameters.SpellInfo.CasterPersistencePrerequisites != null)
            {
            }

            if (parameters.SpellInfo.TargetPersistencePrerequisites != null)
            {
            }

            return CastResult.Ok;
        }

        /// <summary>
        /// Returns whether the Caster is in a state where they can ignore Resource or other constraints.
        /// </summary>
        private bool CheckRunnerOverride(Player player)
        {
            foreach (PrerequisiteEntry runnerPrereq in parameters.SpellInfo.PrerequisiteRunners)
                if (PrerequisiteManager.Instance.Meets(player, runnerPrereq.Id))
                    return true;

            return false;
        }

        protected CastResult CheckCCConditions()
        {
            // TODO: this just looks like a mask for CCState enum
            if (parameters.SpellInfo.CasterCCConditions != null)
            {
            }

            // not sure if this should be for explicit and/or implicit targets
            if (parameters.SpellInfo.TargetCCConditions != null)
            {
            }

            return CastResult.Ok;
        }

        protected CastResult CheckResourceConditions()
        {
            if (!(caster is Player player))
                return CastResult.Ok;

            bool runnerOveride = CheckRunnerOverride(player);
            if (runnerOveride)
                return CastResult.Ok;

            //for (int i = 0; i < parameters.SpellInfo.Entry.CasterInnateRequirements.Length; i++)
            //{
            //    uint innateRequirement = parameters.SpellInfo.Entry.CasterInnateRequirements[i];
            //    if (innateRequirement == 0)
            //        continue;

            //    switch (parameters.SpellInfo.Entry.CasterInnateRequirementEval[i])
            //    {
            //        case 2:
            //            if (caster.GetVitalValue((Vital)innateRequirement) < parameters.SpellInfo.Entry.CasterInnateRequirementValues[i])
            //                return GlobalSpellManager.Instance.GetFailedCastResultForVital((Vital)innateRequirement);
            //            break;
            //    }
            //}

            //for (int i = 0; i < parameters.SpellInfo.Entry.InnateCostTypes.Length; i++)
            //{
            //    uint innateCostType = parameters.SpellInfo.Entry.InnateCostTypes[i];
            //    if (innateCostType == 0)
            //        continue;

            //    if (caster.GetVitalValue((Vital)innateCostType) < parameters.SpellInfo.Entry.InnateCosts[i])
            //        return GlobalSpellManager.Instance.GetFailedCastResultForVital((Vital)innateCostType);
            //}

            return CastResult.Ok;
        }

        /// <summary>
        /// Initialises a <see cref="Telegraph"/> per <see cref="TelegraphDamageEntry"/> as associated with this <see cref="Spell"/>.
        /// </summary>
        private void InitialiseTelegraphs()
        {
            telegraphs.Clear();

            Vector3 position = caster.Position;
            if (parameters.PositionalUnitId > 0)
                position = caster.GetVisible<WorldEntity>(parameters.PositionalUnitId)?.Position ?? caster.Position;

            Vector3 rotation = caster.Rotation;
            if (parameters.PositionalUnitId > 0)
                rotation = caster.GetVisible<WorldEntity>(parameters.PositionalUnitId)?.Rotation ?? caster.Rotation;

            foreach (TelegraphDamageEntry telegraphDamageEntry in parameters.SpellInfo.Telegraphs)
                telegraphs.Add(new Telegraph(telegraphDamageEntry, caster, position, rotation));
        }

        /// <summary>
        /// Cancel cast with supplied <see cref="CastResult"/>.
        /// </summary>
        public virtual void CancelCast(CastResult result)
        {
            if (caster is Player player && !player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new Server07F9
                {
                    ServerUniqueId = CastingId,
                    CastResult = result,
                    CancelCast = true
                });

                if (result == CastResult.CasterMovement)
                    player.SpellManager.SetGlobalSpellCooldown(parameters.SpellInfo.Entry.GlobalCooldownEnum, 0d);

                player.SpellManager.SetAsContinuousCast(null);

                SendSpellCastResult(result);
            }

            events.CancelEvents();
            status = SpellStatus.Finishing;

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} cast was cancelled.");
        }

        protected virtual void Execute(bool handleCDAndCost = true)
        {
            SpellStatus previousStatus = status;
            status = SpellStatus.Executing;
            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started executing.");

            if (handleCDAndCost)
            {
                if ((currentPhase == 0 || currentPhase == 255))
                {
                    CostSpell();
                    SetCooldown();
                }
            }

            // Clear Effects so that we don't duplicate Effect information back to the client.
            targets.ForEach(t => t.Effects.Clear());
            effectTriggerCount.Clear();

            // Order below must not change.
            SelectTargets();  // First Select Targets
            ExecuteEffects(); // All Effects are evaluated and executed (after SelectTargets())
            HandleProxies();  // Any Proxies that are added by Effets are evaluated and executed (after ExecuteEffects())
            SendSpellGo();    // Inform the Client once all evaluations are taken place (after Effects & Proxies are executed)

            if (duration > 0 || parameters.SpellInfo.Entry.SpellDuration > 0)
                SendBuffsApplied(targets.Where(x => x.TargetSelectionState == TargetSelectionState.New).Select(x => x.Entity.Guid).ToList());
        }

        protected void HandleProxies()
        {
            foreach (Proxy proxy in proxies)
                proxy.Evaluate();

            foreach (Proxy proxy in proxies)
                proxy.Cast(caster, events);

            proxies.Clear();
        }

        protected void SetCooldown()
        {
            if (!(caster is Player player))
                return;

            if (parameters.SpellInfo.Entry.SpellCoolDown != 0u)
                player.SpellManager.SetSpellCooldown(parameters.SpellInfo, parameters.SpellInfo.Entry.SpellCoolDown / 1000d);
        }

        protected void CostSpell()
        {
            if (parameters.CharacterSpell?.MaxAbilityCharges > 0)
                parameters.CharacterSpell.UseCharge();

            // TODO: Handle costing Vital Resources
        }

        List<uint> uniqueTargets = new();
        protected virtual void SelectTargets()
        {
            // We clear targets every time this is called for this spell so we don't have duplicate targets.
            targets.Clear();

            // Add Caster Entity with the appropriate SpellEffectTargetFlags.
            targets.Add(new SpellTargetInfo(SpellEffectTargetFlags.Caster, caster));

            // Add Targeted Entity with the appropriate SpellEffectTargetFlags.
            if (parameters.PrimaryTargetId > 0)
            {
                UnitEntity primaryTargetEntity = caster.GetVisible<UnitEntity>(parameters.PrimaryTargetId);
                if (primaryTargetEntity != null)
                    targets.Add(new SpellTargetInfo((SpellEffectTargetFlags.Target), primaryTargetEntity));
            }
            else
                targets[0].Flags |= SpellEffectTargetFlags.Target;

            // Targeting First Pass: Do Basic Checks to get targets for spell as needed, nearby.
            targets.AddRange(new AoeSelection(caster, parameters));

            // Re-initiailise Telegraphs at Execute time, so that position and rotation is calculated appropriately.
            // This is optimised to only happen on player-cast spells.
            // TODO: Add support for this for Server Controlled entities. It is presumed most will stand still when casting.
            if (caster is Player)
                InitialiseTelegraphs();

            if (telegraphs.Count > 0)
            {
                List<SpellTargetInfo> allowedTargets = new();
                foreach (Telegraph telegraph in telegraphs)
                {
                    List<uint> targetGuids = new();

                    // Ensure only telegraphs that apply to this Execute phase are evaluated.
                    if (CastMethod == CastMethod.Multiphase && currentPhase < 255)
                    {
                        int phaseMask = 1 << currentPhase;
                        if (telegraph.TelegraphDamage.PhaseFlags != 1 && (phaseMask & telegraph.TelegraphDamage.PhaseFlags) == 0)
                            continue;
                    }

                    log.Trace($"Getting targets for Telegraph ID {telegraph.TelegraphDamage.Id}");

                    foreach (var target in telegraph.GetTargets(this, targets))
                    {
                        // Ensure that this Telegraph hasn't already selected this entity
                        if (targetGuids.Contains(target.Entity.Guid))
                            continue;

                        // Ensure that this telegraph doesn't select an entity that has already been slected by another telegraph, as the targeting flags dictate.
                        if ((parameters.SpellInfo.BaseInfo.Entry.TargetingFlags & 32) != 0 &&
                            uniqueTargets.Contains(target.Entity.Guid))
                            continue;

                        target.Flags |= SpellEffectTargetFlags.Telegraph;
                        allowedTargets.Add(target);
                        targetGuids.Add(target.Entity.Guid);
                        uniqueTargets.Add(target.Entity.Guid);
                    }

                    log.Trace($"Got {targets.Count} for Telegraph ID {telegraph.TelegraphDamage.Id}");
                }
                targets.RemoveAll(x => x.Flags == SpellEffectTargetFlags.Telegraph); // Only remove targets that are ONLY Telegraph Targeted
                targets.AddRange(allowedTargets);
            }

            if (parameters.SpellInfo.AoeTargetConstraints != null)
            {
                List<SpellTargetInfo> finalAoeTargets = new();
                foreach (var target in targets)
                {
                    // Ensure that we're not exceeding the amount of targets we can select
                    if (parameters.SpellInfo.AoeTargetConstraints.TargetCount > 0 &&
                        finalAoeTargets.Count > parameters.SpellInfo.AoeTargetConstraints.TargetCount)
                        break;

                    if ((target.Flags & SpellEffectTargetFlags.Telegraph) == 0)
                        continue;

                    finalAoeTargets.Add(target);
                }

                // Finalise targets for effect execution
                targets.RemoveAll(x => x.Flags == SpellEffectTargetFlags.Telegraph); // Only remove targets that are ONLY Telegraph Targeted
                targets.AddRange(finalAoeTargets);
            }

            var distinctList = targets.Distinct(new SpellTargetInfo.SpellTargetInfoComparer()).ToList();
            targets.Clear();
            targets.AddRange(distinctList);
        }

        private void ExecuteEffects()
        {
            if (targets.Where(t => t.TargetSelectionState == TargetSelectionState.New).Count() == 0)
                return;

            if (targets.Count > 0 && CastMethod == CastMethod.Aura)
                log.Trace($"New Targets found for {CastingId}, applying effects.");

            // Using For..Loop instead of foreach intentionally, as this can be modified as effects are evaluated.
            for (int index = 0; index < parameters.SpellInfo.Effects.Count(); index++)
            {
                Spell4EffectsEntry spell4EffectsEntry = parameters.SpellInfo.Effects[index];

                ExecuteEffect(spell4EffectsEntry);
            }
        }

        private bool CanExecuteEffect(Spell4EffectsEntry spell4EffectsEntry)
        {
            if (caster is Player player)
            {
                // Ensure caster can apply this effect
                if (spell4EffectsEntry.PrerequisiteIdCasterApply > 0 && !PrerequisiteManager.Instance.Meets(player, spell4EffectsEntry.PrerequisiteIdCasterApply))
                    return false;
            }

            if (CastMethod == CastMethod.Multiphase && currentPhase < 255)
            {
                int phaseMask = 1 << currentPhase;
                if ((spell4EffectsEntry.PhaseFlags != 1 && spell4EffectsEntry.PhaseFlags != uint.MaxValue) && (phaseMask & spell4EffectsEntry.PhaseFlags) == 0)
                    return false;
            }

            if (CastMethod == CastMethod.Aura && spell4EffectsEntry.TickTime > 0 && effectRetriggerTimers[spell4EffectsEntry.Id] > 0d)
                return false;

            return true;
        }

        protected void ExecuteEffect(Spell4EffectsEntry spell4EffectsEntry)
        {
            if (!CanExecuteEffect(spell4EffectsEntry))
                return;

            log.Trace($"Executing SpellEffect ID {spell4EffectsEntry.Id} ({1 << currentPhase})");

            // Set Allowed States for entities being affected by this ExecuteEffect
            List<TargetSelectionState> allowedStates = new() { TargetSelectionState.New };
            if (CastMethod == CastMethod.Aura && spell4EffectsEntry.TickTime > 0)
                allowedStates.Add(TargetSelectionState.Existing);

            // select targets for effect
            List<SpellTargetInfo> effectTargets = targets
                .Where(t => allowedStates.Contains(t.TargetSelectionState) && (t.Flags & (SpellEffectTargetFlags)spell4EffectsEntry.TargetFlags) != 0)
                .ToList();

            SpellEffectDelegate handler = GlobalSpellManager.Instance.GetEffectHandler((SpellEffectType)spell4EffectsEntry.EffectType);
            if (handler == null)
                log.Warn($"Unhandled spell effect {(SpellEffectType)spell4EffectsEntry.EffectType}");
            else
            {
                uint effectId = GlobalSpellManager.Instance.NextEffectId;
                foreach (SpellTargetInfo effectTarget in effectTargets)
                {
                    if (!CheckEffectApplyPrerequisites(spell4EffectsEntry, effectTarget.Entity, effectTarget.Flags))
                        continue;

                    var info = new SpellTargetInfo.SpellTargetEffectInfo(effectId, spell4EffectsEntry);
                    effectTarget.Effects.Add(info);

                    // TODO: if there is an unhandled exception in the handler, there will be an infinite loop on Execute()
                    handler.Invoke(this, effectTarget.Entity, info);

                    // Track the number of times this effect has fired.
                    // Some spell effects have a limited trigger count per spell cast.
                    if (effectTriggerCount.TryGetValue(spell4EffectsEntry.Id, out uint count))
                        effectTriggerCount[spell4EffectsEntry.Id]++;
                    else
                        effectTriggerCount.TryAdd(spell4EffectsEntry.Id, 1);
                }

                // Add durations for each effect so that when the Effect timer runs out, the Spell can Finish.
                if (spell4EffectsEntry.DurationTime > 0)
                    events.EnqueueEvent(new SpellEvent(spell4EffectsEntry.DurationTime / 1000d, () => { /* placeholder for duration */ }));

                if (spell4EffectsEntry.DurationTime > 0 && spell4EffectsEntry.DurationTime > duration)
                    duration = spell4EffectsEntry.DurationTime;

                if (spell4EffectsEntry.DurationTime == 0u && ((SpellEffectFlags)spell4EffectsEntry.Flags & SpellEffectFlags.CancelOnly) != 0)
                    parameters.ForceCancelOnly = true;

                if (spell4EffectsEntry.TickTime > 0 && effectRetriggerTimers.ContainsKey(spell4EffectsEntry.Id))
                    effectRetriggerTimers[spell4EffectsEntry.Id] = spell4EffectsEntry.TickTime / 1000d;
            }
        }

        protected void RemoveEffects(SpellTargetInfo target)
        {
            if (target.Entity == null)
                return;

            if (targets.Count > 0 && CastMethod == CastMethod.Aura)
                log.Trace($"Target exited spell {CastingId}'s range, removing effects.");

            // TODO: Remove effects triggered by this spell from the target.
            // target.Entity?.RemoveSpellProperties(Spell4Id);
            // target.Entity?.RemoveProc(parameters.SpellInfo.Entry.Id);
            // target.Entity?.RemoveTemporaryDisplayItem(Spell4Id);
        }

        private bool CheckEffectApplyPrerequisites(Spell4EffectsEntry spell4EffectsEntry, UnitEntity unit, SpellEffectTargetFlags targetFlags)
        {
            bool effectCanApply = true;

            // TODO: Possibly update Prereq Manager to handle other Units
            if (unit is not Player player)
                return true;

            if ((targetFlags & SpellEffectTargetFlags.Caster) != 0)
            {
                // TODO
                if (spell4EffectsEntry.PrerequisiteIdCasterApply > 0)
                {
                    effectCanApply = PrerequisiteManager.Instance.Meets(player, spell4EffectsEntry.PrerequisiteIdCasterApply);
                }
            }

            if (effectCanApply && (targetFlags & SpellEffectTargetFlags.Caster) == 0)
            {
                if (spell4EffectsEntry.PrerequisiteIdTargetApply > 0)
                {
                    effectCanApply = PrerequisiteManager.Instance.Meets(player, spell4EffectsEntry.PrerequisiteIdTargetApply);
                }
            }

            return effectCanApply;
        }

        public bool IsMovingInterrupted()
        {
            // TODO: implement correctly
            return parameters.UserInitiatedSpellCast && parameters.SpellInfo.BaseInfo.SpellType.Id != 5 && parameters.SpellInfo.Entry.CastTime > 0;
        }

        /// <summary>
        /// Finish this <see cref="Spell"/> and end all effects associated with it.
        /// </summary>
        public virtual void Finish()
        {
            if (status == SpellStatus.Finished)
                return;

            events.CancelEvents();
            status = SpellStatus.Finishing;
        }

        private bool PassEntityChecks()
        {
            if (caster is Player)
                return parameters.UserInitiatedSpellCast;

            return true;
        }

        protected virtual bool _IsCasting()
        {
            if (parameters.IsProxy)
                return false;

            if (!(caster is Player) && status == SpellStatus.Initiating)
                return true;

            return PassEntityChecks();
        }

        protected void SendSpellCastResult(CastResult castResult)
        {
            if (castResult == CastResult.Ok)
                return;

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} failed to cast {castResult}.");

            if (caster is Player player && !player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new ServerSpellCastResult
                {
                    Spell4Id = parameters.SpellInfo.Entry.Id,
                    CastResult = castResult
                });
            }
        }

        protected virtual uint GetPrimaryTargetId()
        {
            if (parameters.PrimaryTargetId > 0)
                return parameters.PrimaryTargetId;

            if (parameters.PositionalUnitId > 0)
                return parameters.PositionalUnitId;

            return caster.Guid;
        }

        protected void SendSpellStart()
        {
            ServerSpellStart spellStart = new ServerSpellStart
            {
                CastingId            = CastingId,
                CasterId             = caster.Guid,
                PrimaryTargetId      = GetPrimaryTargetId(),
                Spell4Id             = parameters.SpellInfo.Entry.Id,
                RootSpell4Id         = parameters.RootSpellInfo?.Entry.Id ?? 0,
                ParentSpell4Id       = parameters.ParentSpellInfo?.Entry.Id ?? 0,
                FieldPosition        = new Position(caster.Position),
                Yaw                  = caster.Rotation.X,
                UserInitiatedSpellCast = parameters.UserInitiatedSpellCast,
                InitialPositionData = new List<InitialPosition>(),
                TelegraphPositionData = new List<TelegraphPosition>()
            };

            // TODO: Add Proxy Units
            List<UnitEntity> unitsCasting = new List<UnitEntity>();
            unitsCasting.Add(caster);

            foreach (UnitEntity unit in unitsCasting)
            {
                if (unit == null)
                    continue;

                if (unit is Player)
                    continue;

                spellStart.InitialPositionData.Add(new InitialPosition
                {
                    UnitId = unit.Guid,
                    Position = new Position(unit.Position),
                    TargetFlags = 3,
                    Yaw = unit.Rotation.X
                });
            }

            foreach (UnitEntity unit in unitsCasting)
            {
                if (unit == null)
                    continue;

                foreach (Telegraph telegraph in telegraphs)
                    spellStart.TelegraphPositionData.Add(new TelegraphPosition
                    {
                        TelegraphId = (ushort)telegraph.TelegraphDamage.Id,
                        AttachedUnitId = unit.Guid,
                        TargetFlags = 3,
                        Position = new Position(telegraph.Position),
                        Yaw = telegraph.Rotation.X
                    });
            }


            caster.EnqueueToVisible(spellStart, true);
        }

        private void SendSpellFinish()
        {
            if (status != SpellStatus.Finished)
                return;

            caster.EnqueueToVisible(new ServerSpellFinish
            {
                ServerUniqueId = CastingId,
            }, true);
        }

        private void SendSpellGo()
        {
            if (CastMethod == CastMethod.Aura && targets.FirstOrDefault(x => x.TargetSelectionState == TargetSelectionState.New) == null)
                return;

            var serverSpellGo = new ServerSpellGo
            {
                ServerUniqueId = CastingId,
                PrimaryDestination = new Position(caster.Position),
                Phase = currentPhase
            };

            byte targetCount = 0;
            foreach (SpellTargetInfo targetInfo in targets
                .Where(t => t.Effects.Count > 0 && t.TargetSelectionState == TargetSelectionState.New))
            {
                var networkTargetInfo = new TargetInfo
                {
                    UnitId = targetInfo.Entity.Guid,
                    Ndx = targetCount++,
                    TargetFlags = (byte)targetInfo.Flags,
                    InstanceCount = 1,
                    CombatResult = CombatResult.Hit
                };

                foreach (SpellTargetInfo.SpellTargetEffectInfo targetEffectInfo in targetInfo.Effects)
                {

                    if ((SpellEffectType)targetEffectInfo.Entry.EffectType == SpellEffectType.Proxy)
                        continue;

                    var networkTargetEffectInfo = new EffectInfo
                    {
                        Spell4EffectId = targetEffectInfo.Entry.Id,
                        EffectUniqueId = targetEffectInfo.EffectId,
                        DelayTime = targetEffectInfo.Entry.DelayTime,
                        TimeRemaining = duration > 0 ? (int)duration : -1
                    };

                    if (targetEffectInfo.Damage != null)
                    {
                        networkTargetEffectInfo.InfoType = 1;
                        networkTargetEffectInfo.DamageDescriptionData = new DamageDescription
                        {
                            RawDamage = targetEffectInfo.Damage.RawDamage,
                            RawScaledDamage = targetEffectInfo.Damage.RawScaledDamage,
                            AbsorbedAmount = targetEffectInfo.Damage.AbsorbedAmount,
                            ShieldAbsorbAmount = targetEffectInfo.Damage.ShieldAbsorbAmount,
                            AdjustedDamage = targetEffectInfo.Damage.AdjustedDamage,
                            OverkillAmount = targetEffectInfo.Damage.OverkillAmount,
                            KilledTarget = targetEffectInfo.Damage.KilledTarget,
                            CombatResult = targetEffectInfo.Damage.CombatResult,
                            DamageType = targetEffectInfo.Damage.DamageType
                        };
                    }

                    networkTargetInfo.EffectInfoData.Add(networkTargetEffectInfo);
                }

                serverSpellGo.TargetInfoData.Add(networkTargetInfo);
            }

            List<UnitEntity> unitsCasting = new List<UnitEntity>
                {
                    caster
                };

            foreach (UnitEntity unit in unitsCasting)
                serverSpellGo.InitialPositionData.Add(new Network.Message.Model.Shared.InitialPosition
                {
                    UnitId = unit.Guid,
                    Position = new Position(unit.Position),
                    TargetFlags = 3,
                    Yaw = unit.Rotation.X
                });

            foreach (UnitEntity unit in unitsCasting)
                foreach (Telegraph telegraph in telegraphs)
                    serverSpellGo.TelegraphPositionData.Add(new TelegraphPosition
                    {
                        TelegraphId = (ushort)telegraph.TelegraphDamage.Id,
                        AttachedUnitId = unit.Guid,
                        TargetFlags = 3,
                        Position = new Position(telegraph.Position),
                        Yaw = telegraph.Rotation.X
                    });

            caster.EnqueueToVisible(serverSpellGo, true);
        }

        private void SendBuffsApplied(List<uint> unitIds)
        {
            if (unitIds.Count == 0)
                return;

            var serverSpellBuffsApply = new ServerSpellBuffsApply();
            foreach (uint unitId in unitIds)
                serverSpellBuffsApply.spellTargets.Add(new ServerSpellBuffsApply.SpellTarget
                {
                    ServerUniqueId = CastingId,
                    TargetId = unitId,
                    InstanceCount = 1 // TODO: If something stacks, we may need to grab this from the target unit
                });
            caster.EnqueueToVisible(serverSpellBuffsApply, true);
        }

        public void SendBuffsRemoved(List<uint> unitIds)
        {
            if (unitIds.Count == 0)
                return;

            ServerSpellBuffsRemoved serverSpellBuffsRemoved = new ServerSpellBuffsRemoved
            {
                CastingId = CastingId,
                SpellTargets = unitIds
            };
            caster.EnqueueToVisible(serverSpellBuffsRemoved, true);
        }

        private void SendRemoveBuff(uint unitId)
        {
            caster.EnqueueToVisible(new ServerSpellBuffRemove
            {
                CastingId = CastingId,
                CasterId  = unitId
            }, true);
        }

        private void CheckPersistance(double lastTick)
        {
            if (caster is not Player player)
                return;

            if (parameters.SpellInfo.Entry.PrerequisiteIdCasterPersistence == 0 && parameters.SpellInfo.Entry.PrerequisiteIdTargetPersistence == 0)
                return;

            persistCheck.Update(lastTick);
            if (persistCheck.HasElapsed)
            {
                if (parameters.SpellInfo.Entry.PrerequisiteIdCasterPersistence > 0 && !PrerequisiteManager.Instance.Meets(player, parameters.SpellInfo.Entry.PrerequisiteIdCasterPersistence))
                    Finish();
                
                // TODO: Check if target can still persist

                persistCheck.Reset();
            }
        }

        /// <summary>
        /// Called when this <see cref="Spell"/> <see cref="SpellStatus"/> changes.
        /// </summary>
        protected virtual void OnStatusChange(SpellStatus previousStatus, SpellStatus status)
        {
            if (status == SpellStatus.Casting)
                SendSpellStart();
        }

        protected virtual bool CanFinish()
        {
            return (status == SpellStatus.Executing && !events.HasPendingEvent && !parameters.ForceCancelOnly) || status == SpellStatus.Finishing;
        }
    }
}