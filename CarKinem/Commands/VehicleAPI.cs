using System.Numerics;
using CarKinem.Core;
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
        /// </summary>
        public void JoinFormation(Entity entity, int formationId, int slotIndex)
        {
            var cmd = _view.GetCommandBuffer();
            cmd.PublishEvent(new CmdJoinFormation
            {
                Entity = entity,
                FormationId = formationId,
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
