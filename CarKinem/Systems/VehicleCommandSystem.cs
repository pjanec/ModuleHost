using System.Numerics;
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Formation;
using Fdp.Kernel;

namespace CarKinem.Systems
{
    /// <summary>
    /// Processes vehicle command events.
    /// Runs early to update NavState before physics.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(CarKinematicsSystem))]
    public class VehicleCommandSystem : ComponentSystem
    {
        protected override void OnUpdate()
        {
            ProcessSpawnCommands();
            ProcessCreateFormationCommands();
            ProcessNavigateToPointCommands();
            ProcessFollowTrajectoryCommands();
            ProcessNavigateViaRoadCommands();
            ProcessJoinFormationCommands();
            ProcessLeaveFormationCommands();
            ProcessStopCommands();
            ProcessSetSpeedCommands();
        }
        
        private void ProcessSpawnCommands()
        {
            var events = World.Bus.Consume<CmdSpawnVehicle>();
            
            foreach (var cmd in events)
            {
                var entity = cmd.Entity;
                
                // Verify entity was pre-allocated and is alive
                if (!World.IsAlive(entity))
                {
                    // Console.WriteLine($"WARNING: CmdSpawnVehicle references dead entity {entity}");
                    continue;
                }
                
                // Add VehicleState component
                World.AddComponent(entity, new VehicleState
                {
                    Position = cmd.Position,
                    Forward = Vector2.Normalize(cmd.Heading),
                    Speed = 0f,
                    SteerAngle = 0f,
                    Accel = 0f,
                    Pitch = 0f,
                    Roll = 0f,
                    CurrentLaneIndex = -1
                });
                
                // Add VehicleParams component (use preset)
                var preset = VehiclePresets.GetPreset(cmd.Class);
                preset.Class = cmd.Class;  // Set class field
                World.AddComponent(entity, preset);
                
                // Add NavState component (idle)
                World.AddComponent(entity, new NavState
                {
                    Mode = NavigationMode.None,
                    RoadPhase = RoadGraphPhase.Approaching,
                    TrajectoryId = -1,
                    CurrentSegmentId = -1,
                    ProgressS = 0f,
                    TargetSpeed = 0f,
                    FinalDestination = cmd.Position,
                    ArrivalRadius = 2.0f,
                    SpeedErrorInt = 0f,
                    LastSteerCmd = 0f,
                    ReverseAllowed = 0,
                    HasArrived = 0,
                    IsBlocked = 0
                });
            }
        }

        private void ProcessCreateFormationCommands()
        {
            var events = World.Bus.Consume<CmdCreateFormation>();
            
            foreach (var cmd in events)
            {
                var leaderEntity = cmd.LeaderEntity;
                
                if (!World.IsAlive(leaderEntity))
                {
                    // Console.WriteLine($"WARNING: CmdCreateFormation references dead leader {leaderEntity}");
                    continue;
                }
                
                // Create/update FormationRoster component on leader
                FormationRoster roster;
                
                if (World.HasComponent<FormationRoster>(leaderEntity))
                {
                    // Update existing roster
                    roster = World.GetComponent<FormationRoster>(leaderEntity);
                }
                else
                {
                    // Create new roster
                    roster = new FormationRoster();
                    World.AddComponent(leaderEntity, roster);
                }
                
                // Configure roster
                roster.Type = cmd.Type;
                roster.Params = cmd.Params;
                roster.Count = 1;  // Leader only initially
                roster.SetMember(0, leaderEntity);  // Leader is always slot 0
                roster.SetSlotIndex(0, 0);
                
                World.SetComponent(leaderEntity, roster);
            }
        }

