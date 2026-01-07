using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CarKinem.Avoidance;
using CarKinem.Controllers;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Road;
using CarKinem.Spatial;
using CarKinem.Trajectory;
using Fdp.Kernel;

namespace CarKinem.Systems
{
    /// <summary>
    /// Main vehicle physics system.
    /// Runs in parallel for all vehicles.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SpatialHashSystem))]
    [UpdateAfter(typeof(FormationTargetSystem))]
    public class CarKinematicsSystem : ComponentSystem
    {
        private readonly RoadNetworkBlob _roadNetwork;
        private readonly TrajectoryPoolManager _trajectoryPool;
        
        public CarKinematicsSystem(RoadNetworkBlob roadNetwork, TrajectoryPoolManager trajectoryPool)
        {
            _roadNetwork = roadNetwork;
            _trajectoryPool = trajectoryPool;
        }

        protected override void OnUpdate()
        {
            float dt = DeltaTime;
            
            // Read spatial grid from singleton (Data-Oriented dependency)
            // If singleton is missing (e.g. first frame or testing), handle gracefully?
            // World.GetSingleton throws if missing. 
            // In tests we register it.
            if (!World.HasSingleton<SpatialGridData>()) return;
            
            var gridData = World.GetSingleton<SpatialGridData>();
            var spatialGrid = gridData.Grid;
            
            // Get all vehicles
            var query = World.Query()
                .With<VehicleState>()
                .With<VehicleParams>()
                .With<NavState>()
                .Build();
            
            // Parallel update using FDP's zero-GC ForEachParallel
            query.ForEachParallel((entity) =>
            {
                UpdateVehicle(entity, dt, spatialGrid);
            });
        }
        
        private void UpdateVehicle(Entity entity, float dt, SpatialHashGrid spatialGrid)
        {
            var state = World.GetComponent<VehicleState>(entity);
            var @params = World.GetComponent<VehicleParams>(entity);
            var nav = World.GetComponent<NavState>(entity);
            
            // Determine target (position, heading, speed) based on navigation mode
            Vector2 targetPos;
            Vector2 targetHeading;
            float targetSpeed;
            
            switch (nav.Mode)
            {
                case NavigationMode.RoadGraph:
                    // This updates nav state internal phase/progress, so we pass by ref
                    (targetPos, targetHeading, targetSpeed) = RoadGraphNavigator.UpdateRoadGraphNavigation(
                        ref nav, state.Position, _roadNetwork);
                    break;
                    
                case NavigationMode.CustomTrajectory:
                    (targetPos, targetHeading, targetSpeed) = SampleCustomTrajectory(ref nav);
                    break;
                    
                case NavigationMode.Formation:
                    (targetPos, targetHeading, targetSpeed) = GetFormationTarget(entity);
                    break;
                    
                case NavigationMode.None:
                default:
                    // If we have a destination and we are not in a specific mode, drive to point
                    // Simple "Drive to point" logic
                    if (nav.HasArrived == 0 && nav.TargetSpeed > 0 && Vector2.DistanceSquared(state.Position, nav.FinalDestination) > nav.ArrivalRadius * nav.ArrivalRadius)
                    {
                         Vector2 toDest = nav.FinalDestination - state.Position;
                         targetHeading = Vector2.Normalize(toDest);
                         targetPos = state.Position + targetHeading; // Look ahead
                         targetSpeed = nav.TargetSpeed;
                    }
                    else
                    {
                        // Idle
                        targetPos = state.Position;
                        targetHeading = state.Forward;
                        targetSpeed = 0f;
                    }
                    break;
            }
            
            // Calculate desired velocity
            Vector2 desiredVelocity = targetHeading * targetSpeed;
            
            // Apply collision avoidance
            Vector2 avoidanceVelocity = ApplyCollisionAvoidance(
                desiredVelocity, state.Position, state.Forward * state.Speed, 
                spatialGrid, @params);
            
            // Pure Pursuit steering
            float steerAngle = PurePursuitController.CalculateSteering(
                state.Position,
                state.Forward,
                avoidanceVelocity,
                state.Speed,
                @params.WheelBase,
                @params.LookaheadTimeMin,
                @params.LookaheadTimeMax,
                @params.MaxSteerAngle);
            
            // Speed control
            float targetSpeedAfterAvoidance = avoidanceVelocity.Length();
            float accel = SpeedController.CalculateAcceleration(
                state.Speed,
                targetSpeedAfterAvoidance,
                @params.AccelGain,
                @params.MaxAccel,
                @params.MaxDecel);
            
            // Integrate bicycle model
            BicycleModel.Integrate(ref state, steerAngle, accel, dt, @params.WheelBase);
            
            // Update progress (for trajectory/road modes)
            if (nav.Mode == NavigationMode.CustomTrajectory || nav.Mode == NavigationMode.RoadGraph)
            {
                nav.ProgressS += state.Speed * dt;
            }
            
            // Write back state
            World.SetComponent(entity, state);
            World.SetComponent(entity, nav);
        }
        
        private (Vector2 pos, Vector2 heading, float speed) SampleCustomTrajectory(ref NavState nav)
        {
            var (pos, tangent, speed) = _trajectoryPool.SampleTrajectory(nav.TrajectoryId, nav.ProgressS);
            return (pos, tangent, speed);
        }
        
        private (Vector2 pos, Vector2 heading, float speed) GetFormationTarget(Entity entity)
        {
            if (!World.HasComponent<FormationTarget>(entity))
            {
                var state = World.GetComponent<VehicleState>(entity);
                return (state.Position, state.Forward, 0f);
            }
            
            var target = World.GetComponent<FormationTarget>(entity);
            return (target.TargetPosition, target.TargetHeading, target.TargetSpeed);
        }
        
        private Vector2 ApplyCollisionAvoidance(Vector2 preferredVel, Vector2 selfPos, 
            Vector2 selfVel, SpatialHashGrid spatialGrid, VehicleParams @params)
        {
            // Query neighbors within avoidance radius
            Span<(int, Vector2)> neighbors = stackalloc (int, Vector2)[32];
            int count = spatialGrid.QueryNeighbors(selfPos, @params.AvoidanceRadius * 2.5f, neighbors);
            
            if (count == 0)
                return preferredVel;
            
            // Convert to (pos, vel) format for RVO
            Span<(Vector2 pos, Vector2 vel)> neighborData = stackalloc (Vector2, Vector2)[count];
            for (int i = 0; i < count; i++)
            {
                var (entityId, pos) = neighbors[i];
                
                // Fetch neighbor velocity (SAFE - read-only access)
                // Use GetEntity to reconstruct handle checking active generation
                var neighborEntity = World.GetEntity(entityId); 
                
                // Check if entity is valid and has VehicleState
                if (!neighborEntity.IsNull && World.HasComponent<VehicleState>(neighborEntity))
                {
                    var neighborState = World.GetComponent<VehicleState>(neighborEntity);
                    Vector2 neighborVel = neighborState.Forward * neighborState.Speed;
                    neighborData[i] = (pos, neighborVel);
                }
                else
                {
                    // Fallback to stationary if entity is invalid
                    neighborData[i] = (pos, Vector2.Zero);
                }
            }
            
            return RVOAvoidance.ApplyAvoidance(
                preferredVel, selfPos, selfVel, neighborData,
                @params.AvoidanceRadius, @params.MaxSpeedFwd);
        }
    }
}
