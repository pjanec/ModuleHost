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
    [UpdateInGroup(typeof(SimulationSystemGroup))]
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
                UpdateFormation(ref roster);
            }
        }
        
        private void UpdateFormation(ref FormationRoster roster)
        {
            if (roster.Count == 0)
                return;
            
            // Get leader entity
            Entity leaderEntity = roster.GetMember(0);
            
            if (!World.IsAlive(leaderEntity))
                return;
            
            var leaderState = World.GetComponent<VehicleState>(leaderEntity);
            var template = _templateManager.GetTemplate(roster.Type);
            
            // Update each member's target
            for (int i = 1; i < roster.Count; i++) // Start at 1 (skip leader)
            {
                Entity memberEntity = roster.GetMember(i);
                
                if (!World.IsAlive(memberEntity))
                    continue;
                
                int slotIndex = roster.GetSlotIndex(i);
                
                // Calculate slot position
                Vector2 slotPos = template.GetSlotPosition(slotIndex, 
                    leaderState.Position, leaderState.Forward);
                
                // Get/create FormationTarget component
                if (!World.HasComponent<FormationTarget>(memberEntity))
                {
                    World.AddComponent(memberEntity, new FormationTarget());
                }
                
                var target = World.GetComponent<FormationTarget>(memberEntity);
                target.TargetPosition = slotPos;
                target.TargetHeading = leaderState.Forward;
                target.TargetSpeed = leaderState.Speed;
                World.SetComponent(memberEntity, target);
                
                // Update member state based on distance to slot
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
                    
                    World.SetComponent(memberEntity, member);
                }
            }
        }
    }
}
