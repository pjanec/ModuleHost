# BATCH-14: Network-ELM Integration - Reliable Initialization Protocol

**Batch Number:** BATCH-14  
**Phase:** Network System Upgrade - Phase 2 (Reliability Features)  
**Estimated Effort:** 6-8 hours  
**Priority:** HIGH - Critical reliability feature  
**Dependencies:** BATCH-12, BATCH-13

---

## 📋 Onboarding & Workflow

### Developer Instructions
Welcome to BATCH-14! This batch implements the reliable initialization protocol, enabling the network system to guarantee that all peer nodes have completed entity construction before the master node marks the entity as Active.

### Required Reading (IN ORDER)
1. **Workflow Guide:** `d:\Work\ModuleHost\.dev-workstream\README.md`
2. **BATCH-13 Review:** `d:\Work\ModuleHost\.dev-workstream\reviews\BATCH-13-REVIEW.md` - See what was done well
3. **Implementation Spec:** `d:\Work\ModuleHost\docs\ModuleHost-network-ELM-implementation-spec.md` - Focus on Section 5.3 (Workflow 3: Reliable Initialization)
4. **Analysis Summary:** `d:\Work\ModuleHost\docs\ModuleHost-network-ELM-analysis-summary.md` - Issue #4: Reliable Init Mode Not Implemented

