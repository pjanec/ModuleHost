# BATCH-12: Network-ELM Integration - Foundation Layer

**Batch Number:** BATCH-12  
**Phase:** Network System Upgrade - Phase 1 (Part 1 of 2)  
**Estimated Effort:** 4-6 hours  
**Priority:** HIGH - Blocking work for Phase 1

---

## 📋 Onboarding & Workflow

### Developer Instructions
Welcome to BATCH-12! This batch establishes the foundation for the Network-ELM integration. You'll be adding core infrastructure components, constants, interfaces, and events that will be used by subsequent batches.

### Required Reading (IN ORDER)
1. **Workflow Guide:** `d:\Work\ModuleHost\.dev-workstream\README.md` - Read this first!
2. **Analysis Document:** `d:\Work\ModuleHost\docs\ModuleHost-network-ELM-analysis-summary.md` - Understanding the problems
3. **Implementation Spec:** `d:\Work\ModuleHost\docs\ModuleHost-network-ELM-implementation-spec.md` - Detailed design (focus on sections 4, 5, and Appendix A)

### Source Code Location
- **Primary Work Area:** `d:\Work\ModuleHost\ModuleHost.Core\Network\`
- **Test Project:** `d:\Work\ModuleHost\ModuleHost.Core.Tests\`

### Report Submission
**When done, submit your report to:**  
`d:\Work\ModuleHost\.dev-workstream\reports\BATCH-12-REPORT.md`

**If you have questions, create:**  
`d:\Work\ModuleHost\.dev-workstream\questions\BATCH-12-QUESTIONS.md`

---

## 🎯 Batch Objectives

This batch focuses on **foundational infrastructure** that will be used by subsequent batches:

1. Define new lifecycle states and constants
2. Add new components and events for network coordination
3. Create interface abstractions for strategies and topology
4. Add helper utilities for composite key management
5. Prepare existing code for refactoring (no breaking changes yet)

**What this batch does NOT include:**
- Translator refactoring (BATCH-13)
- NetworkSpawnerSystem implementation (BATCH-13)
- ELM integration logic (BATCH-13)

---

## ✅ Tasks

### Task 1: Create NetworkConstants.cs

**File:** `ModuleHost.Core/Network/NetworkConstants.cs` (NEW FILE)

**Description:**  
Create a centralized constants file for descriptor type IDs, message IDs, and timeouts.

**Requirements:**
```csharp
namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Centralized network constants and descriptor type IDs.
    /// </summary>
    public static class NetworkConstants
    {
        // === Descriptor Type IDs ===
        /// <summary>DDS Descriptor ID for EntityMaster (entity lifecycle)</summary>
        public const long ENTITY_MASTER_DESCRIPTOR_ID = 0;
        
        /// <summary>DDS Descriptor ID for EntityState (position/velocity)</summary>
        public const long ENTITY_STATE_DESCRIPTOR_ID = 1;
        
        /// <summary>DDS Descriptor ID for WeaponState</summary>
        public const long WEAPON_STATE_DESCRIPTOR_ID = 2;
        
        // === System Message IDs ===
        /// <summary>Lifecycle status messages for reliable init</summary>
        public const long ENTITY_LIFECYCLE_STATUS_ID = 900;
        
        /// <summary>Ownership transfer messages</summary>
        public const long OWNERSHIP_UPDATE_ID = 901;
        
        // === Timeouts ===
        /// <summary>Ghost entity timeout in frames (5 sec @ 60Hz)</summary>
        public const int GHOST_TIMEOUT_FRAMES = 300;
        
        /// <summary>Reliable init ACK timeout in frames (5 sec @ 60Hz)</summary>
        public const int RELIABLE_INIT_TIMEOUT_FRAMES = 300;
    }
}
```

**Tests:**
- Not required (constants only)
- But add usage validation in integration tests (verify constants are used correctly in other classes)

---

### Task 2: Add MasterFlags Enum to EntityMasterDescriptor.cs

**File:** `ModuleHost.Core/Network/EntityMasterDescriptor.cs` (UPDATE)

**Description:**  
Add a `MasterFlags` enum and corresponding property to `EntityMasterDescriptor` to support reliable initialization mode.

**Requirements:**

1. Add the `MasterFlags` enum (before the descriptor class):
```csharp
/// <summary>
/// Flags for EntityMaster descriptor configuration.
/// </summary>
[Flags]
public enum MasterFlags
{
    /// <summary>No special flags</summary>
    None = 0,
    
