using System;
using System.Numerics;
using CarKinem.Core;
using CarKinem.Formation;
using Fdp.Kernel;

namespace CarKinem.Systems
{
    /// <summary>
    /// Calculates formation slot targets for members.
    /// Runs before CarKinematicsSystem.
    /// </summary>
    [UpdateBefore(typeof(CarKinematicsSystem))]
    public class FormationTargetSystem : ComponentSystem
    {
        private readonly FormationTemplateManager _templateManager;
        
        public FormationTargetSystem(FormationTemplateManager templateManager)
        {
            _templateManager = templateManager;
        }
        
        protected override void OnUpdate()
        {
            // Query all formations
            var formationQuery = World.Query().With<FormationRoster>().Build();
            
            foreach (var formationEntity in formationQuery)
            {
                var roster = World.GetComponent<FormationRoster>(formationEntity);
                UpdateFormation(roster);
            }
        }
        
        private unsafe void UpdateFormation(FormationRoster roster)
        {
            if (roster.Count == 0)
                return;
            
            // Get leader entity
            int leaderEntityId = roster.MemberEntityIds[0];
            var leaderEntity = new Entity(leaderEntityId, 0); // Generation 0 is technically invalid, usually we need real Entity. 
                                                            // But FormationRoster stores "Entity IDs".
                                                            // If we don't store generation in roster, we risk stale refs.
                                                            // Assuming IDs are valid indices for now.
                                                            // However, accessing World requires Generation check usually.
                                                            // Since we don't have generation in FormationRoster (only int[]), 
                                                            // this is a potential design flaw in BATCH-CK-01 or just a simplification.
                                                            // But Entity constructor requires generation.
                                                            // We can hack it by fetching generation from repository if index is valid?
                                                            // World.GetEntityIndex().GetHeader(index).Generation...
                                                            // But we don't have access to EntityIndex easily here without casting World.
                                                            // Let's assume the IDs in Roster are just Indices and we hope the entity is alive 
                                                            // and we get the current generation from World if possible...
                                                            // World.IsAlive takes Entity.
                                                            
                                                            // Using special Entity constructor or forcing logic? 
                                                            // World.GetHeader(index) is exposed in my recent view of EntityRepository.
                                                            // Let's use that to reconstruct valid Entity handle.
            
            if (leaderEntityId < 0) return;
            
            ref var leaderHeader = ref World.GetHeader(leaderEntityId);
            if (!leaderHeader.IsActive) return;
            
            leaderEntity = new Entity(leaderEntityId, leaderHeader.Generation);
            
            if (!World.IsAlive(leaderEntity))
                return;
            
            var leaderState = World.GetComponent<VehicleState>(leaderEntity);
            var template = _templateManager.GetTemplate(roster.Type);
            
            // Update each member's target
            for (int i = 1; i < roster.Count; i++) // Start at 1 (skip leader)
            {
                int memberEntityId = roster.MemberEntityIds[i];
                if (memberEntityId < 0) continue;
                
                ref var memberHeader = ref World.GetHeader(memberEntityId);
                if (!memberHeader.IsActive) continue;
                
                var memberEntity = new Entity(memberEntityId, memberHeader.Generation);
                
                int slotIndex = roster.SlotIndices[i];
                
                // Calculate slot position
                Vector2 slotPos = template.GetSlotPosition(slotIndex, 
                    leaderState.Position, leaderState.Forward);
                
                // Get/create FormationTarget component
                if (!World.HasComponent<FormationTarget>(memberEntity))
                {
                    World.AddComponent(memberEntity, new FormationTarget());
                }
                
                // We need read/write access. 
                // Since I can't call GetComponentRef easily based on recent issues (or can I?),
                // I'll Get, Modify, Set.
                var target = World.GetComponent<FormationTarget>(memberEntity);
                target.TargetPosition = slotPos;
                target.TargetHeading = leaderState.Forward;
                target.TargetSpeed = leaderState.Speed;
                World.AddComponent(memberEntity, target); // AddComponent overwrites
                
                // Update member state based on distance to slot
                // We need to modify FormationMember component.
                if (World.HasComponent<FormationMember>(memberEntity))
                {
                    var member = World.GetComponent<FormationMember>(memberEntity);
                    var memberState = World.GetComponent<VehicleState>(memberEntity);
                    
                    float distToSlot = Vector2.Distance(memberState.Position, slotPos);
                    
                    if (distToSlot < roster.Params.ArrivalThreshold)
                    {
                        member.State = FormationMemberState.InSlot;
                    }
                    else if (distToSlot < roster.Params.BreakDistance * 0.5f) // Heuristic for CatchUp
                    {
                        member.State = FormationMemberState.CatchingUp;
                    }
                    else if (distToSlot < roster.Params.BreakDistance)
                    {
                        member.State = FormationMemberState.Rejoining;
                    }
                    else
                    {
                        member.State = FormationMemberState.Broken;
                    }
                    
                    World.AddComponent(memberEntity, member);
                }
            }
        }
    }
}
