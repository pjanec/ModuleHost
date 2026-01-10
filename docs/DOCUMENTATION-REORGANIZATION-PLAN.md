# Documentation Reorganization Plan - UPDATED WITH SOURCE CODE ANALYSIS


## New Structure (suggestion)

```
docs/
├── README.md                              [REWRITE] Accurate index
│
├── 01-OVERVIEW/
│   ├── System-Architecture-Overview.md    [NEW] Based on actual code structure
│   ├── Terminology-Glossary.md            [NEW] Actual terms used in code
│
├── 02-DESIGN/
│   ├── FDP-Kernel-Design.md               [NEW] Actual Fdp.Kernel architecture
│   ├── ModuleHost-Design.md               [NEW] Actual ModuleHost.Core architecture
│   ├── Time-Synchronization-Design.md
│   ├── Network-Gateway-Design.md
│
├── 03-USER-GUIDE/
│   ├── as now
│
├── 04-API-REFERENCE/
│   ├── Component-API.md                   [NEW] IComponentTable.HasChanges() etc.
│   ├── Event-API.md                       [NEW] HasEvent<T>(), ClearAll()
│   ├── Module-API.md                      [NEW] ModuleExecutionPolicy, TriggerType
│   └── Snapshot-Provider-API.md           [NEW] GDB, SoD, Convoy, Pool
│
│
```
