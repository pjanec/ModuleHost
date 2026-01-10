using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Systems;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Commands
{
    public class FormationCreationTests
    {
        [Fact]
        public void CreateFormation_AddsRosterToLeader()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<FormationRoster>();
            repo.RegisterEvent<CmdCreateFormation>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var leaderEntity = repo.CreateEntity();
            
            // Create formation
            repo.Bus.Publish(new CmdCreateFormation
            {
                LeaderEntity = leaderEntity,
                Type = FormationType.Column,
                Params = new FormationParams
                {
                    Spacing = 5.0f,
                    MaxCatchUpFactor = 1.2f,
                    BreakDistance = 50.0f,
                    ArrivalThreshold = 2.0f
                }
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Verify roster
            Assert.True(repo.HasComponent<FormationRoster>(leaderEntity));
            var roster = repo.GetComponent<FormationRoster>(leaderEntity);
            Assert.Equal(FormationType.Column, roster.Type);
            Assert.Equal(1, roster.Count);  // Leader only
            Assert.Equal(5.0f, roster.Params.Spacing);
            
            repo.Dispose();
        }
        
        [Fact]
        public void JoinFormation_AddsFollowerToRoster()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<NavState>();
            repo.RegisterComponent<FormationMember>();
            repo.RegisterComponent<FormationRoster>();
            repo.RegisterEvent<CmdCreateFormation>();
            repo.RegisterEvent<CmdJoinFormation>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            var leaderEntity = repo.CreateEntity();
            repo.AddComponent(leaderEntity, new VehicleState());
            repo.AddComponent(leaderEntity, new NavState());
            
            var followerEntity = repo.CreateEntity();
            repo.AddComponent(followerEntity, new VehicleState());
            repo.AddComponent(followerEntity, new NavState());
            
            // Create formation
            repo.Bus.Publish(new CmdCreateFormation
            {
                LeaderEntity = leaderEntity,
                Type = FormationType.Column,
                Params = new FormationParams { Spacing = 5.0f }
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Join formation
            repo.Bus.Publish(new CmdJoinFormation
            {
                Entity = followerEntity,
                LeaderEntity = leaderEntity,
                SlotIndex = 1
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Verify follower
            Assert.True(repo.HasComponent<FormationMember>(followerEntity));
            var member = repo.GetComponent<FormationMember>(followerEntity);
            Assert.Equal(leaderEntity.Index, member.LeaderEntityId);
            Assert.Equal(1, member.SlotIndex);
            
            // Verify roster
            var roster = repo.GetComponent<FormationRoster>(leaderEntity);
            Assert.Equal(2, roster.Count);  // Leader + follower
            
            repo.Dispose();
        }
    }
}