    /// <summary>
    /// Reliable initialization mode: Master waits for all peers to confirm
    /// entity activation before considering construction complete.
    /// </summary>
    ReliableInit = 1 << 0,
}
```

2. Add property to `EntityMasterDescriptor`:
```csharp
public MasterFlags Flags { get; set; } = MasterFlags.None;
```

**Reference:** Implementation Spec Section 5 (Workflows), Section 6.1.2 (EntityMasterTranslator)

**Tests:**
- Unit test: Verify default value is `MasterFlags.None`
- Unit test: Verify flag can be set and read correctly
- Unit test: Verify flags are combinable (bitwise operations work)

---

### Task 3: Update NetworkComponents.cs - Add New Components and Events

**File:** `ModuleHost.Core/Network/NetworkComponents.cs` (UPDATE)

**Description:**  
Add new transient components, events, and extend OwnershipExtensions with packing helpers.

#### 3A: Add NetworkSpawnRequest Component
```csharp
/// <summary>
/// Transient component added by EntityMasterTranslator when a new EntityMaster
/// descriptor arrives. Consumed by NetworkSpawnerSystem in the same frame.
/// </summary>
public struct NetworkSpawnRequest
{
    /// <summary>DIS entity type from EntityMaster descriptor</summary>
    public DISEntityType DisType;
    
    /// <summary>Primary owner node ID (EntityMaster owner)</summary>
    public int PrimaryOwnerId;
    
    /// <summary>Master flags (ReliableInit, etc.)</summary>
    public MasterFlags Flags;
    
    /// <summary>Network entity ID for mapping</summary>
    public long NetworkEntityId;
}
```

#### 3B: Add PendingNetworkAck Component
```csharp
/// <summary>
/// Transient tag component for entities awaiting network acknowledgment
/// in reliable initialization mode. Removed after publishing lifecycle status.
/// </summary>
public struct PendingNetworkAck { }
```

#### 3C: Add ForceNetworkPublish Component
```csharp
/// <summary>
/// Tag component to force immediate network publication of owned descriptors,
/// bypassing normal change detection. Used for ownership transfer confirmations.
/// </summary>
public struct ForceNetworkPublish { }
```

#### 3D: Add DescriptorAuthorityChanged Event
```csharp
/// <summary>
/// Event emitted when descriptor ownership changes (via OwnershipUpdate message).
/// Allows modules to react to ownership transfers.
/// </summary>
[EventId(9010)]
public struct DescriptorAuthorityChanged
{
    public Entity Entity;
    public long DescriptorTypeId;
    
    /// <summary>True if this node acquired ownership, false if lost</summary>
    public bool IsNowOwner;
    
    /// <summary>New owner node ID</summary>
    public int NewOwnerId;
}
```

#### 3E: Extend OwnershipExtensions with Packing Helpers

Add these helper methods to the existing `OwnershipExtensions` class:

```csharp
/// <summary>
/// Packs descriptor type ID and instance ID into a single long key.
/// Format: [TypeId: bits 63-32][InstanceId: bits 31-0]
/// </summary>
public static long PackKey(long descriptorTypeId, long instanceId)
{
    return (descriptorTypeId << 32) | (uint)instanceId;
}

/// <summary>
/// Unpacks a composite key into descriptor type ID and instance ID.
/// </summary>
public static (long TypeId, long InstanceId) UnpackKey(long packedKey)
{
    long typeId = packedKey >> 32;
    long instanceId = (uint)(packedKey & 0xFFFFFFFF);
    return (typeId, instanceId);
}

/// <summary>
/// Overload of OwnsDescriptor that accepts separate typeId and instanceId.
/// Packs them internally before lookup.
/// </summary>
public static bool OwnsDescriptor(this ISimulationView view, Entity entity, 
    long descriptorTypeId, long instanceId)
{
    long packedKey = PackKey(descriptorTypeId, instanceId);
    return OwnsDescriptor(view, entity, packedKey);
}
```

**Reference:** Implementation Spec Section 4 (Component Specifications), Section 6.3 (Composite Key Helper)

**Tests:**
- Unit test: Verify `PackKey` and `UnpackKey` round-trip correctly
- Unit test: Test edge cases (typeId = 0, instanceId = 0, max values)
- Unit test: Verify 32-bit boundaries (typeId = 2^31-1, instanceId = 2^32-1)
- Unit test: Create event and verify all fields accessible
- Unit test: Verify event can be emitted and consumed in test simulation

---

### Task 4: Create Network Interface Definitions

**Description:**  
Create interface abstractions for ownership strategy, network topology, and TKB database access.

#### 4A: IOwnershipDistributionStrategy Interface

**File:** `ModuleHost.Core/Network/Interfaces/IOwnershipDistributionStrategy.cs` (NEW FILE)

```csharp
using Fdp.Kernel;

