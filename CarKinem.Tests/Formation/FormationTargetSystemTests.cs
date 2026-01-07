using System;
using System.Numerics;
using CarKinem.Core;
using CarKinem.Formation;
using CarKinem.Systems;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Formation
{
    public class FormationTargetSystemTests
    {
        [Fact]

        public unsafe void System_UpdatesFormationTargets()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<FormationRoster>();
            repo.RegisterComponent<FormationTarget>();
            repo.RegisterComponent<FormationMember>();

            var templateManager = new FormationTemplateManager();
            var system = new FormationTargetSystem(templateManager);
            system.Create(repo);

            // Create Leader
            var leader = repo.CreateEntity();
            repo.AddComponent(leader, new VehicleState { Position = new Vector2(100, 100), Forward = new Vector2(1, 0), Speed = 10f });

            // Create Follower
            var follower = repo.CreateEntity();
            repo.AddComponent(follower, new VehicleState { Position = new Vector2(0, 0), Forward = new Vector2(1, 0), Speed = 0f });
            repo.AddComponent(follower, new FormationMember { State = FormationMemberState.Broken });

            // Create Formation Roster Entity
            var rosterEntity = repo.CreateEntity();
            var roster = new FormationRoster();
            
            // Fixed buffer assignment needs no 'new' allocation, they are inline.
            roster.SetMember(0, leader); // Leader at index 0
            roster.SetMember(1, follower);
            roster.SetSlotIndex(1, 0); // Use first slot
            roster.Count = 2;
            roster.Type = FormationType.Column;
            roster.Params = new FormationParams { ArrivalThreshold = 1f, BreakDistance = 20f, MaxCatchUpFactor = 1.2f };
            
            repo.AddComponent(rosterEntity, roster);

            system.Run();

            // Check follower target
            Assert.True(repo.HasComponent<FormationTarget>(follower));
            var target = repo.GetComponent<FormationTarget>(follower);
            
            // Expected slot pos: Leader (100,100) + Offset of slot 0 in Column (-5, 0) -> (95, 100)
            Assert.Equal(95f, target.TargetPosition.X, 0.1f);
            Assert.Equal(100f, target.TargetPosition.Y, 0.1f);
            
            // Check Member State
            // Follower at (0,0), target at (95, 100). Dist ~137m.
            // BreakageRadius = 20m. So it should constitute Broken (or Rejoining if we had specific logic, but logic says dist > breakage -> broken)
            // dist > 20 -> Broken.
            var member = repo.GetComponent<FormationMember>(follower);
            Assert.Equal(FormationMemberState.Broken, member.State);

            // Move follower closer to verify state change
            var closerPos = new Vector2(94.5f, 100f); // 0.5m dist
            var vState = repo.GetComponent<VehicleState>(follower);
            vState.Position = closerPos;
            repo.AddComponent(follower, vState);
            
            system.Run();
            
            member = repo.GetComponent<FormationMember>(follower);
            Assert.Equal(FormationMemberState.InSlot, member.State);

            system.Dispose();
            templateManager.Dispose();
            repo.Dispose();
        }
    }
}
