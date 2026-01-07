# BATCH 07: Network Gateway Core (DDS/SST Integration)

**Batch ID:** BATCH-07  
**Phase:** Advanced - Network Integration  
**Priority:** LOW (P3)  
**Estimated Effort:** 1.5 weeks  
**Dependencies:** BATCH-06 (needs Entity Lifecycle Manager)  
**Developer:** TBD  
**Assigned Date:** TBD

---

## 📚 Required Reading

**BEFORE starting, read these documents completely:**

1. **Workflow Instructions:** `../.dev-workstream/README.md`
2. **Design Document:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 7 (Network Gateway)
3. **Task Tracker:** `../.dev-workstream/TASK-TRACKER.md` - BATCH 07 section
4. **SST Rules:** `../../docs/ModuleHost-TODO.md` - Search for "bdc-sst-rules" section
5. **BATCH-06 Review:** `../reviews/BATCH-06-REVIEW.md` (understand ELM integration)
6. **Current Implementation:** Review FDP component and event systems

---

## 🎯 Batch Objectives

### Primary Goal
Bridge FDP EntityRepository with external DDS network descriptors using the Translator pattern.

### Success Criteria
- ✅ Translator pattern working (rich descriptors ↔ atomic components)
- ✅ Ingress: DDS → FDP components (polling-based)
- ✅ Egress: FDP components → DDS (ownership-aware)
- ✅ Ownership rules respected ("only owner writes")
- ✅ Mock DDS for testing
- ✅ Round-trip data integrity validated
- ✅ All tests passing

### Why This Matters
FDP uses atomic, normalized components for simulation. Network uses rich, denormalized descriptors for efficiency. The Translator pattern decouples these concerns, allowing FDP to evolve independently while maintaining network compatibility. Polling (not callbacks) aligns with ECS batching philosophy.

---

## 📋 Tasks

### Task 7.1: Translator Interfaces ⭐⭐

**Objective:** Define the abstraction layer between DDS and FDP.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 7, Section 7.2 - "Architecture"

**What to Create:**

```csharp
// File: ModuleHost.Core/Network/IDescriptorTranslator.cs (NEW)

using System;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Translates between rich network descriptors and atomic FDP components.
    /// Each translator handles one DDS topic.
    /// </summary>
    public interface IDescriptorTranslator
    {
        /// <summary>
        /// DDS topic name this translator handles.
        /// Example: "SST.EntityState", "SST.FireEvent"
        /// </summary>
        string TopicName { get; }
        
        /// <summary>
        /// Ingress: Poll DDS topic and translate to FDP components/events.
        /// Called during Input phase.
        /// </summary>
        /// <param name="reader">DDS data reader for this topic</param>
        /// <param name="cmd">Command buffer for creating/updating entities</param>
        /// <param name="view">Simulation view for queries</param>
        void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view);
        
        /// <summary>
        /// Egress: Scan FDP entities and publish owned data to DDS.
        /// Called during Export phase.
        /// </summary>
        /// <param name="view">Simulation view for queries</param>
        /// <param name="writer">DDS data writer for this topic</param>
        void ScanAndPublish(ISimulationView view, IDataWriter writer);
    }
    
    /// <summary>
    /// Abstraction over DDS DataReader (for testability and DDS independence).
    /// </summary>
    public interface IDataReader : IDisposable
    {
        /// <summary>
        /// Take all available samples from DDS topic.
        /// Returns empty if no new data.
        /// </summary>
        IEnumerable<object> TakeSamples();
        
        /// <summary>
        /// Topic name this reader is subscribed to.
        /// </summary>
        string TopicName { get; }
    }
    
    /// <summary>
    /// Abstraction over DDS DataWriter (for testability and DDS independence).
    /// </summary>
    public interface IDataWriter : IDisposable
    {
        /// <summary>
        /// Write a descriptor sample to DDS.
        /// </summary>
        void Write(object sample);
        
        /// <summary>
        /// Dispose/unregister an entity instance.
        /// Signals to DDS that entity no longer exists.
        /// </summary>
        void Dispose(long networkEntityId);
        
        /// <summary>
        /// Topic name this writer publishes to.
        /// </summary>
        string TopicName { get; }
    }
}
```

