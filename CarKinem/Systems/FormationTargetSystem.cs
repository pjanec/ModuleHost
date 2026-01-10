using System;
using System.Numerics;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Trajectory;
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
        private readonly TrajectoryPoolManager _trajectoryPool;
        
        public FormationTargetSystem(FormationTemplateManager templateManager, TrajectoryPoolManager trajectoryPool)
        {
            _templateManager = templateManager;
            _trajectoryPool = trajectoryPool;
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
            
            // Default Formation Orientation (Rigid fallback)
            Vector2 formationHeading = leaderState.Forward;
            
            // Trajectory Following Logic ("Ghost Rails")
            bool hasTrajectory = false;
            CustomTrajectory trajectory = default;
            float leaderS = 0f;

            if (World.HasComponent<NavState>(leaderEntity))
            {
                var nav = World.GetComponent<NavState>(leaderEntity);
                if (nav.Mode == NavigationMode.CustomTrajectory && nav.TrajectoryId > 0)
                {
                     if (_trajectoryPool.TryGetTrajectory(nav.TrajectoryId, out trajectory))
                     {
                         hasTrajectory = true;
                         leaderS = nav.ProgressS;
                         
                         // Update fallback heading to path tangent at leader position
                         // (Still useful if we fallback for some reason)
                         var (_, tangent, _) = _trajectoryPool.SampleTrajectory(trajectory.Id, leaderS);
                         if (tangent != Vector2.Zero) 
                             formationHeading = Vector2.Normalize(tangent);
                     }
                }
            }
            
            // Update each member's target
            for (int i = 1; i < roster.Count; i++) // Start at 1 (skip leader)
            {
                Entity memberEntity = roster.GetMember(i);
                
                if (!World.IsAlive(memberEntity))
                    continue;
                
                int slotIndex = roster.GetSlotIndex(i);
                Vector2 slotPos;
                Vector2 slotHeading;
                
                // Try to use Trajectory Following (Curved Formation)
                if (hasTrajectory && template.SlotOffsets != null && slotIndex < template.SlotOffsets.Length)
                {
                    Vector2 offset = template.SlotOffsets[slotIndex];
                    // offset.X = Longitudinal (Along track), offset.Y = Lateral (Right of track)
                    
                    float targetS = leaderS + offset.X;
                    
                    // Sample and Extrapolate if needed
                    Vector2 pathPos;
                    Vector2 pathTangent;
                    
                    if (trajectory.IsLooped == 0)
                    {
                        // Linear Extrapolation for start/end
                        if (targetS < 0)
                        {
                            var (p0, t0, _) = _trajectoryPool.SampleTrajectory(trajectory.Id, 0);
                            pathPos = p0 + t0 * targetS; // targetS is negative distance
                            pathTangent = t0;
                        }
                        else if (targetS > trajectory.TotalLength)
                        {
                            var (pe, te, _) = _trajectoryPool.SampleTrajectory(trajectory.Id, trajectory.TotalLength);
                            pathPos = pe + te * (targetS - trajectory.TotalLength);
                            pathTangent = te;
                        }
                        else
                        {
                            // On path
                            (pathPos, pathTangent, _) = _trajectoryPool.SampleTrajectory(trajectory.Id, targetS);
                        }
                    }
                    else
                    {
                        // Looped: SampleTrajectory handles wrapping
                        (pathPos, pathTangent, _) = _trajectoryPool.SampleTrajectory(trajectory.Id, targetS);
                    }
                    
                    // Apply Lateral Offset
                    Vector2 pathRight = new Vector2(pathTangent.Y, -pathTangent.X);
                    slotPos = pathPos + pathRight * offset.Y;
                    slotHeading = pathTangent;
                }
                else
                {
                    // Fallback: Rigid Body formation relative to leader's current position/heading
                    slotPos = template.GetSlotPosition(slotIndex, leaderState.Position, formationHeading);
                    slotHeading = formationHeading;
                }
                
                // Get/create FormationTarget component
                if (!World.HasComponent<FormationTarget>(memberEntity))
                {
                    World.AddComponent(memberEntity, new FormationTarget());
                }
                
                var target = World.GetComponent<FormationTarget>(memberEntity);
                target.TargetPosition = slotPos;
                target.TargetHeading = slotHeading; 
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