namespace ModuleHost.Core.Network.Interfaces
{
    /// <summary>
    /// Strategy interface for determining initial descriptor ownership
    /// in partial ownership scenarios.
    /// </summary>
    public interface IOwnershipDistributionStrategy
    {
        /// <summary>
        /// Determines the initial owner for a specific descriptor on a newly created entity.
        /// </summary>
        /// <param name="descriptorTypeId">DDS descriptor type ID</param>
        /// <param name="entityType">DIS entity type from EntityMaster</param>
        /// <param name="masterNodeId">Primary owner (EntityMaster owner)</param>
        /// <param name="instanceId">Descriptor instance ID (0 for single-instance)</param>
        /// <returns>
        /// Node ID that should own this descriptor, or null to use masterNodeId as default.
        /// </returns>
        int? GetInitialOwner(
            long descriptorTypeId, 
            DISEntityType entityType, 
            int masterNodeId, 
            long instanceId);
    }
}
```

#### 4B: INetworkTopology Interface

**File:** `ModuleHost.Core/Network/Interfaces/INetworkTopology.cs` (NEW FILE)

```csharp
using System.Collections.Generic;
using Fdp.Kernel;

namespace ModuleHost.Core.Network.Interfaces
{
    /// <summary>
    /// Abstraction for network peer discovery and topology management.
    /// Supports both static configuration and dynamic peer detection.
    /// </summary>
    public interface INetworkTopology
    {
        /// <summary>
        /// Gets the local node ID.
        /// </summary>
        int LocalNodeId { get; }
        
        /// <summary>
        /// Returns the list of peer node IDs expected to participate in
        /// construction of entities of the given type.
        /// Used for reliable initialization barriers.
        /// </summary>
        /// <param name="entityType">DIS entity type</param>
        /// <returns>Collection of peer node IDs (excluding LocalNodeId)</returns>
        IEnumerable<int> GetExpectedPeers(DISEntityType entityType);
    }
}
```

#### 4C: ITkbDatabase Interface

**File:** `ModuleHost.Core/Network/Interfaces/ITkbDatabase.cs` (NEW FILE)

```csharp
using Fdp.Kernel;

namespace ModuleHost.Core.Network.Interfaces
{
    /// <summary>
    /// Abstraction for Type-Kit-Bag (TKB) template database access.
    /// Allows network systems to apply templates without direct dependency
    /// on concrete TKB implementation.
    /// </summary>
    public interface ITkbDatabase
    {
        /// <summary>
        /// Retrieves a TKB template by DIS entity type.
        /// </summary>
        /// <param name="entityType">DIS entity type</param>
        /// <returns>Template, or null if no template exists for this type</returns>
        TkbTemplate? GetTemplateByEntityType(DISEntityType entityType);
        
        /// <summary>
        /// Retrieves a TKB template by name.
        /// </summary>
        /// <param name="templateName">Template name</param>
        /// <returns>Template, or null if not found</returns>
        TkbTemplate? GetTemplateByName(string templateName);
    }
}
```

**Reference:** Implementation Spec Section 6.2 (NetworkSpawnerSystem dependencies), Appendix B (Configuration Examples)

**Tests:**
- Create mock implementations of each interface
- Unit test: Verify interface can be implemented without compile errors
- Unit test: Verify mock strategy can return null (default) or specific node ID
- Unit test: Verify mock topology returns expected peer lists
- Unit test: Verify mock TKB database can return templates

---

### Task 5: Add EntityLifecycleStatusDescriptor Message

**File:** `ModuleHost.Core/Network/Messages/EntityLifecycleStatusDescriptor.cs` (NEW FILE)

**Description:**  
Create the DDS message type for reliable initialization ACK protocol.

**Requirements:**
```csharp
namespace ModuleHost.Core.Network.Messages
{
    /// <summary>
    /// DDS message published by peer nodes to confirm entity activation
    /// in reliable initialization mode. Master node collects these to
    /// determine when all peers have completed construction.
    /// </summary>
    public class EntityLifecycleStatusDescriptor
    {
        /// <summary>Network entity ID</summary>
        public long EntityId { get; set; }
        
        /// <summary>Reporting node ID</summary>
        public int NodeId { get; set; }
        
        /// <summary>Lifecycle state achieved</summary>
        public EntityLifecycle State { get; set; }
        
