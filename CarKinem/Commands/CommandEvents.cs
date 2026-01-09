using System.Numerics;
using CarKinem.Core;
using Fdp.Kernel;

namespace CarKinem.Commands
{
    [EventId(2101)]
    public struct CmdNavigateToPoint
    {
        public Entity Entity;
        public Vector2 Destination;
        public float ArrivalRadius;
        public float Speed;
    }
    
    [EventId(2102)]
    public struct CmdFollowTrajectory
    {
        public Entity Entity;
        public int TrajectoryId;
        public byte Looped;
    }
    
    [EventId(2103)]
    public struct CmdNavigateViaRoad
    {
        public Entity Entity;
        public Vector2 Destination;
        public float ArrivalRadius;
    }
    
    [EventId(2104)]
    public struct CmdJoinFormation
    {
        public Entity Entity;
        public int FormationId;
        public int SlotIndex;
    }
    
    [EventId(2105)]
    public struct CmdLeaveFormation
    {
        public Entity Entity;
    }
    
    [EventId(2106)]
    public struct CmdStop
    {
        public Entity Entity;
    }
    
    [EventId(2107)]
    public struct CmdSetSpeed
    {
        public Entity Entity;
        public float Speed;
    }
}