### Source Code Location
- **Primary Work Area:** `d:\Work\ModuleHost\ModuleHost.Core\Network\`
- **Test Project:** `d:\Work\ModuleHost\ModuleHost.Core.Tests\`

### Report Submission
**When done, submit your report to:**  
`d:\Work\ModuleHost\.dev-workstream\reports\BATCH-14-REPORT.md`

**If you have questions, create:**  
`d:\Work\ModuleHost\.dev-workstream\questions\BATCH-14-QUESTIONS.md`

---

## 🎯 Batch Objectives

This batch implements **Reliable Initialization Mode**:

1. NetworkGateway participates in ELM as a module
2. NetworkGateway withholds its ACK until peer nodes confirm entity activation
3. NetworkGateway publishes EntityLifecycleStatusDescriptor messages
4. NetworkGateway egress system handles ForceNetworkPublish
5. Comprehensive testing of reliable initialization flow

**What this enables:**
- Master node can wait for distributed construction completion before activation
- Critical entities (tanks, helicopters) guaranteed synchronized across cluster
- Fast mode still works for non-critical entities (VFX, debris)

---

## ⚠️ IMPORTANT: Quality Standards

### Based on BATCH-13 Success:

**✅ Test Quality (MAINTAIN EXCELLENCE):**
- Tests must validate actual behavior
- Include edge cases (timeout, partial ACKs, node disconnect scenarios)
- Integration tests for full reliable init flow
- Minimum 25 tests for this batch

**✅ Report Quality (MAINTAIN EXCELLENCE):**
- Answer all specific questions thoroughly with examples
- Document all design decisions beyond spec
- Explain challenges and solutions in detail

**⚠️ From BATCH-13 Review:**
- Remove debug logging before submission (or make it conditional)
- No duplicate XML comment tags
- Handle all edge cases explicitly

---

## ✅ Tasks

### Task 1: Create NetworkGatewayModule Wrapper

**File:** `ModuleHost.Core/Network/NetworkGatewayModule.cs` (NEW FILE)

**Description:**  
Create a module that wraps the network gateway functionality and participates in ELM. This module will act as a barrier for reliable initialization.

**Requirements:**

```csharp
using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.ELM;
using ModuleHost.Core.Network.Interfaces;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Network Gateway Module that participates in Entity Lifecycle Management.
    /// For entities with ReliableInit flag, this module withholds its construction
    /// ACK until peer nodes confirm entity activation.
    /// </summary>
    public class NetworkGatewayModule : IModule
    {
        private readonly int _localNodeId;
        private readonly INetworkTopology _topology;
        private readonly EntityLifecycleModule _elm;
        
        // Track pending network ACKs: EntityId -> Set of node IDs we're waiting for
        private readonly Dictionary<Entity, HashSet<int>> _pendingPeerAcks;
        
        // Track when entities entered pending state (for timeout)
        private readonly Dictionary<Entity, uint> _pendingStartFrame;
        
        public int ModuleId { get; }
        public ExecutionPolicy Policy => ExecutionPolicy.Synchronous();
        
        public NetworkGatewayModule(
            int moduleId,
            int localNodeId,
            INetworkTopology topology,
            EntityLifecycleModule elm)
        {
            ModuleId = moduleId;
            _localNodeId = localNodeId;
            _topology = topology ?? throw new ArgumentNullException(nameof(topology));
            _elm = elm ?? throw new ArgumentNullException(nameof(elm));
            _pendingPeerAcks = new Dictionary<Entity, HashSet<int>>();
            _pendingStartFrame = new Dictionary<Entity, uint>();
        }
        
        public void Initialize(IModuleContext context)
        {
            // Register with ELM so we receive ConstructionOrder events
            _elm.RegisterModule(ModuleId);
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            var repo = view as EntityRepository;
            if (repo == null) return;
            
            var cmd = view.GetCommandBuffer();
            uint currentFrame = repo.GlobalVersion;
            
            // Process ConstructionOrder events from ELM
            ProcessConstructionOrders(view, cmd, currentFrame);
            
            // Check for timeouts on pending ACKs
            CheckPendingAckTimeouts(repo, cmd, currentFrame);
            
            // Process incoming EntityLifecycleStatusDescriptor messages
            ProcessIncomingLifecycleStatus(view, cmd);
        }
        
        private void ProcessConstructionOrders(ISimulationView view, IEntityCommandBuffer cmd, uint currentFrame)
        {
            var events = view.ConsumeEvents<ConstructionOrder>();
            
            foreach (var evt in events)
            {
                // Only handle entities with PendingNetworkAck component
                if (!view.HasComponent<PendingNetworkAck>(evt.Entity))
                {
                    // Fast mode - ACK immediately
                    _elm.AcknowledgeConstruction(evt.Entity, ModuleId, currentFrame, cmd);
                    continue;
                }
                
                // Reliable mode - determine peers and wait for their ACKs
                if (!view.HasComponent<NetworkSpawnRequest>(evt.Entity))
                {
                    // Already processed or missing spawn request
                    // This can happen if NetworkSpawnerSystem already removed it
                    // We need the DIS type to know which peers to expect
                    // Solution: Store DIS type in a separate component or lookup from DescriptorOwnership
                    // For now, ACK immediately if we can't determine peers
                    _elm.AcknowledgeConstruction(evt.Entity, ModuleId, currentFrame, cmd);
                    continue;
                }
                
                var request = view.GetComponentRO<NetworkSpawnRequest>(evt.Entity);
                var expectedPeers = _topology.GetExpectedPeers(request.DisType);
                var peerSet = new HashSet<int>(expectedPeers);
                
                if (peerSet.Count == 0)
                {
                    // No peers to wait for - ACK immediately
                    _elm.AcknowledgeConstruction(evt.Entity, ModuleId, currentFrame, cmd);
                    cmd.RemoveComponent<PendingNetworkAck>(evt.Entity);
                }
                else
                {
                    // Wait for peer ACKs
                    _pendingPeerAcks[evt.Entity] = peerSet;
                    _pendingStartFrame[evt.Entity] = currentFrame;
                    
                    Console.WriteLine($"[NetworkGatewayModule] Entity {evt.Entity.Index}: Waiting for {peerSet.Count} peer ACKs");
                }
            }
        }
        
        private void ProcessIncomingLifecycleStatus(ISimulationView view, IEntityCommandBuffer cmd)
        {
            // This would read from DDS EntityLifecycleStatusDescriptor topic
            // For now, we'll create a method that can be called by a translator
            // The translator will call: ReceiveLifecycleStatus(entity, nodeId, state)
        }
        
        /// <summary>
        /// Called by EntityLifecycleStatusTranslator when a peer status message arrives.
        /// </summary>
        public void ReceiveLifecycleStatus(Entity entity, int nodeId, EntityLifecycle state, IEntityCommandBuffer cmd, uint currentFrame)
        {
            if (!_pendingPeerAcks.TryGetValue(entity, out var pendingPeers))
                return; // Not waiting for this entity
            
            if (state != EntityLifecycle.Active)
                return; // Only care about Active confirmations
            
            if (pendingPeers.Remove(nodeId))
            {
                Console.WriteLine($"[NetworkGatewayModule] Entity {entity.Index}: Received ACK from node {nodeId} ({pendingPeers.Count} remaining)");
            }
            
            // Check if all peers have ACKed
            if (pendingPeers.Count == 0)
            {
                Console.WriteLine($"[NetworkGatewayModule] Entity {entity.Index}: All peer ACKs received, sending local ACK to ELM");
                
                _elm.AcknowledgeConstruction(entity, ModuleId, currentFrame, cmd);
                cmd.RemoveComponent<PendingNetworkAck>(entity);
                
                _pendingPeerAcks.Remove(entity);
                _pendingStartFrame.Remove(entity);
            }
        }
        
        private void CheckPendingAckTimeouts(EntityRepository repo, IEntityCommandBuffer cmd, uint currentFrame)
        {
            var timedOut = new List<Entity>();
            
            foreach (var kvp in _pendingStartFrame)
            {
                var entity = kvp.Key;
                var startFrame = kvp.Value;
                
                if (currentFrame - startFrame > NetworkConstants.RELIABLE_INIT_TIMEOUT_FRAMES)
                {
                    Console.Error.WriteLine($"[NetworkGatewayModule] Entity {entity.Index}: Timeout waiting for peer ACKs");
                    timedOut.Add(entity);
                }
            }
            
            foreach (var entity in timedOut)
            {
                // Timeout - ACK anyway to prevent blocking forever
                _elm.AcknowledgeConstruction(entity, ModuleId, currentFrame, cmd);
                cmd.RemoveComponent<PendingNetworkAck>(entity);
                
                _pendingPeerAcks.Remove(entity);
                _pendingStartFrame.Remove(entity);
            }
        }
        
        public void Cleanup(IModuleContext context)
        {
            _elm.UnregisterModule(ModuleId);
        }
    }
}
```

**Key Design Points:**
- Module participates in ELM by handling ConstructionOrder events
- Tracks expected peers per entity
- Receives lifecycle status from peers via translator callback
- Timeout handling (300 frames)

**Reference:** Implementation Spec Section 5.3, Analysis Summary "Phase 2: Reliability Features"

**Tests Required:**
- ✅ Fast mode (no PendingNetworkAck) → Immediate ACK
- ✅ Reliable mode with no peers → Immediate ACK
- ✅ Reliable mode with peers → Waits for ACKs
- ✅ Receives all peer ACKs → Sends ACK to ELM
- ✅ Timeout after 300 frames → Sends ACK anyway
- ✅ Partial ACKs (only some peers respond)

---

### Task 2: Create EntityLifecycleStatusTranslator

**File:** `ModuleHost.Core/Network/Translators/EntityLifecycleStatusTranslator.cs` (NEW FILE)

**Description:**  
Translator for EntityLifecycleStatusDescriptor messages. Handles both ingress (receiving peer status) and egress (publishing our status).

**Requirements:**

```csharp
using System;
using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Messages;

