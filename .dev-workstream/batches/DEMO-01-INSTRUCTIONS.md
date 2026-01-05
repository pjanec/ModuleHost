# DEMO-01 Developer Assignment - BattleRoyale Demo Foundation

**Date:** January 5, 2026  
**Project:** Battle Royale Server Simulator Demo  
**Estimated Time:** 1 day  
**Story Points:** 5

---

## 📋 Assignment

Implement the foundation for the BattleRoyale demo application: project setup, components, events, and entity factory.

---

## 📚 Required Reading

**Read these documents in order:**

1. **Demo Design:** `d:\WORK\ModuleHost\docs\DEMO-APPLICATION-DESIGN.md`  
   *(Full design specification - READ COMPLETELY)*

2. **Workflow:** `d:\WORK\ModuleHost\.dev-workstream\README.md`  
   *(Standard development workflow)*

3. **Examples:** `d:\WORK\ModuleHost\docs\MODULE-IMPLEMENTATION-EXAMPLES.md`  
   *(Reference implementations)*

---

## 🎯 Tasks

### TASK-001: Create Project Structure (1 SP)

**Create:** `Fdp.Examples.BattleRoyale` console project

**Structure:**
```
Fdp.Examples.BattleRoyale/
├── Components/        (10 component files)
├── Events/           (3 event files)
├── Modules/          (7 module files - STUBS for now)
├── Systems/          (entity factory)
├── Visualization/    (console renderer - STUB)
└── Program.cs        (main entry point - STUB)
```

**Requirements:**
- Target: .NET 8.0
- Reference: `Fdp.Kernel`, `ModuleHost.Core`
- Console application
- Zero warnings

---

### TASK-002: Implement Components (2 SP)

**Create 10 component files** (see DEMO-APPLICATION-DESIGN.md lines 76-86):

**Unmanaged Components:**
```csharp
// Components/Position.cs
public struct Position { public float X, Y; }

// Components/Velocity.cs
public struct Velocity { public float X, Y; }

// Components/Health.cs
public struct Health { public float Current, Max; }

// Components/AIState.cs
public struct AIState 
{ 
    public Entity TargetEntity; 
    public float AggressionLevel; 
}

// Components/Inventory.cs
public struct Inventory 
{ 
    public int Weapon; 
    public int Ammo; 
    public int HealthKits; 
}

// Components/NetworkState.cs
public struct NetworkState 
{ 
    public uint LastUpdateTick; 
    public byte DirtyFlags; 
}

// Components/ItemType.cs
public enum ItemTypeEnum : byte { HealthKit, Weapon, Ammo }
public struct ItemType { public ItemTypeEnum Type; }

// Components/Damage.cs
public struct Damage { public float Amount; }

// Components/SafeZone.cs
public struct SafeZone { public float Radius; }
```

**Managed Component:**
```csharp
// Components/PlayerInfo.cs
public class PlayerInfo 
{ 
    public string Name { get; set; } = "";
    public Guid PlayerId { get; set; }
}
```

**Requirements:**
- All unmanaged components use `struct`
- Managed component uses `class`
- Public fields/properties
- Zero warnings

---

### TASK-003: Implement Events (1 SP)

**Create 3 event files** (see DEMO-APPLICATION-DESIGN.md lines 87-89):

```csharp
// Events/DamageEvent.cs
public struct DamageEvent
{
    public Entity Victim;
    public Entity Attacker;
    public float Amount;
    public uint Tick;
}

// Events/KillEvent.cs
public struct KillEvent
{
    public Entity Victim;
    public Entity Killer;
    public Position Position;
    public uint Tick;
}

// Events/ItemPickupEvent.cs
public struct ItemPickupEvent
{
    public Entity Player;
    public Entity Item;
    public ItemTypeEnum ItemType;    public uint Tick;
}
```

**Requirements:**
- All events are `struct` (unmanaged)
- Include tick for tracking
- Zero warnings

---

### TASK-004: Implement Entity Factory (1 SP)

**Create:** `Systems/EntityFactory.cs`

**Methods needed:**
```csharp
public static class EntityFactory
{
    // Spawn 100 players at random positions
    public static void SpawnPlayers(EntityRepository world, int count = 100);
    
    // Spawn 50 AI bots
    public static void SpawnBots(EntityRepository world, int count = 50);
    
    // Spawn 100 items (health kits, weapons)
    public static void SpawnItems(EntityRepository world, int count = 100);
    
    // Create 1 safe zone entity
    public static Entity CreateSafeZone(EntityRepository world);
    
    // Create projectile fired from position in direction
    public static Entity CreateProjectile(
        EntityRepository world, 
        Position pos, 
        Velocity vel, 
        float damage);
}
```

**Implementation hints:**
- Use `Random.Shared` for positions (0-1000 range)
- Register all components before creating entities
- Players: Position, Velocity, Health, Inventory, NetworkState, PlayerInfo
- Bots: Position, Velocity, Health, AIState
- Items: Position, ItemType
- Projectiles: Position, Velocity, Damage

**Requirements:**
- All entities created correctly
- Components registered first
- Randomized positions (0-1000 x/y range)
- Zero warnings

---

## ✅ Acceptance Criteria

**DEMO-01 is complete when:**

- [ ] Project compiles with zero warnings
- [ ] All 10 components implemented
- [ ] All 3 events implemented
- [ ] EntityFactory implemented with all 5 methods
- [ ] Can create 100 players + 50 bots + 100 items
- [ ] All entities have correct components
- [ ] Code follows C# naming conventions

---

## 📝 Deliverables

**Submit:** `reports/DEMO-01-REPORT.md`

**Must include:**
- Status of all 4 tasks
- Build output (zero warnings)
- Test code showing entity creation works
- File listing (all files created)
- Any issues encountered

---

## 💡 Implementation Notes

**Component Registration Example:**
```csharp
// In EntityFactory before creating entities
world.RegisterComponent<Position>();
world.RegisterComponent<Velocity>();
world.RegisterComponent<Health>();
// ... register all 10 component types
```

**Entity Creation Example:**
```csharp
// Create player
var player = world.CreateEntity();
world.AddComponent(player, new Position { X = x, Y = y });
world.AddComponent(player, new Velocity { X = 0, Y = 0 });
world.AddComponent(player, new Health { Current = 100, Max = 100 });
world.AddComponent(player, new Inventory { Weapon = 1, Ammo = 30, HealthKits = 2 });
world.AddComponent(player, new NetworkState { });
world.AddManagedComponent(player, new PlayerInfo 
{ 
    Name = $"Player{i}", 
    PlayerId = Guid.NewGuid() 
});
```

---

## 🚀 Next Batches

**After DEMO-01:**
- DEMO-02: Implement 3 Fast Tier modules (Network, Recorder, Physics)
- DEMO-03: Implement 4 Slow Tier modules (AI, Pathfinding, Analytics, World)
- DEMO-04: Main loop + visualization

**Total:** ~3 days for complete demo

---

**Questions?** Ask in `reports/DEMO-01-QUESTIONS.md`  
**Blocked?** Update `reports/BLOCKERS-ACTIVE.md`  
**Done?** Submit `reports/DEMO-01-REPORT.md`

**Remember:** Read the demo design document completely before starting!

---

**This will be an AMAZING demo of the ModuleHost system!** 🎮🚀
