using System.Numerics;
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Systems;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using Xunit;

namespace CarKinem.Tests.Commands
{
    public class VehicleCommandSystemTests
    {
        [Fact]
        public void NavigateToPoint_SetsNavState()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<NavState>();
            // Register events to ensure streams exist
            repo.RegisterEvent<CmdNavigateToPoint>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new VehicleState());
            repo.AddComponent(entity, new NavState { Mode = NavigationMode.None });
            
            // Issue command
            var api = new VehicleAPI(repo);
            api.NavigateToPoint(entity, new Vector2(100, 100), 2.0f, 15.0f);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            // Process commands
            system.Run();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(new Vector2(100, 100), nav.FinalDestination);
            Assert.Equal(2.0f, nav.ArrivalRadius);
            Assert.Equal(15.0f, nav.TargetSpeed);
            Assert.Equal(NavigationMode.None, nav.Mode); // Implementation sets None for direct nav
            
            repo.Dispose();
        }
        
        [Fact]
        public void FollowTrajectory_SetsTrajectoryMode()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdFollowTrajectory>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState());
            
            var api = new VehicleAPI(repo);
            api.FollowTrajectory(entity, trajectoryId: 42, looped: true);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            system.Run();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(NavigationMode.CustomTrajectory, nav.Mode);
            Assert.Equal(42, nav.TrajectoryId);
            
            repo.Dispose();
        }

        [Fact]
        public void NavigateViaRoad_SetsRoadMode()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdNavigateViaRoad>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState());
            
            var api = new VehicleAPI(repo);
            api.NavigateViaRoad(entity, new Vector2(200, 200), 5.0f);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            system.Run();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(NavigationMode.RoadGraph, nav.Mode);
            Assert.Equal(RoadGraphPhase.Approaching, nav.RoadPhase);
            Assert.Equal(new Vector2(200, 200), nav.FinalDestination);
            Assert.Equal(5.0f, nav.ArrivalRadius);
            
            repo.Dispose();
        }

        [Fact]
        public void JoinFormation_SetsFormationMemberAndMode()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterComponent<FormationMember>();
            repo.RegisterComponent<FormationRoster>();
            repo.RegisterEvent<CmdJoinFormation>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState());
            // FormationMember added dynamically by system if missing
            
            var leader = repo.CreateEntity();
            repo.AddComponent(leader, new FormationRoster { Count = 1 });
            
            var api = new VehicleAPI(repo);
            api.JoinFormation(entity, leaderEntity: leader, slotIndex: 2);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            system.Run();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(NavigationMode.Formation, nav.Mode);
            
            Assert.True(repo.HasComponent<FormationMember>(entity));
            var member = repo.GetComponent<FormationMember>(entity);
            Assert.Equal(leader.Index, member.LeaderEntityId);
            Assert.Equal(2, member.SlotIndex);
            Assert.Equal(FormationMemberState.Rejoining, member.State);
            
            repo.Dispose();
        }

        [Fact]
        public void LeaveFormation_SetsModeToNone()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdLeaveFormation>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState { Mode = NavigationMode.Formation });
            
            var api = new VehicleAPI(repo);
            api.LeaveFormation(entity);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            system.Run();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(NavigationMode.None, nav.Mode);
            
            repo.Dispose();
        }

        [Fact]
        public void Stop_SetsTargetSpeedZero()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdStop>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState { TargetSpeed = 20.0f });
            
            var api = new VehicleAPI(repo);
            api.Stop(entity);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            system.Run();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(0.0f, nav.TargetSpeed);
            Assert.Equal(NavigationMode.None, nav.Mode);
            
            repo.Dispose();
        }

        [Fact]
        public void SetSpeed_UpdatesTargetSpeed()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdSetSpeed>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState { TargetSpeed = 10.0f });
            
            var api = new VehicleAPI(repo);
            api.SetSpeed(entity, 30.0f);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            system.Run();
            
            var nav = repo.GetComponent<NavState>(entity);
            Assert.Equal(30.0f, nav.TargetSpeed);
            
            repo.Dispose();
        }

        [Fact]
        public void Command_IgnoresDeadEntity()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdSetSpeed>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var entity = repo.CreateEntity();
            repo.AddComponent(entity, new NavState { TargetSpeed = 10.0f });
            var id = entity.Index;
            var gen = entity.Generation;
            
            repo.DestroyEntity(entity);
            
            // Reuse index with new generation (if any)
            // But here we just check checking old handle
            
            var api = new VehicleAPI(repo);
            // Command targeting the DEAD entity
            api.SetSpeed(entity, 30.0f);
            
            // Playback and Swap
            var cb = ((ISimulationView)repo).GetCommandBuffer();
            ((EntityCommandBuffer)cb).Playback(repo);
            repo.Bus.SwapBuffers();
            
            system.Run();
            
            // Ideally should not crash and do nothing.
            // Functionally hard to verify "nothing happened" to a dead entity.
            // But we can verify no exception was thrown.
            
            repo.Dispose();
        }
    }
}
