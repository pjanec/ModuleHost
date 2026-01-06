# BATCH-CK-08: Formation Support

**Batch ID:** BATCH-CK-08  
**Phase:** Formation  
**Prerequisites:**
- BATCH-CK-01 (Formation structs) COMPLETE
- BATCH-CK-07 (Kinematics system) COMPLETE  
**Assigned:** TBD  

---

## 📋 Objectives

Implement formation target calculation and member tracking:
1. FormationTemplateManager (singleton for templates)
2. FormationTargetSystem (calculates slot positions)
3. Formation template definitions (Column, Wedge, Line, Custom)
4. Slot assignment logic
5. Member state tracking (InSlot, CatchingUp, Rejoining, Broken)

**Design Reference:** `D:\WORK\ModuleHost\docs\car-kinem-implementation-design.md`  
**Formation Section:** Lines 683-778 in design doc

---

## 📁 Project Structure

```
D:\WORK\ModuleHost\CarKinem\
├── Formation\
│   ├── FormationTemplateManager.cs   ← NEW
│   └── FormationTemplate.cs          ← NEW
└── Systems\
    └── FormationTargetSystem.cs      ← NEW

D:\WORK\ModuleHost\CarKinem.Tests\
└── Formation\
    ├── FormationTemplateTests.cs     ← NEW
    └── FormationTargetSystemTests.cs ← NEW
```

---

## 🎯 Tasks

### Task CK-08-01: Formation Template

**File:** `CarKinem/Formation/FormationTemplate.cs`

```csharp
using System.Numerics;

namespace CarKinem.Formation
{
    /// <summary>
    /// Formation template defining slot offsets.
    /// </summary>
    public class FormationTemplate
    {
        public FormationType Type { get; set; }
        public Vector2[] SlotOffsets { get; set; }
        
        /// <summary>
        /// Calculate slot position in world space.
        /// </summary>
        public Vector2 GetSlotPosition(int slotIndex, Vector2 leaderPos, Vector2 leaderForward)
        {
            if (slotIndex < 0 || slotIndex >= SlotOffsets.Length)
                return leaderPos;
            
            Vector2 offset = SlotOffsets[slotIndex];
            Vector2 right = new Vector2(leaderForward.Y, -leaderForward.X);
            
            return leaderPos + leaderForward * offset.X + right * offset.Y;
        }
    }
}
```

**File:** `CarKinem/Formation/FormationTemplateManager.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;

namespace CarKinem.Formation
{
    /// <summary>
    /// Singleton manager for formation templates.
    /// </summary>
    public class FormationTemplateManager : IDisposable
    {
        private readonly Dictionary<FormationType, FormationTemplate> _templates = new();
        
        public FormationTemplateManager()
        {
            RegisterDefaultTemplates();
        }
        
        private void RegisterDefaultTemplates()
        {
            // Column formation (single file)
            var column = new FormationTemplate
            {
                Type = FormationType.Column,
                SlotOffsets = new Vector2[16]
            };
            
            for (int i = 0; i < 16; i++)
            {
                // Offset behind leader (negative X = behind)
                column.SlotOffsets[i] = new Vector2(-(i + 1) * 5.0f, 0f);
            }
            _templates[FormationType.Column] = column;
            
            // Wedge formation (V-shape)
            var wedge = new FormationTemplate
            {
                Type = FormationType.Wedge,
                SlotOffsets = new Vector2[16]
            };
            
            for (int i = 0; i < 16; i++)
            {
                int row = (i / 2) + 1;
                int side = (i % 2 == 0) ? 1 : -1;
                wedge.SlotOffsets[i] = new Vector2(-row * 4.0f, side * row * 3.0f);
            }
            _templates[FormationType.Wedge] = wedge;
            
            // Line formation (horizontal line)
            var line = new FormationTemplate
            {
                Type = FormationType.Line,
                SlotOffsets = new Vector2[16]
            };
            
            for (int i = 0; i < 16; i++)
            {
                int side = (i % 2 == 0) ? 1 : -1;
                int offset = (i / 2) + 1;
                line.SlotOffsets[i] = new Vector2(0f, side * offset * 4.0f);
            }
            _templates[FormationType.Line] = line;
        }
        
        public FormationTemplate GetTemplate(FormationType type)
        {
            return _templates.TryGetValue(type, out var template) ? template : _templates[FormationType.Column];
        }
        
        public void RegisterCustomTemplate(FormationType type, Vector2[] slotOffsets)
        {
            _templates[type] = new FormationTemplate
            {
                Type = type,
                SlotOffsets = slotOffsets
            };
        }
        
        public void Dispose()
        {
            _templates.Clear();
        }
    }
}
```

---

### Task CK-08-02: Formation Target System

**File:** `CarKinem/Systems/FormationTargetSystem.cs`