        /// <summary>Timestamp of status update</summary>
        public long Timestamp { get; set; }
    }
}
```

**Reference:** Implementation Spec Section 4 (Component Specifications), Section 5.3 (Workflow 3: Reliable Initialization)

**Tests:**
- Unit test: Verify all properties can be set and read
- Unit test: Verify default values are reasonable
- Unit test: Create message with `EntityLifecycle.Active` and verify serialization (mock DDS writer)

---

### Task 6: Update DISEntityType Placeholder (if needed)

**Description:**  
Verify that `DISEntityType` structure exists and is suitable for use in interfaces.

**Action Required:**
1. Search for existing `DISEntityType` definition in the codebase
2. If it exists, verify it has these minimum fields:
   - `Kind` (byte or enum)
   - `Domain` (byte or enum)
   - `Country` (short)
   - `Category`, `Subcategory`, `Specific`, `Extra` (bytes)
3. If it doesn't exist, create a placeholder:

**File:** `ModuleHost.Core/Network/DISEntityType.cs` (NEW FILE if needed)

```csharp
namespace ModuleHost.Core.Network
{
    /// <summary>
    /// DIS Protocol Entity Type enumeration (IEEE 1278.1).
    /// Identifies entity types in distributed simulations.
    /// </summary>
    public struct DISEntityType
    {
        public byte Kind { get; set; }
        public byte Domain { get; set; }
        public ushort Country { get; set; }
        public byte Category { get; set; }
        public byte Subcategory { get; set; }
        public byte Specific { get; set; }
        public byte Extra { get; set; }
        
        public override bool Equals(object obj)
        {
            if (obj is not DISEntityType other) return false;
            return Kind == other.Kind && Domain == other.Domain && 
                   Country == other.Country && Category == other.Category &&
                   Subcategory == other.Subcategory && Specific == other.Specific &&
                   Extra == other.Extra;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(Kind, Domain, Country, Category, 
                Subcategory, Specific, Extra);
        }
        
        public static bool operator ==(DISEntityType left, DISEntityType right) 
            => left.Equals(right);
        public static bool operator !=(DISEntityType left, DISEntityType right) 
            => !left.Equals(right);
    }
}
```

**Tests:**
- Unit test: Verify equality operator works correctly
- Unit test: Verify hash code is consistent

---

### Task 7: Create DefaultOwnershipStrategy Implementation

**File:** `ModuleHost.Core/Network/DefaultOwnershipStrategy.cs` (NEW FILE)

**Description:**  
Provide a simple default implementation of `IOwnershipDistributionStrategy` that always returns null (use master as default owner).

```csharp
using Fdp.Kernel;
using ModuleHost.Core.Network.Interfaces;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Default ownership strategy that assigns all descriptors to the master node.
    /// Returns null for all queries, causing fallback to PrimaryOwnerId.
    /// </summary>
    public class DefaultOwnershipStrategy : IOwnershipDistributionStrategy
    {
        public int? GetInitialOwner(
            long descriptorTypeId, 
            DISEntityType entityType, 
            int masterNodeId, 
            long instanceId)
        {
            // Return null = use master as default owner for all descriptors
            return null;
        }
    }
}
```

**Tests:**
- Unit test: Verify always returns null
- Unit test: Verify can be used in place of interface without errors

---

## 📊 Testing Requirements

### Unit Tests to Create

Create test file: `ModuleHost.Core.Tests/Network/NetworkFoundationTests.cs`

**Test Coverage:**
1. ✅ **NetworkConstants** - Verify constants have expected values
2. ✅ **MasterFlags** - Default value, flag combinations, bitwise operations
3. ✅ **Composite Key Packing** - PackKey/UnpackKey round-trip, edge cases
4. ✅ **DescriptorAuthorityChanged Event** - Creation, field access, emission
5. ✅ **Interface Implementations** - Mock objects for all 3 interfaces
6. ✅ **EntityLifecycleStatusDescriptor** - Property access, valid states
7. ✅ **DISEntityType** - Equality, hash code consistency
8. ✅ **DefaultOwnershipStrategy** - Always returns null

**Minimum Test Count:** 15-20 tests

**Test Naming Convention:**
```
Test_<Component>_<Scenario>_<ExpectedBehavior>
```

Examples:
- `Test_PackKey_WithTypeId123Instance456_ReturnsCorrectPackedValue`
- `Test_UnpackKey_RoundTrip_RestoresOriginalValues`
- `Test_MasterFlags_ReliableInit_CanBeCombinedWithBitwiseOr`

### Integration Test (Optional but Recommended)

Create test file: `ModuleHost.Core.Tests/Network/NetworkComponentsIntegrationTests.cs`

**Test Scenario:**
- Create mock entity with `DescriptorOwnership`
- Pack keys using helper methods
- Verify ownership queries work correctly with packed keys
- Emit `DescriptorAuthorityChanged` event and consume it

---

## 🎯 Success Criteria

This batch is considered **DONE** when:

1. ✅ All 7 tasks implemented
2. ✅ All new files compile without errors or warnings
3. ✅ All unit tests passing (minimum 15 tests)
4. ✅ Code follows existing patterns in `ModuleHost.Core`
5. ✅ XML documentation comments on all public APIs
6. ✅ No breaking changes to existing code
7. ✅ Report submitted with complete information

---

## ⚠️ Important Notes

### Existing Code Impact
- **DO NOT** refactor existing translators in this batch
- **DO NOT** implement NetworkSpawnerSystem yet (that's BATCH-13)
- **DO NOT** modify ELM systems
- This batch is purely additive (new files + extensions to existing files)

### Design Decisions
If you encounter ambiguities:
1. Check Implementation Spec first (sections referenced above)
2. Check Analysis Summary for context
3. If still unclear, create a questions file - don't guess!

### Performance Considerations
- Composite key packing must be allocation-free (use primitives only)
- Event structs should be lightweight (no managed references)
- Constants should be `const` not `static readonly`

### Code Quality Standards
- Follow existing naming conventions in the codebase
- Use XML doc comments (`///`) for all public types and members
- Keep files organized by category (Components, Interfaces, Messages, etc.)
- Add `#pragma warning disable` only if absolutely necessary (and document why)