namespace ModuleHost.Core.Network.Translators
{
    /// <summary>
    /// Translates EntityLifecycleStatusDescriptor messages for reliable initialization.
    /// Ingress: Receives peer status updates and notifies NetworkGatewayModule.
    /// Egress: Publishes our lifecycle status when entities become Active.
    /// </summary>
    public class EntityLifecycleStatusTranslator : IDescriptorTranslator
    {
        public string TopicName => "SST.EntityLifecycleStatus";
        
        private readonly int _localNodeId;
        private readonly NetworkGatewayModule _gateway;
        private readonly Dictionary<long, Entity> _networkIdToEntity;
        
        public EntityLifecycleStatusTranslator(
            int localNodeId,
            NetworkGatewayModule gateway,
            Dictionary<long, Entity> networkIdToEntity)
        {
            _localNodeId = localNodeId;
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _networkIdToEntity = networkIdToEntity ?? throw new ArgumentNullException(nameof(networkIdToEntity));
        }
        
        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            var repo = view as EntityRepository;
            if (repo == null) return;
            
            uint currentFrame = repo.GlobalVersion;
            
            foreach (var sample in reader.TakeSamples())
            {
                if (sample.InstanceState != DdsInstanceState.Alive)
                    continue;
                
                if (sample.Data is not EntityLifecycleStatusDescriptor status)
                    continue;
                
                // Ignore our own messages
                if (status.NodeId == _localNodeId)
                    continue;
                
                // Find entity by network ID
                if (!_networkIdToEntity.TryGetValue(status.EntityId, out var entity))
                {
                    Console.WriteLine($"[LifecycleStatusTranslator] Status for unknown entity {status.EntityId} from node {status.NodeId}");
                    continue;
                }
                
                // Forward to gateway
                _gateway.ReceiveLifecycleStatus(entity, status.NodeId, status.State, cmd, currentFrame);
            }
        }
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Query entities that:
            // 1. Have NetworkIdentity (networked entities)
            // 2. Have PendingNetworkAck (were in reliable init mode)
            // 3. Are now Active (just finished construction)
            
