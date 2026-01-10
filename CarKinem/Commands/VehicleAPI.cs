using System.Numerics;
using CarKinem.Core;
using CarKinem.Formation;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace CarKinem.Commands
{
    /// <summary>
    /// High-level API for vehicle commands.
    /// Facade over event system.
    /// </summary>
    public class VehicleAPI
    {
        private readonly ISimulationView _view;
        
        public VehicleAPI(ISimulationView view)
        {
            _view = view;
        }

        /// <summary>
        /// Spawn a new vehicle at the specified position.
        /// Note: Entity must be pre-allocated via command buffer.
        /// </summary>
        public void SpawnVehicle(Entity entity, Vector2 position, Vector2 heading, 
            VehicleClass vehicleClass = VehicleClass.PersonalCar)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdSpawnVehicle
            {
                Entity = entity,
                Position = position,
                Heading = heading,
                Class = vehicleClass
            });
        }
        
        /// <summary>
        /// Create a formation with the specified leader.
        /// </summary>
        public void CreateFormation(Entity leaderEntity, FormationType type, 
            FormationParams? parameters = null)
        {
            var cmd = _view.GetCommandBuffer();
            
            // Use default params if not specified
            var params_ = parameters ?? new FormationParams
            {
                Spacing = 5.0f,
                WedgeAngleRad = 0.52f,  // ~30 degrees
                MaxCatchUpFactor = 1.2f,
                BreakDistance = 50.0f,
                ArrivalThreshold = 2.0f,
                SpeedFilterTau = 0.5f
            };
            
            cmd.PublishEvent(new CmdCreateFormation
            {
                LeaderEntity = leaderEntity,
                Type = type,
                Params = params_
            });
        }
        
        /// <summary>
        /// Command vehicle to navigate to a point and stop.
        /// </summary>
        public void NavigateToPoint(Entity entity, Vector2 destination, 
            float arrivalRadius = 2.0f, float speed = 10.0f)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdNavigateToPoint
            {
                Entity = entity,
                Destination = destination,
                ArrivalRadius = arrivalRadius,
                Speed = speed
            });
        }
        
        /// <summary>
        /// Command vehicle to follow a custom trajectory.
        /// </summary>
        public void FollowTrajectory(Entity entity, int trajectoryId, bool looped = false)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdFollowTrajectory
            {
                Entity = entity,
                TrajectoryId = trajectoryId,
                Looped = (byte)(looped ? 1 : 0)
            });
        }
        
        /// <summary>
        /// Command vehicle to navigate using road network.
        /// </summary>
        public void NavigateViaRoad(Entity entity, Vector2 destination, 
            float arrivalRadius = 2.0f)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdNavigateViaRoad
            {
                Entity = entity,
                Destination = destination,
                ArrivalRadius = arrivalRadius
            });
        }
        
        /// <summary>
        /// Command vehicle to join a formation.
        /// Updated signature to use leader entity.
        /// </summary>
        public void JoinFormation(Entity followerEntity, Entity leaderEntity, int slotIndex = -1)
        {
            var cmd = _view.GetCommandBuffer();
            
            // Auto-assign slot if not specified
            if (slotIndex < 0)
            {
                // TODO: Query roster to find next available slot
                slotIndex = 1;  // Default to slot 1 (leader is 0)
            }
            
            cmd.PublishEvent(new CmdJoinFormation
            {
                Entity = followerEntity,
                LeaderEntity = leaderEntity,
                SlotIndex = slotIndex
            });
        }
        
        /// <summary>
        /// Command vehicle to leave its formation.
        /// </summary>
        public void LeaveFormation(Entity entity)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdLeaveFormation
            {
                Entity = entity
            });
        }
        
        /// <summary>
        /// Command vehicle to stop.
        /// </summary>
        public void Stop(Entity entity)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdStop
            {
                Entity = entity
            });
        }
        
        /// <summary>
        /// Set vehicle target speed.
        /// </summary>
        public void SetSpeed(Entity entity, float speed)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdSetSpeed
            {
                Entity = entity,
                Speed = speed
            });
        }
    }
}
