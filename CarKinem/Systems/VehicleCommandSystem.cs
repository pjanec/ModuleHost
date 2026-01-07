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
            ProcessNavigateToPointCommands();
            ProcessFollowTrajectoryCommands();
            ProcessNavigateViaRoadCommands();
            ProcessJoinFormationCommands();
            ProcessLeaveFormationCommands();
            ProcessStopCommands();
            ProcessSetSpeedCommands();
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
                var entity = cmd.Entity;
                
                if (!World.IsAlive(entity))
                    continue;
                
                // Add FormationMember component if not exists
                if (!World.HasComponent<FormationMember>(entity))
                {
                    World.AddComponent(entity, new FormationMember());
                }
                
                var member = World.GetComponent<FormationMember>(entity);
                member.LeaderEntityId = cmd.FormationId;
                member.SlotIndex = (ushort)cmd.SlotIndex;
                member.State = FormationMemberState.Rejoining;
                World.SetComponent(entity, member);
                
                var nav = World.GetComponent<NavState>(entity);
                nav.Mode = NavigationMode.Formation;
                nav.HasArrived = 0;
                World.SetComponent(entity, nav);
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