            var query = view.Query()
                .With<NetworkIdentity>()
                .With<PendingNetworkAck>()
                .WithLifecycle(EntityLifecycle.Active)
                .Build();
            
            foreach (var entity in query)
            {
                var networkId = view.GetComponentRO<NetworkIdentity>(entity).Value;
                
                var status = new EntityLifecycleStatusDescriptor
                {
                    EntityId = networkId,
                    NodeId = _localNodeId,
                    State = EntityLifecycle.Active,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                writer.Write(status);
                
                Console.WriteLine($"[LifecycleStatusTranslator] Published Active status for entity {entity.Index} (network ID {networkId})");
            }
        }
    }
}
```

**Key Design Points:**
- Ingress: Receives peer status, filters out own messages, forwards to gateway
- Egress: Publishes status for entities that became Active with PendingNetworkAck
- Uses shared network ID mapping

**Reference:** Implementation Spec Section 4 (Component Specifications), Section 6

**Tests Required:**
- ✅ Ingress: Receives peer status → Calls gateway.ReceiveLifecycleStatus
- ✅ Ingress: Ignores own messages (NodeId == LocalNodeId)
- ✅ Ingress: Unknown entity ID → Logs warning, doesn't crash
- ✅ Egress: Entity becomes Active with PendingNetworkAck → Publishes status
- ✅ Egress: Entity Active without PendingNetworkAck → Doesn't publish

---

### Task 3: Update Network Egress to Handle ForceNetworkPublish

**File:** `ModuleHost.Core/Network/Systems/NetworkEgressSystem.cs` (NEW FILE or UPDATE existing)

**Description:**  
Create or update the network egress system to handle ForceNetworkPublish component, ensuring immediate publication of descriptors when ownership changes.

**Requirements:**

```csharp
using System;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Translators;

namespace ModuleHost.Core.Network.Systems
{
    /// <summary>
    /// System responsible for publishing owned descriptors to the network.
    /// Handles normal periodic publishing and force-publish requests.
    /// </summary>
    public class NetworkEgressSystem
    {
        private readonly IDescriptorTranslator[] _translators;
        private readonly IDataWriter[] _writers;
        
        public NetworkEgressSystem(
            IDescriptorTranslator[] translators,
            IDataWriter[] writers)
        {
            _translators = translators ?? throw new ArgumentNullException(nameof(translators));
            _writers = writers ?? throw new ArgumentNullException(nameof(writers));
            
            if (_translators.Length != _writers.Length)
                throw new ArgumentException("Translators and writers arrays must have same length");
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            // Process force-publish requests first
            ProcessForcePublish(view);
            
            // Normal periodic publishing
            for (int i = 0; i < _translators.Length; i++)
            {
                _translators[i].ScanAndPublish(view, _writers[i]);
            }
        }
        