        private void ProcessNavigateToPointCommands()
        {
            var events = World.Bus.Consume<CmdNavigateToPoint>();
            
            foreach (var cmd in events)
            {
                var entity = cmd.Entity;
                
                if (!World.IsAlive(entity))
                    continue;
                
                var nav = World.GetComponent<NavState>(entity);
                nav.Mode = NavigationMode.None; // Direct navigation (no special mode)
                nav.FinalDestination = cmd.Destination;
                nav.ArrivalRadius = cmd.ArrivalRadius;
                nav.TargetSpeed = cmd.Speed;
                nav.HasArrived = 0;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessFollowTrajectoryCommands()
        {
            var events = World.Bus.Consume<CmdFollowTrajectory>();
            
            foreach (var cmd in events)
            {
                var entity = cmd.Entity;
                
                if (!World.IsAlive(entity))
                    continue;
                
                var nav = World.GetComponent<NavState>(entity);
                nav.Mode = NavigationMode.CustomTrajectory;
                nav.TrajectoryId = cmd.TrajectoryId;
                nav.ProgressS = 0f;
                nav.HasArrived = 0;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessNavigateViaRoadCommands()
        {
            var events = World.Bus.Consume<CmdNavigateViaRoad>();
            
            foreach (var cmd in events)
            {
                var entity = cmd.Entity;
                
                if (!World.IsAlive(entity))
                    continue;
                
                var nav = World.GetComponent<NavState>(entity);
                nav.Mode = NavigationMode.RoadGraph;
                nav.RoadPhase = RoadGraphPhase.Approaching;
                nav.FinalDestination = cmd.Destination;
                nav.ArrivalRadius = cmd.ArrivalRadius;
                nav.CurrentSegmentId = -1;
                nav.ProgressS = 0f;
                nav.HasArrived = 0;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessJoinFormationCommands()
        {
            var events = World.Bus.Consume<CmdJoinFormation>();
            
            foreach (var cmd in events)
            {
                var followerEntity = cmd.Entity;
                var leaderEntity = cmd.LeaderEntity;
                
                if (!World.IsAlive(followerEntity) || !World.IsAlive(leaderEntity))
                    continue;
                
                // Verify leader has a formation
                if (!World.HasComponent<FormationRoster>(leaderEntity))
                {
                    // Console.WriteLine($"WARNING: CmdJoinFormation: Leader {leaderEntity} has no FormationRoster");
                    continue;
                }
                
                // Add FormationMember component if not exists
                if (!World.HasComponent<FormationMember>(followerEntity))
                {
                    World.AddComponent(followerEntity, new FormationMember());
                }
                
                var member = World.GetComponent<FormationMember>(followerEntity);
                member.LeaderEntityId = leaderEntity.Index;  // Store leader index
                member.SlotIndex = (ushort)cmd.SlotIndex;
                member.State = FormationMemberState.Rejoining;
                member.IsInFormation = 1;
                World.SetComponent(followerEntity, member);
                
                // Add follower to leader's roster
                var roster = World.GetComponent<FormationRoster>(leaderEntity);
                if (roster.Count < 16)  // Max 16 members
                {
                    roster.SetMember(roster.Count, followerEntity);
                    roster.SetSlotIndex(roster.Count, (ushort)cmd.SlotIndex);
                    roster.Count++;
                    World.SetComponent(leaderEntity, roster);
                }
                
                // Set follower navigation mode to Formation
                var nav = World.GetComponent<NavState>(followerEntity);
                nav.Mode = NavigationMode.Formation;
                nav.HasArrived = 0;
                World.SetComponent(followerEntity, nav);
            }
        }
        
        private void ProcessLeaveFormationCommands()
        {
            var events = World.Bus.Consume<CmdLeaveFormation>();
            
            foreach (var cmd in events)
            {
                var entity = cmd.Entity;
                
                if (!World.IsAlive(entity))
                    continue;
                
                var nav = World.GetComponent<NavState>(entity);
                nav.Mode = NavigationMode.None;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessStopCommands()
        {
            var events = World.Bus.Consume<CmdStop>();
            
            foreach (var cmd in events)
            {
                var entity = cmd.Entity;
                
                if (!World.IsAlive(entity))
                    continue;
                
                var nav = World.GetComponent<NavState>(entity);
                nav.Mode = NavigationMode.None;
                nav.TargetSpeed = 0f;
                
                World.SetComponent(entity, nav);
            }
        }
        
        private void ProcessSetSpeedCommands()
        {
            var events = World.Bus.Consume<CmdSetSpeed>();
            
            foreach (var cmd in events)
            {
                var entity = cmd.Entity;
                
                if (!World.IsAlive(entity))
                    continue;
                
                var nav = World.GetComponent<NavState>(entity);
                nav.TargetSpeed = cmd.Speed;
                
                World.SetComponent(entity, nav);
            }
        }
    }
}