**Acceptance Criteria:**
- [ ] `IDescriptorTranslator` interface defined with PollIngress/ScanAndPublish
- [ ] `IDataReader` abstraction created
- [ ] `IDataWriter` abstraction created
- [ ] Interfaces are DDS-agnostic (testable with mocks)
- [ ] XML documentation complete

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/DescriptorTranslatorInterfaceTests.cs

using Xunit;
using ModuleHost.Core.Network;
using Moq;

namespace ModuleHost.Core.Tests
{
    public class DescriptorTranslatorInterfaceTests
    {
        [Fact]
        public void IDataReader_TakeSamples_CanBeEmpty()
        {
            var mockReader = new Mock<IDataReader>();
            mockReader.Setup(r => r.TakeSamples()).Returns(Enumerable.Empty<object>());
            
            var samples = mockReader.Object.TakeSamples();
            Assert.Empty(samples);
        }
        
        [Fact]
        public void IDataWriter_Write_AcceptsDescriptor()
        {
            var mockWriter = new Mock<IDataWriter>();
            var descriptor = new TestDescriptor { Id = 123 };
            
            mockWriter.Object.Write(descriptor);
            
            mockWriter.Verify(w => w.Write(It.IsAny<object>()), Times.Once);
        }
        
        [Fact]
        public void IDescriptorTranslator_HasTopicName()
        {
            var mockTranslator = new Mock<IDescriptorTranslator>();
            mockTranslator.Setup(t => t.TopicName).Returns("TestTopic");
            
            Assert.Equal("TestTopic", mockTranslator.Object.TopicName);
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Network/IDescriptorTranslator.cs`
- [ ] New test file: `ModuleHost.Core.Tests/DescriptorTranslatorInterfaceTests.cs`
- [ ] 3+ unit tests passing

---

### Task 7.2: Example EntityState Translator ⭐⭐⭐

**Objective:** Implement a concrete translator for the EntityState descriptor.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 7, Section 7.2 - "Example Translator"

**What to Create:**

```csharp
// File: ModuleHost.Core/Network/Translators/EntityStateTranslator.cs (NEW)

using System;
using System.Collections.Generic;
using System.Numerics;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Network.Translators
{
    /// <summary>
    /// Translates SST EntityState descriptors to/from FDP components.
    /// 
    /// Ingress: EntityStateDescriptor → Position, Velocity, NetworkTarget components
    /// Egress: Position, Velocity components → EntityStateDescriptor
    /// </summary>
    public class EntityStateTranslator : IDescriptorTranslator
    {
        public string TopicName => "SST.EntityState";
        
        private readonly Dictionary<long, Entity> _networkIdToEntity = new();
        private readonly Dictionary<Entity, long> _entityToNetworkId = new();
        
        // === INGRESS: DDS → FDP ===
        
        public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
        {
            foreach (var sample in reader.TakeSamples())
            {
                if (sample is not EntityStateDescriptor desc)
                {
                    Console.Error.WriteLine($"[EntityStateTranslator] Unexpected sample type: {sample?.GetType().Name}");
                    continue;
                }
                
                // Map network ID to local entity
                Entity entity;
                
                if (!_networkIdToEntity.TryGetValue(desc.EntityId, out entity))
                {
                    // New entity from network - create it
                    entity = CreateEntityFromDescriptor(desc, cmd, view);
                    _networkIdToEntity[desc.EntityId] = entity;
                    _entityToNetworkId[entity] = desc.EntityId;
                }
                
                // Update entity state
                UpdateEntityFromDescriptor(entity, desc, cmd, view);
            }
        }
        
        private Entity CreateEntityFromDescriptor(
            EntityStateDescriptor desc, 
            IEntityCommandBuffer cmd, 
            ISimulationView view)
        {
            // Create entity with Constructing lifecycle state
            var entity = view.CreateEntity();
            cmd.SetLifecycleState(entity, EntityLifecycle.Constructing);
            
            // Set initial components
            cmd.SetComponent(entity, new Position { Value = desc.Location });
            cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
            
            // Set network metadata
            cmd.SetComponent(entity, new NetworkOwnership
            {
                OwnerId = desc.OwnerId,
                IsLocallyOwned = false // Remote entity
            });
            
            cmd.SetComponent(entity, new NetworkTarget
            {
                Value = desc.Location,
                Timestamp = desc.Timestamp
            });
            
            Console.WriteLine($"[EntityStateTranslator] Created entity {entity.Id} from network ID {desc.EntityId}");
            
            return entity;
        }
        
        private void UpdateEntityFromDescriptor(
            Entity entity,
            EntityStateDescriptor desc,
            IEntityCommandBuffer cmd,
            ISimulationView view)
        {
            // Only update if we don't own this entity
            if (view.HasComponent<NetworkOwnership>(entity))
            {
                var ownership = view.GetComponentRO<NetworkOwnership>(entity);
                if (ownership.IsLocallyOwned)
                {
                    // We own it - ignore incoming updates
                    return;
                }
            }
            
            // Update NetworkTarget (smoothing system will interpolate)
            cmd.SetComponent(entity, new NetworkTarget
            {
                Value = desc.Location,
                Timestamp = desc.Timestamp
            });
            
            // Update velocity for dead reckoning
            cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
        }
        
        // === EGRESS: FDP → DDS ===
        
        public void ScanAndPublish(ISimulationView view, IDataWriter writer)
        {
            // Query locally owned entities with Position and Velocity
            var query = view.Query()
                .With<Position>()
                .With<Velocity>()
                .With<NetworkOwnership>()
                .Build();
            
            foreach (var entity in query)
            {
                var ownership = view.GetComponentRO<NetworkOwnership>(entity);
                
                // Only publish if we own this entity
                if (!ownership.IsLocallyOwned)
                    continue;
                
                // Get or assign network ID
                if (!_entityToNetworkId.TryGetValue(entity, out var networkId))
                {
                    networkId = GenerateNetworkId(entity);
                    _entityToNetworkId[entity] = networkId;
                    _networkIdToEntity[networkId] = entity;
                }
                
                // Build descriptor from components
                var descriptor = BuildDescriptor(entity, networkId, view);
                
                // Publish to DDS
                writer.Write(descriptor);
            }
        }
        
        private EntityStateDescriptor BuildDescriptor(
            Entity entity, 
            long networkId, 
            ISimulationView view)
        {
            var pos = view.GetComponentRO<Position>(entity);
            var vel = view.GetComponentRO<Velocity>(entity);
            var ownership = view.GetComponentRO<NetworkOwnership>(entity);
            
            return new EntityStateDescriptor
            {
                EntityId = networkId,
                OwnerId = ownership.OwnerId,
                Location = pos.Value,
                Velocity = vel.Value,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
        
        private long GenerateNetworkId(Entity entity)
        {
            // Simple strategy: use entity ID + node offset
            // In production: use GUID or distributed ID generator
            return entity.Id;
        }
        
        // === CLEANUP ===
        
        public void OnEntityDestroyed(Entity entity, IDataWriter writer)
        {
            if (_entityToNetworkId.TryGetValue(entity, out var networkId))
            {
                // Notify DDS that entity is disposed
                writer.Dispose(networkId);
                
                _entityToNetworkId.Remove(entity);
                _networkIdToEntity.Remove(networkId);
            }
        }
    }
    
    // === DESCRIPTOR DEFINITIONS ===
    
    /// <summary>
    /// Network descriptor for entity state (position, velocity).
    /// Maps to multiple FDP components.
    /// </summary>
    public class EntityStateDescriptor
    {
        public long EntityId { get; set; }
        public int OwnerId { get; set; }
        public Vector3 Location { get; set; }
        public Vector3 Velocity { get; set; }
        public long Timestamp { get; set; }
    }
    
    // === FDP COMPONENTS ===
    
    public struct Position
    {
        public Vector3 Value;
    }
    
    public struct Velocity
    {
        public Vector3 Value;
    }
    
    public struct NetworkOwnership
    {
        public int OwnerId;
        public bool IsLocallyOwned;
    }
    
    public struct NetworkTarget
    {
        public Vector3 Value;
        public long Timestamp;
    }
}
```

**Acceptance Criteria:**
- [ ] EntityStateTranslator implements IDescriptorTranslator
- [ ] Ingress creates entities from descriptors
- [ ] Ingress updates existing entities
- [ ] Egress publishes owned entities only
- [ ] Network ID mapping maintained
- [ ] Ownership rules enforced

**Unit Tests to Write:**

```csharp
// File: ModuleHost.Core.Tests/EntityStateTranslatorTests.cs

[Fact]
public void EntityStateTranslator_Ingress_CreatesEntity()
{
    var translator = new EntityStateTranslator();
    var mockReader = CreateMockReader(new EntityStateDescriptor
    {
        EntityId = 100,
        OwnerId = 2,
        Location = new Vector3(10, 20, 30),
        Velocity = new Vector3(1, 2, 3)
    });
    
    var view = CreateSimulationView();
    var cmd = view.GetCommandBuffer();
    
    translator.PollIngress(mockReader, cmd, view);
    
    // Verify entity created
    var entities = view.Query().With<Position>().Build().ToList();
    Assert.Single(entities);
    
    var entity = entities[0];
    var pos = view.GetComponentRO<Position>(entity);
    Assert.Equal(new Vector3(10, 20, 30), pos.Value);
}

[Fact]
public void EntityStateTranslator_Ingress_IgnoresOwnedEntities()
{
    var translator = new EntityStateTranslator();
    var view = CreateSimulationView();
    var cmd = view.GetCommandBuffer();
    
    // Create locally owned entity
    var entity = view.CreateEntity();
    cmd.SetComponent(entity, new Position { Value = new Vector3(1, 2, 3) });
    cmd.SetComponent(entity, new NetworkOwnership 
    { 
        OwnerId = 1, 
        IsLocallyOwned = true 
    });
    
    // Receive update for same entity
    var mockReader = CreateMockReader(new EntityStateDescriptor
    {
        EntityId = entity.Id,
        Location = new Vector3(999, 999, 999) // Different position
    });
    
    translator.PollIngress(mockReader, cmd, view);
    
    // Position should NOT change (we own it)
    var pos = view.GetComponentRO<Position>(entity);
    Assert.Equal(new Vector3(1, 2, 3), pos.Value);
}

[Fact]
public void EntityStateTranslator_Egress_PublishesOwnedOnly()
{
    var translator = new EntityStateTranslator();
    var view = CreateSimulationView();
    
    // Create owned entity
    var owned = view.CreateEntity();
    view.SetComponent(owned, new Position { Value = Vector3.One });
    view.SetComponent(owned, new Velocity { Value = Vector3.Zero });
    view.SetComponent(owned, new NetworkOwnership 
    { 
        IsLocallyOwned = true, 
        OwnerId = 1 
    });
    
    // Create remote entity
    var remote = view.CreateEntity();
    view.SetComponent(remote, new Position { Value = Vector3.Zero });
    view.SetComponent(remote, new Velocity { Value = Vector3.Zero });
    view.SetComponent(remote, new NetworkOwnership 
    { 
        IsLocallyOwned = false, 
        OwnerId = 2 
    });
    
    var mockWriter = new MockDataWriter();
    translator.ScanAndPublish(view, mockWriter);
    
    // Only owned entity published
    Assert.Single(mockWriter.WrittenSamples);
    var desc = (EntityStateDescriptor)mockWriter.WrittenSamples[0];
    Assert.Equal(Vector3.One, desc.Location);
}

[Fact]
public void EntityStateTranslator_RoundTrip_PreservesData()
{
    var translator = new EntityStateTranslator();
    var view = CreateSimulationView();
    
    // Ingress: Create entity from descriptor
    var originalDesc = new EntityStateDescriptor
    {
        EntityId = 100,
        OwnerId = 1,
        Location = new Vector3(10, 20, 30),
        Velocity = new Vector3(1, 2, 3),
        Timestamp = 12345
    };
    
    var mockReader = CreateMockReader(originalDesc);
    translator.PollIngress(mockReader, view.GetCommandBuffer(), view);
    
    // Make it owned (simulate ownership transfer)
    var entity = view.Query().With<Position>().Build().First();
    view.SetComponent(entity, new NetworkOwnership 
    { 
        IsLocallyOwned = true, 
        OwnerId = 1 
    });
    
    // Egress: Publish back
    var mockWriter = new MockDataWriter();
    translator.ScanAndPublish(view, mockWriter);
    
    // Verify data preserved
    var publishedDesc = (EntityStateDescriptor)mockWriter.WrittenSamples[0];
    Assert.Equal(originalDesc.Location, publishedDesc.Location);
    Assert.Equal(originalDesc.Velocity, publishedDesc.Velocity);
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Network/Translators/EntityStateTranslator.cs`
- [ ] New test file: `ModuleHost.Core.Tests/EntityStateTranslatorTests.cs`
- [ ] 4+ unit tests passing

---

### Task 7.3: NetworkIngestSystem ⭐⭐

**Objective:** Create system that polls DDS and runs translators during Input phase.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 7, Section 7.2 - "NetworkIngestSystem"

**What to Create:**

```csharp
// File: ModuleHost.Core/Network/NetworkIngestSystem.cs (NEW)

using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Polls DDS topics and runs ingress translators.
    /// Runs in Input phase to ensure network data available before simulation.
    /// </summary>
    [UpdateInPhase(SystemPhase.Input)]
    public class NetworkIngestSystem : IModuleSystem
    {
        private readonly List<IDescriptorTranslator> _translators;
        private readonly Dictionary<string, IDataReader> _readers;
        
        public NetworkIngestSystem(
            List<IDescriptorTranslator> translators,
            Dictionary<string, IDataReader> readers)
        {
            _translators = translators;
            _readers = readers;
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            var cmd = view.GetCommandBuffer();
            
            // Poll each translator's topic
            foreach (var translator in _translators)
            {
                if (_readers.TryGetValue(translator.TopicName, out var reader))
                {
                    try
                    {
                        translator.PollIngress(reader, cmd, view);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[NetworkIngest] Error in translator '{translator.TopicName}': {ex.Message}");
                        Console.Error.WriteLine(ex.StackTrace);
                    }
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[NetworkIngest] No reader configured for topic '{translator.TopicName}'");
                }
            }
        }
    }
}
```

**Acceptance Criteria:**
- [ ] Runs in Input phase
- [ ] Polls all translators
- [ ] Exception handling per translator
- [ ] Reports missing readers

**Unit Tests:**

```csharp
[Fact]
public void NetworkIngestSystem_Execute_CallsAllTranslators()
{
    var mockTranslator1 = new Mock<IDescriptorTranslator>();
    mockTranslator1.Setup(t => t.TopicName).Returns("Topic1");
    
    var mockTranslator2 = new Mock<IDescriptorTranslator>();
    mockTranslator2.Setup(t => t.TopicName).Returns("Topic2");
    
    var readers = new Dictionary<string, IDataReader>
    {
        ["Topic1"] = CreateMockReader(),
        ["Topic2"] = CreateMockReader()
    };
    
    var system = new NetworkIngestSystem(
        new List<IDescriptorTranslator> { mockTranslator1.Object, mockTranslator2.Object },
        readers
    );
    
    var view = CreateSimulationView();
    system.Execute(view, 0.016f);
    
    mockTranslator1.Verify(t => t.PollIngress(
        It.IsAny<IDataReader>(), 
        It.IsAny<IEntityCommandBuffer>(), 
        It.IsAny<ISimulationView>()), 
        Times.Once);
        
    mockTranslator2.Verify(t => t.PollIngress(
        It.IsAny<IDataReader>(), 
        It.IsAny<IEntityCommandBuffer>(), 
        It.IsAny<ISimulationView>()), 
        Times.Once);
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Network/NetworkIngestSystem.cs`
- [ ] Tests in existing file
- [ ] 1+ test passing

---

### Task 7.4: NetworkSyncSystem ⭐⭐

**Objective:** Create system that publishes FDP state to DDS during Export phase.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 7, Section 7.2 - "NetworkSyncSystem"

**What to Create:**

```csharp
// File: ModuleHost.Core/Network/NetworkSyncSystem.cs (NEW)

using System.Collections.Generic;
using Fdp.Kernel;
using ModuleHost.Core.Abstractions;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Scans FDP entities and publishes owned state to DDS.
    /// Runs in Export phase after all simulation complete.
    /// </summary>
    [UpdateInPhase(SystemPhase.Export)]
    public class NetworkSyncSystem : IModuleSystem
    {
        private readonly List<IDescriptorTranslator> _translators;
        private readonly Dictionary<string, IDataWriter> _writers;
        
        public NetworkSyncSystem(
            List<IDescriptorTranslator> translators,
            Dictionary<string, IDataWriter> writers)
        {
            _translators = translators;
            _writers = writers;
        }
        
        public void Execute(ISimulationView view, float deltaTime)
        {
            // Publish each translator's data
            foreach (var translator in _translators)
            {
                if (_writers.TryGetValue(translator.TopicName, out var writer))
                {
                    try
                    {
                        translator.ScanAndPublish(view, writer);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"[NetworkSync] Error in translator '{translator.TopicName}': {ex.Message}");
                        Console.Error.WriteLine(ex.StackTrace);
                    }
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[NetworkSync] No writer configured for topic '{translator.TopicName}'");
                }
            }
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Network/NetworkSyncSystem.cs`
- [ ] Tests similar to NetworkIngestSystem
- [ ] 1+ test passing

---

### Task 7.5: SSTModule Implementation ⭐

**Objective:** Package network systems into a module.

**Design Reference:**
- Document: `DESIGN-IMPLEMENTATION-PLAN.md`
- Section: Chapter 7, Section 7.2 - "SST Module"

**What to Create:**

```csharp
// File: ModuleHost.Core/Network/SSTModule.cs (NEW)

using System.Collections.Generic;
using ModuleHost.Core.Abstractions;
using ModuleHost.Core.Network.Translators;

namespace ModuleHost.Core.Network
{
    /// <summary>
    /// Simulation State Transport (SST) module.
    /// Bridges FDP simulation with DDS network federation.
    /// </summary>
    public class SSTModule : IModule
    {
        public string Name => "SSTGateway";
        
        public ExecutionPolicy Policy => ExecutionPolicy.FastReplica();
        
        public IReadOnlyList<Type>? WatchComponents => null;
        public IReadOnlyList<Type>? WatchEvents => null;
        
        private readonly List<IDescriptorTranslator> _translators = new();
        private readonly Dictionary<string, IDataReader> _readers = new();
        private readonly Dictionary<string, IDataWriter> _writers = new();
        
        public SSTModule()
        {
            // Register standard translators
            RegisterTranslator(new EntityStateTranslator());
            // Add more: FireEventTranslator, EntityMasterTranslator, etc.
        }
        
        public void RegisterTranslator(IDescriptorTranslator translator)
        {
            _translators.Add(translator);
        }
        
        public void ConfigureReader(string topicName, IDataReader reader)
        {
            _readers[topicName] = reader;
        }
        
        public void ConfigureWriter(string topicName, IDataWriter writer)
        {
            _writers[topicName] = writer;
        }
        
        public void RegisterSystems(ISystemRegistry registry)
        {
            registry.RegisterSystem(new NetworkIngestSystem(_translators, _readers));
            registry.RegisterSystem(new NetworkSyncSystem(_translators, _writers));
        }
        
        public void Tick(ISimulationView view, float deltaTime)
        {
            // Systems handle execution
        }
    }
}
```

**Deliverables:**
- [ ] New file: `ModuleHost.Core/Network/SSTModule.cs`
- [ ] Integration test
- [ ] 1+ test passing

---

### Task 7.6: Network Integration Testing ⭐⭐

**Objective:** End-to-end validation with mock DDS.

**Test Scenarios:**

```csharp
// File: ModuleHost.Tests/NetworkIntegrationTests.cs

[Fact]
public async Task Network_RoundTrip_PreservesData()
{
    // Setup: SST module with mock DDS
    var sstModule = new SSTModule();
    
    var mockReader = new MockDataReader("SST.EntityState");
    var mockWriter = new MockDataWriter("SST.EntityState");
    
    sstModule.ConfigureReader("SST.EntityState", mockReader);
    sstModule.ConfigureWriter("SST.EntityState", mockWriter);
    
    var kernel = CreateKernel();
    kernel.RegisterModule(sstModule);
    kernel.Initialize();
    
    // Ingress: Receive entity from network
    mockReader.Enqueue(new EntityStateDescriptor
    {
        EntityId = 100,
        OwnerId = 2,
        Location = new Vector3(10, 20, 30),
        Velocity = new Vector3(1, 2, 3)
    });
    
    kernel.Update(0.016f);
    
    // Verify entity created
    var entities = kernel.LiveWorld.Query().With<Position>().Build().ToList();
    Assert.Single(entities);
    
    // Simulate ownership transfer
    var entity = entities[0];
    kernel.LiveWorld.SetComponent(entity, new NetworkOwnership 
    { 
        IsLocallyOwned = true, 
        OwnerId = 1 
    });
    
    // Egress: Publish back
    kernel.Update(0.016f);
    
    // Verify published
    Assert.Single(mockWriter.WrittenSamples);
    var desc = (EntityStateDescriptor)mockWriter.WrittenSamples[0];
    Assert.Equal(new Vector3(10, 20, 30), desc.Location);
}

[Fact]
public async Task Network_Ownership_EnforcesWriteRules()
{
    // Verify only owner publishes
    // Verify non-owner doesn't overwrite owned data
}

[Fact]
public async Task Network_MultiNode_Simulation()
{
    // Simulate 3 nodes sharing entities
}
```

**Deliverables:**
- [ ] New test file: `ModuleHost.Tests/NetworkIntegrationTests.cs`
- [ ] Mock DDS implementation
- [ ] 3+ integration tests passing

---

## ✅ Definition of Done

- [ ] All 6 tasks completed
- [ ] Translator interfaces defined
- [ ] EntityStateTranslator working
- [ ] Ingress system functional
- [ ] Egress system functional
- [ ] SST module packaged
- [ ] All unit tests passing (18+ tests)
- [ ] All integration tests passing (3+ tests)
- [ ] Round-trip validated
- [ ] Ownership rules enforced
- [ ] No compiler warnings
- [ ] Changes committed
- [ ] Report submitted

---

## 📊 Success Metrics

### Performance Targets
| Metric | Target | Critical |
|--------|--------|----------|
| Ingress latency | <1ms per 100 updates | <5ms |
| Egress throughput | >1000 updates/sec | >500 updates/sec |
| Data integrity | 100% | 100% |

### Quality Targets
| Metric | Target |
|--------|--------|
| Test coverage | >90% |
| All tests | Passing |

---

## 🚧 Potential Challenges

### Challenge 1: DDS Library Integration
**Issue:** Actual DDS integration requires vendor SDK  
**Solution:** Abstractions (IDataReader/Writer) decouple from DDS  
**Ask if:** DDS SDK choice unclear

### Challenge 2: Network ID Mapping
**Issue:** Entity ID collision across nodes  
**Solution:** Use GUID or distributed ID generator  
**Ask if:** ID strategy needs refinement

### Challenge 3: Lifecycle Integration with ELM
**Issue:** Network entities need coordinated spawn  
**Solution:** Use ELM's ConstructionOrder/ACK protocol  
**Ask if:** ELM integration unclear

### Challenge 4: Descriptor Versioning
**Issue:** Network protocol changes break compatibility  
**Solution:** Version descriptors, support migration  
**Ask if:** Versioning strategy needed

---

## 📝 Reporting

**When Complete:** Submit `../reports/BATCH-07-REPORT.md`  
**If Blocked:** Submit `../questions/BATCH-07-QUESTIONS.md`

---

## 🔗 References

**Primary Design:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 7  
**SST Rules:** `../../docs/ModuleHost-TODO.md` - bdc-sst-rules  
**Task Tracker:** `../TASK-TRACKER.md` - BATCH 07

**Code to Review:**
- FDP component and event system
- BATCH-06 ELM for lifecycle integration

---

## 💡 Implementation Tips

1. **Start with interfaces** - they're independent and testable
2. **Use mocks extensively** - don't need real DDS for most tests
3. **Test ownership rules thoroughly** - critical for federation
4. **Think about error cases** - malformed descriptors, missing entities
5. **Log network traffic** - essential for debugging distributed systems
6. **Consider batching** - publish multiple entities per DDS write

**This enables worldwide distributed simulation!**

Good luck! 🚀