```csharp
using System.Numerics;
using CarKinem.Core;
using CarKinem.Formation;
using Fdp.Kernel;

namespace CarKinem.Systems
{
    /// <summary>
    /// Calculates formation slot targets for members.
    /// Runs before CarKinematicsSystem.
    /// </summary>
    [SystemAttributes(Phase = Phase.EarlyUpdate, UpdateFrequency = UpdateFrequency.EveryFrame)]
    public class FormationTargetSystem : ComponentSystem
    {
        private readonly FormationTemplateManager _templateManager;
        
        public FormationTargetSystem(FormationTemplateManager templateManager)
        {
            _templateManager = templateManager;
        }
        
        protected override void OnUpdate()
        {
            // Query all formations
            var formationQuery = World.Query<FormationRoster>();
            
            foreach (var formationEntity in formationQuery)
            {
                var roster = World.GetComponent<FormationRoster>(formationEntity);
                UpdateFormation(roster);
            }
        }
        
        private unsafe void UpdateFormation(FormationRoster roster)
        {
            if (roster.Count == 0)
                return;
            
            // Get leader entity
            int leaderEntityId = roster.MemberEntityIds[0];
            var leaderEntity = new Entity(leaderEntityId, 0);
            
            if (!World.IsAlive(leaderEntity))
                return;
            
            var leaderState = World.GetComponent<VehicleState>(leaderEntity);
            var template = _templateManager.GetTemplate(roster.Type);
            
            // Update each member's target
            for (int i = 1; i < roster.Count; i++) // Start at 1 (skip leader)
            {
                int memberEntityId = roster.MemberEntityIds[i];
                var memberEntity = new Entity(memberEntityId, 0);
                
                if (!World.IsAlive(memberEntity))
                    continue;
                
                int slotIndex = roster.SlotIndices[i];
                
                // Calculate slot position
                Vector2 slotPos = template.GetSlotPosition(slotIndex, 
                    leaderState.Position, leaderState.Forward);
                
                // Get/create FormationTarget component
                if (!World.HasComponent<FormationTarget>(memberEntity))
                {
                    World.AddComponent(memberEntity, new FormationTarget());
                }
                
                ref var target = ref World.GetComponentRef<FormationTarget>(memberEntity);
                target.TargetPosition = slotPos;
                target.TargetHeading = leaderState.Forward;
                target.TargetSpeed = leaderState.Speed;
                
                // Update member state based on distance to slot
                ref var member = ref World.GetComponentRef<FormationMember>(memberEntity);
                var memberState = World.GetComponent<VehicleState>(memberEntity);
                
                float distToSlot = Vector2.Distance(memberState.Position, slotPos);
                
                if (distToSlot < roster.Params.SlotRadius)
                {
                    member.State = FormationMemberState.InSlot;
                }
                else if (distToSlot < roster.Params.CatchUpRadius)
                {
                    member.State = FormationMemberState.CatchingUp;
                }
                else if (distToSlot < roster.Params.BreakageRadius)
                {
                    member.State = FormationMemberState.Rejoining;
                }
                else
                {
                    member.State = FormationMemberState.Broken;
                }
                
                World.SetComponent(memberEntity, member);
            }
        }
    }
}
```

---

### Task CK-08-03: Update CarKinematicsSystem

Modify `CarKinem/Systems/CarKinematicsSystem.cs`:

```csharp
// In GetFormationTarget method:
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
```

---

### Task CK-08-04: Tests

**File:** `CarKinem.Tests/Formation/FormationTemplateTests.cs`

```csharp
using System.Numerics;
using CarKinem.Formation;
using Xunit;

namespace CarKinem.Tests.Formation
{
    public class FormationTemplateTests
    {
        [Fact]
        public void ColumnTemplate_OffsetsAreBehindLeader()
        {
            var manager = new FormationTemplateManager();
            var template = manager.GetTemplate(FormationType.Column);
            
            // All slots should be behind (negative X)
            for (int i = 0; i < template.SlotOffsets.Length; i++)
            {
                Assert.True(template.SlotOffsets[i].X < 0, 
                    $"Slot {i} should be behind leader");
                Assert.Equal(0f, template.SlotOffsets[i].Y, 
                    $"Slot {i} should be centered");
            }
            
            manager.Dispose();
        }
        
        [Fact]
        public void GetSlotPosition_TransformsOffsetToWorldSpace()
        {
            var manager = new FormationTemplateManager();
            var template = manager.GetTemplate(FormationType.Column);
            
            Vector2 leaderPos = new Vector2(100, 100);
            Vector2 leaderForward = new Vector2(1, 0);
            
            Vector2 slotPos = template.GetSlotPosition(0, leaderPos, leaderForward);
            
            // First slot should be 5m behind
            Assert.Equal(95f, slotPos.X, precision: 1);
            Assert.Equal(100f, slotPos.Y, precision: 1);
            
            manager.Dispose();
        }
    }
}
```

---

## ✅ Acceptance Criteria

- [ ] `dotnet build` succeeds with **zero warnings**
- [ ] `dotnet test` - **ALL tests pass**
- [ ] Minimum 5 unit tests
- [ ] All 3 default templates defined (Column, Wedge, Line)
- [ ] Formation targets updated each frame
- [ ] Member state tracking works (InSlot, CatchingUp, etc.)
- [ ] CarKinematicsSystem integrated with FormationTarget

---

## 📤 Submission

Submit report to: `.dev-workstream/reports/BATCH-CK-08-REPORT.md`

**Time Estimate:** 4-5 hours
