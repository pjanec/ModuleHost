# BATCH 07: Network Gateway Core (DDS/SST Integration)

**Batch ID:** BATCH-07  
**Priority:** LOW (P3)  
**Estimated Effort:** 1.5 weeks  
**Dependencies:** BATCH-06

---

## 🎯 Objectives
Bridge FDP EntityRepository with external DDS network descriptors.

**Success Criteria:**
- ✅ Translator pattern working
- ✅ Ingress: DDS → FDP components
- ✅ Egress: FDP components → DDS
- ✅ Ownership respected
- ✅ Mock DDS for testing

---

## 📋 Tasks

### Task 7.1: Translator Interfaces ⭐⭐
**File:** `ModuleHost.Core/Network/IDescriptorTranslator.cs` (NEW)

```csharp
public interface IDescriptorTranslator
{
    string TopicName { get; }
    
    void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view);
    void ScanAndPublish(ISimulationView view, IDataWriter writer);
}

public interface IDataReader
{
    IEnumerable<object> TakeSamples();
}

public interface IDataWriter
{
    void Write(object sample);
    void Dispose(long entityId);
}
```

---

### Task 7.2: Example EntityState Translator ⭐⭐⭐
**File:** `ModuleHost.Core/Network/EntityStateTranslator.cs` (NEW)

```csharp
public class EntityStateTranslator : IDescriptorTranslator
{
    public string TopicName => "SST.EntityState";
    
    public void PollIngress(IDataReader reader, IEntityCommandBuffer cmd, ISimulationView view)
    {
        foreach (var sample in reader.TakeSamples())
        {
            var desc = (EntityStateDescriptor)sample;
            var entity = MapNetworkToLocal(desc.EntityId);
            
            // Map descriptor → components
            cmd.SetComponent(entity, new Position { Value = desc.Location });
            cmd.SetComponent(entity, new Velocity { Value = desc.Velocity });
            cmd.SetComponent(entity, new NetworkTarget { Value = desc.Location });
        }
    }
    
    public void ScanAndPublish(ISimulationView view, IDataWriter writer)
    {
        // Query owned entities
        var query = view.Query().With<Position>().WithOwnership(local).Build();
        
        foreach (var entity in query)
        {
            var desc = BuildDescriptor(entity, view);
            writer.Write(desc);
        }
    }
}
```

---

### Task 7.3: NetworkIngestSystem ⭐⭐
**File:** `ModuleHost.Core/Network/NetworkIngestSystem.cs` (NEW)

```csharp
[UpdateInPhase(SystemPhase.Input)]
public class NetworkIngestSystem : IModuleSystem
{
    private readonly List<IDescriptorTranslator> _translators;
    private readonly Dictionary<string, IDataReader> _readers = new();
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        var cmd = view.GetCommandBuffer();
        
        foreach (var translator in _translators)
        {
            if (_readers.TryGetValue(translator.TopicName, out var reader))
            {
                translator.PollIngress(reader, cmd, view);
            }
        }
    }
}
```

---

### Task 7.4: NetworkSyncSystem ⭐⭐
**File:** `ModuleHost.Core/Network/NetworkSyncSystem.cs` (NEW)

```csharp
[UpdateInPhase(SystemPhase.Export)]
public class NetworkSyncSystem : IModuleSystem
{
    private readonly List<IDescriptorTranslator> _translators;
    private readonly Dictionary<string, IDataWriter> _writers = new();
    
    public void Execute(ISimulationView view, float deltaTime)
    {
        foreach (var translator in _translators)
        {
            if (_writers.TryGetValue(translator.TopicName, out var writer))
            {
                translator.ScanAndPublish(view, writer);
            }
        }
    }
}
```

---

### Task 7.5: SSTModule Implementation ⭐
**File:** `ModuleHost.Core/Network/SSTModule.cs` (NEW)

```csharp
public class SSTModule : IModule
{
    public string Name => "SSTGateway";
    public ExecutionPolicy Policy => ExecutionPolicy.FastReplica();
    
    private readonly List<IDescriptorTranslator> _translators = new();
    
    public void RegisterSystems(ISystemRegistry registry)
    {
        registry.RegisterSystem(new NetworkIngestSystem(_translators));
        registry.RegisterSystem(new NetworkSyncSystem(_translators));
    }
}
```

---

### Task 7.6: Network Integration Testing ⭐⭐

**Test Scenarios:**
- Mock DDS implementation
- Round-trip data integrity
- Ownership enforcement
- EntityMaster lifecycle sync
- Multi-node simulation

---

## ✅ Definition of Done
- [ ] Translator interfaces defined
- [ ] Example translator working
- [ ] Ingress system functional
- [ ] Egress system functional
- [ ] Mock DDS for testing
- [ ] 18+ tests passing
- [ ] Round-trip validated

## 📊 Targets
- Ingress latency: <1ms per 100 updates
- Egress throughput: >1000 updates/sec
- Data integrity: 100%

---

## 🔗 References
**Design:** `../../docs/DESIGN-IMPLEMENTATION-PLAN.md` - Chapter 7  
**SST Rules:** `../../docs/ModuleHost-TODO.md` - bdc-sst-rules  
**Reporting:** `../reports/BATCH-07-REPORT.md`