        private void ProcessForcePublish(ISimulationView view)
        {
            var repo = view as EntityRepository;
            if (repo == null) return;
            
            // Query entities with ForceNetworkPublish
            var query = repo.Query()
                .With<ForceNetworkPublish>()
                .Build();
            
            foreach (var entity in query)
            {
                // Remove the component - it's one-time
                repo.RemoveComponent<ForceNetworkPublish>(entity);
                
                // Force publish happens implicitly in next ScanAndPublish
                // The translators will see this entity and publish it
                
                Console.WriteLine($"[NetworkEgressSystem] Force publish for entity {entity.Index}");
            }
        }
    }
}
```

**Alternative Approach:** If egress is already handled elsewhere, update that system to check for and remove ForceNetworkPublish components.

**Key Design Points:**
- Removes ForceNetworkPublish after processing
- Normal translator ScanAndPublish methods already handle publishing owned descriptors
- ForceNetworkPublish ensures publication happens even if data hasn't changed

**Reference:** Implementation Spec Section 6.3 (Dynamic Ownership)

**Tests Required:**
- ✅ Entity with ForceNetworkPublish → Component removed
- ✅ Translators called after ForceNetworkPublish processing
- ✅ Integration: Ownership transfer → ForceNetworkPublish → Descriptor published

---

### Task 4: Create StaticNetworkTopology Implementation

**File:** `ModuleHost.Core/Network/StaticNetworkTopology.cs` (NEW FILE)

**Description:**  
Provide a simple static implementation of INetworkTopology for testing and simple deployments.

**Requirements:**

```csharp
using System.Collections.Generic;
using System.Linq;
using Fdp.Kernel;
using ModuleHost.Core.Network.Interfaces;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Static network topology with hardcoded peer lists.
    /// For simple deployments and testing.
    /// </summary>
    public class StaticNetworkTopology : INetworkTopology
    {
        private readonly int _localNodeId;
        private readonly int[] _allNodes;
        
        public int LocalNodeId => _localNodeId;
        
        /// <summary>
        /// Creates a static topology where all nodes participate in all entity types.
        /// </summary>
        /// <param name="localNodeId">This node's ID</param>
        /// <param name="allNodes">All node IDs in the cluster (including local)</param>
        public StaticNetworkTopology(int localNodeId, int[] allNodes)
        {
            _localNodeId = localNodeId;
            _allNodes = allNodes ?? throw new System.ArgumentNullException(nameof(allNodes));
        }
        
        public IEnumerable<int> GetExpectedPeers(DISEntityType entityType)
        {
            // Return all nodes except local
            return _allNodes.Where(id => id != _localNodeId);
        }
    }
}
```

**Simple but functional.** Can be enhanced later with per-entity-type filtering.

**Reference:** Implementation Spec Appendix B (Configuration Examples)

**Tests Required:**
- ✅ GetExpectedPeers excludes local node ID
- ✅ GetExpectedPeers returns all other nodes
- ✅ Empty node list → Returns empty peers

---

### Task 5: Integration - Wire Everything Together

**Description:**  
Ensure all components work together. This is primarily tested via integration tests.

**Key Integration Points:**
1. NetworkSpawnerSystem adds PendingNetworkAck (BATCH-13) ✅
2. ELM sends ConstructionOrder events
3. NetworkGatewayModule receives ConstructionOrder, checks PendingNetworkAck
4. If reliable mode, waits for peer ACKs via EntityLifecycleStatusTranslator
5. When all peers ACK (or timeout), NetworkGatewayModule ACKs to ELM
6. Entity becomes Active
7. EntityLifecycleStatusTranslator publishes status to peers

**No new code required** - integration tests verify the flow.

---

## 🧪 Testing Requirements

### Critical: Test Quality Standards

**Based on BATCH-13 success, maintain the same high standards:**

✅ **Tests must validate BEHAVIOR:**
- Not just "component exists"
- Not just "method doesn't crash"
- Actual distributed coordination behavior

✅ **Test edge cases:**
- Timeout scenarios
- Partial ACK arrival
- No peers scenario
- Unknown entity scenarios

✅ **Integration tests:**
- Full reliable init flow (Master-first)
- Full reliable init flow (State-first/Ghost)
- Fast mode still works
- Mixed entities (some reliable, some fast)

### Unit Test Requirements

Create test file: `ModuleHost.Core.Tests/Network/ReliableInitializationTests.cs`

**Minimum Test Count: 25 tests**

**Test Categories:**

1. **NetworkGatewayModule Tests** (10 tests minimum)
   - Fast mode (no PendingNetworkAck) → Immediate ACK
   - Reliable mode with no peers → Immediate ACK
   - Reliable mode with peers → Waits for ACKs
   - Receives all peer ACKs → Sends local ACK
   - Receives partial ACKs → Still waiting
   - Timeout after 300 frames → Sends ACK anyway
   - Multiple entities pending simultaneously
   - Entity destroyed while pending
   - Duplicate ACK from same peer (idempotent)
   - Initialize/Cleanup registers/unregisters with ELM

2. **EntityLifecycleStatusTranslator Tests** (8 tests minimum)
   - Ingress: Receives peer status → Calls gateway
   - Ingress: Ignores own messages
   - Ingress: Unknown entity → Logs, doesn't crash
   - Ingress: Invalid state value → Handles gracefully
   - Egress: Active with PendingNetworkAck → Publishes
   - Egress: Active without PendingNetworkAck → No publish
   - Egress: Constructing with PendingNetworkAck → No publish
   - Egress: Multiple entities → All published

3. **NetworkEgressSystem Tests** (4 tests minimum)
   - ForceNetworkPublish component → Removed
   - Multiple entities with ForceNetworkPublish
   - No ForceNetworkPublish → Normal egress
   - Translators called correctly

4. **StaticNetworkTopology Tests** (3 tests minimum)
   - GetExpectedPeers excludes local node
   - Multiple nodes in cluster
   - Single node cluster → Empty peers

### Integration Test Requirements

Create test file: `ModuleHost.Core.Tests/Network/ReliableInitializationScenarios.cs`

**Minimum Scenario Count: 4 comprehensive scenarios**

**Required Scenarios:**

1. **Scenario: Full Reliable Init (2-Node Cluster)**
   - Master node creates entity with ReliableInit
   - NetworkSpawnerSystem adds PendingNetworkAck
   - ELM sends ConstructionOrder to all modules
   - NetworkGatewayModule waits for peer (replica node)
   - Simulate receiving EntityLifecycleStatus from replica
   - NetworkGatewayModule sends ACK to ELM
   - Entity becomes Active
   - Verify: Entity synchronized across both nodes

2. **Scenario: Fast Mode Still Works**
   - Entity created without ReliableInit flag
   - No PendingNetworkAck added
   - NetworkGatewayModule immediately ACKs
   - Entity becomes Active quickly
   - Verify: No peer coordination

3. **Scenario: Timeout Handling**
   - Entity with ReliableInit, expecting 2 peers
   - Only 1 peer responds
   - Advance 300 frames
   - NetworkGatewayModule times out, sends ACK
   - Entity becomes Active despite missing peer
   - Verify: Timeout prevents permanent blocking

4. **Scenario: Mixed Entity Types**
   - Create 3 entities: 1 reliable, 2 fast
   - Fast entities activate immediately
   - Reliable entity waits for peers
   - Verify: Independent processing

---

## 📊 Report Requirements

### ⚠️ MANDATORY: Complete Report

**Answer these specific questions:**

**Question 1:** How does NetworkGatewayModule track which peers it's waiting for? Explain the data structures and why you chose them.

**Question 2:** What happens if an entity is destroyed while pending peer ACKs? How does your code handle this edge case?

**Question 3:** Explain the relationship between PendingNetworkAck component lifecycle and the reliable init flow. When is it added, and when removed?

**Question 4:** How does the timeout mechanism work? What prevents the timeout from triggering too early?

**Question 5:** Why does EntityLifecycleStatusTranslator need to ignore its own messages? What would happen if it didn't?

**Question 6:** How do you ensure NetworkGatewayModule participates correctly in ELM? What events does it handle?

**Question 7:** What was the most challenging integration point in this batch? How did you verify it works correctly?

**Question 8:** If a peer never sends an ACK (node crash), what prevents the system from blocking forever? Trace the full timeout path.

---

## 🎯 Success Criteria

This batch is **DONE** when:

1. ✅ All 4 tasks implemented (Module, Translator, Egress, Topology)
2. ✅ NetworkGatewayModule participates in ELM correctly
3. ✅ Reliable initialization barrier logic works
4. ✅ EntityLifecycleStatusTranslator handles ingress and egress
5. ✅ ForceNetworkPublish processed correctly
6. ✅ Minimum 25 unit tests passing
7. ✅ Minimum 4 integration scenarios passing
8. ✅ All tests validate actual behavior (not just compilation)
9. ✅ Comprehensive report submitted (answers all 8 questions)
10. ✅ No debug logging left in code
11. ✅ Code follows existing patterns from BATCH-13
12. ✅ XML documentation on all public methods

---

## ⚠️ Common Pitfalls to Avoid

### 1. Not Handling Entity Destruction
❌ **Pitfall:** Entity destroyed while pending ACKs, causing memory leaks in tracking dictionaries  
✅ **Solution:** Clean up tracking dictionaries when entity destroyed or ACK sent

### 2. Double-ACK to ELM
❌ **Pitfall:** Calling AcknowledgeConstruction multiple times for same entity  
✅ **Solution:** Remove from pending tracking after first ACK

### 3. Timeout Too Short
❌ **Pitfall:** Using frame count without considering GlobalVersion correctly  
✅ **Solution:** Use `currentFrame - startFrame` comparison

### 4. Not Excluding Local Node
❌ **Pitfall:** Waiting for own ACK, causing deadlock  
✅ **Solution:** GetExpectedPeers must exclude local node ID

### 5. Missing PendingNetworkAck
❌ **Pitfall:** Assuming all entities in ConstructionOrder have PendingNetworkAck  
✅ **Solution:** Check component exists, handle both fast and reliable modes

### 6. Debug Logging
❌ **Pitfall:** Leaving debug Console.WriteLine statements (from BATCH-13 review)  
✅ **Solution:** Remove or make conditional (#if DEBUG)

---

## 📚 Reference Materials

### Must Read
- Implementation Spec Section 5.3 (Workflow 3: Reliable Initialization)
- Implementation Spec Section 6.2 (NetworkGatewayModule design)
- Analysis Summary "Phase 2: Reliability Features"

### Code to Study
- BATCH-13: NetworkSpawnerSystem (how PendingNetworkAck is added)
- BATCH-13: EntityStateTranslator (translator pattern)
- Existing: EntityLifecycleModule (ELM event patterns)

### Design Patterns
- Module participation in ELM
- Barrier synchronization pattern
- Timeout handling pattern

---

## 🚀 Getting Started

### Recommended Task Order
1. **Task 4:** StaticNetworkTopology (simplest, no dependencies)
2. **Task 1:** NetworkGatewayModule (core logic)
3. **Task 2:** EntityLifecycleStatusTranslator (depends on gateway)
4. **Task 3:** NetworkEgressSystem (independent)
5. **Task 5:** Integration tests (validates everything works together)

### Development Approach
- **TDD Recommended:** Write timeout test first, implement timeout logic
- **Test Each Mode:** Fast mode, reliable mode, mixed modes
- **Integration Early:** Don't wait until end to test full flow

---

## 💡 Implementation Hints

### Hint 1: Tracking Pending ACKs
Use `Dictionary<Entity, HashSet<int>>` where HashSet contains node IDs still pending. When HashSet becomes empty, all peers have ACKed.

### Hint 2: Timeout Detection
Store start frame in `Dictionary<Entity, uint>`. In Execute(), check `currentFrame - startFrame > TIMEOUT_FRAMES`.

### Hint 3: Module Registration
In Initialize(), call `_elm.RegisterModule(ModuleId)`. In Cleanup(), call `_elm.UnregisterModule(ModuleId)`.

### Hint 4: Event Consumption
Use `view.ConsumeEvents<ConstructionOrder>()` to get events. These are delivered to all registered modules.

### Hint 5: Entity Lifecycle Check
When entity becomes Active, check `HasComponent<PendingNetworkAck>` to know if it needs status published.

---

## 📝 Report Template Reminder

Use the full template structure:
- Implementation Summary (files added/modified)
- Implementation Details (for EACH task)
- Test Results (full output with counts)
- Deviations & Improvements (if any)
- Answers to all 8 specific questions
- Known Issues & Limitations
- Pre-submission Checklist

---

**This batch completes Phase 2 of the Network-ELM integration, enabling reliable distributed entity construction!** 🚀

---

**Batch Created:** 2026-01-11  
**Development Lead:** AI Assistant (Development Manager)  
**Dependencies:** BATCH-12 (Foundation), BATCH-13 (Core Integration)  
**Next Phase:** Phase 3 (Dynamic Ownership Events) - Optional enhancement