---

## 📚 Reference Materials

### Must Read
- `docs/ModuleHost-network-ELM-implementation-spec.md` - Section 4 (Components), Section 6 (Implementation)
- `docs/ModuleHost-network-ELM-analysis-summary.md` - For context on why these changes are needed

### Helpful Context
- `docs/reference-archive/bdc-sst-rules.md` - SST Protocol rules (understand ownership model)
- Existing `ModuleHost.Core/Network/NetworkComponents.cs` - See existing patterns
- Existing `ModuleHost.Core/ELM/LifecycleEvents.cs` - See event ID conventions

---

## 📝 Reporting Instructions

When you submit `reports/BATCH-12-REPORT.md`, make sure to include:

1. **Task Completion Checklist** - All 7 tasks marked complete
2. **Test Results** - Full test run output (all passing)
3. **Files Added** - List of new files with brief descriptions
4. **Files Modified** - List of updated files and what changed
5. **Deviations** - Any intentional changes from these instructions (with rationale)
6. **Improvements** - Any enhancements you made beyond requirements
7. **Integration Notes** - How this batch prepares for BATCH-13
8. **Known Issues** - Any limitations or concerns to address

### Specific Questions to Answer in Report
- Did you find `DISEntityType` in the codebase or create it new?
- Are there any existing constants that conflict with `NetworkConstants`?
- Did you identify any potential issues with the composite key packing approach?
- Are the interfaces flexible enough for future use cases you can foresee?

---

## 🚀 Getting Started

### Step-by-Step Startup
1. Read this entire document
2. Read the Implementation Spec (focus on sections 4, 6, and appendices)
3. Review existing `NetworkComponents.cs` to understand patterns
4. Create a branch: `git checkout -b feature/network-elm-batch-12`
5. Start with Task 1 (NetworkConstants) - simplest task
6. Write tests as you go (TDD approach recommended)
7. Run tests frequently: `dotnet test ModuleHost.Core.Tests`
8. Commit regularly with clear messages

### Recommended Task Order
1. Task 1: NetworkConstants (foundation)
2. Task 6: DISEntityType (needed by interfaces)
3. Task 4: Interfaces (needed by strategy)
4. Task 7: DefaultOwnershipStrategy (implements interface)
5. Task 2: MasterFlags (enum addition)
6. Task 5: EntityLifecycleStatusDescriptor (message class)
7. Task 3: NetworkComponents updates (depends on MasterFlags)

---

## ❓ Questions or Blockers?

If you encounter questions or blockers:

1. Create `questions/BATCH-12-QUESTIONS.md` using the template
2. Document what you're trying to do and what's unclear
3. Note which task you're blocked on
4. Continue with other tasks if possible while waiting for answers

---

**Good luck! This is important foundational work that unblocks all of Phase 1. Take your time to get it right.** 🚀

---

**Batch Created:** 2026-01-11  
**Development Lead:** AI Assistant (Development Manager)  
**Next Batch:** BATCH-13 (Translators & Spawner System)
