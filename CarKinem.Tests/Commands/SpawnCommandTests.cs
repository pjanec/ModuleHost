using System.Numerics;
using CarKinem.Commands;
using CarKinem.Core;
using CarKinem.Systems;
using Fdp.Kernel;
using Xunit;

namespace CarKinem.Tests.Commands
{
    public class SpawnCommandTests
    {
        [Fact]
        public void SpawnCommand_CreatesVehicleWithComponents()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterComponent<VehicleParams>();
            repo.RegisterComponent<NavState>();
            repo.RegisterEvent<CmdSpawnVehicle>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            // Pre-allocate entity
            var entity = repo.CreateEntity();
            
            // Issue spawn command
            repo.Bus.Publish(new CmdSpawnVehicle
            {
                Entity = entity,
                Position = new Vector2(100, 50),
                Heading = new Vector2(1, 0),
                Class = VehicleClass.PersonalCar
            });
            
            // Process command
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Verify components
            Assert.True(repo.HasComponent<VehicleState>(entity));
            Assert.True(repo.HasComponent<VehicleParams>(entity));
            Assert.True(repo.HasComponent<NavState>(entity));
            
            var state = repo.GetComponent<VehicleState>(entity);
            Assert.Equal(new Vector2(100, 50), state.Position);
            Assert.Equal(new Vector2(1, 0), state.Forward);
            
            repo.Dispose();
        }
        
        [Fact]
        public void SpawnCommand_IgnoresDeadEntity()
        {
            var repo = new EntityRepository();
            repo.RegisterComponent<VehicleState>();
            repo.RegisterEvent<CmdSpawnVehicle>();
            
            var system = new VehicleCommandSystem();
            system.Create(repo);
            
            // Create and destroy entity
            var entity = repo.CreateEntity();
            repo.DestroyEntity(entity);
            
            // Try to spawn on dead entity
            repo.Bus.Publish(new CmdSpawnVehicle
            {
                Entity = entity,
                Position = Vector2.Zero,
                Heading = new Vector2(1, 0),
                Class = VehicleClass.PersonalCar
            });
            
            repo.Bus.SwapBuffers();
            system.Run();
            
            // Should not crash, command ignored
            Assert.False(repo.IsAlive(entity));
            
            repo.Dispose();
        }
    }
}
