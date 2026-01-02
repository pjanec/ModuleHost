# B-One NG Module Host - Documentation

**Clean, authoritative documentation set for implementation.**

---

## 📘 START HERE

### **[IMPLEMENTATION-SPECIFICATION.md](IMPLEMENTATION-SPECIFICATION.md)** ⭐ MASTER DOCUMENT

**This is the single source of truth for implementation.**

Contains everything you need:
- ✅ Complete architecture overview
- ✅ All interface definitions (9 interfaces)
- ✅ FDP requirements summary
- ✅ Implementation checklist with progress tracking
- ✅ Verification steps and test criteria (200+ tests)
- ✅ 6-week implementation timeline
- ✅ Performance targets and success criteria

**Read this FIRST before starting implementation.**

---

## Implementation Documents (9 files)

**These are the AUTHORITATIVE documents created for ModuleHost implementation:**

### 1. Master Specification

| Document | Purpose |
|----------|---------|
| **[IMPLEMENTATION-SPECIFICATION.md](IMPLEMENTATION-SPECIFICATION.md)** | Complete implementation spec (master) |
| **[README.md](README.md)** | This file - navigation index |

### 2. Architecture & Design Decisions

| Document | Purpose |
|----------|---------|
| **[ADR-001-Snapshot-on-Demand.md](ADR-001-Snapshot-on-Demand.md)** | Architectural Decision: SoD vs COW |
| **[B-One-FDP-Data-Lake.md](B-One-FDP-Data-Lake.md)** | SoD rationale whitepaper |
| **[detailed-design-overview.md](detailed-design-overview.md)** | All 9 interfaces + ~25 classes |
| **[design-visual-reference.md](design-visual-reference.md)** | Diagrams, flows, memory layouts |

### 3. FDP Integration Requirements

| Document | Purpose |
|----------|---------|
| **[fdp-api-requirements.md](fdp-api-requirements.md)** | FDP kernel changes needed |
| **[FDP-EventsInSnapshots.md](FDP-EventsInSnapshots.md)** | Event history design (3s retention) |
| **[FDP-module-scheduling-support.md](FDP-module-scheduling-support.md)** | Event-driven scheduling |

---

## 📁 Reference Archive

**Original requirements documents are archived in [reference-archive/](reference-archive/)**

These docs provide **historical context** but are **NOT for implementation**:
- specification.md (original requirements)
- FDP-SST-001-Integration-Architecture.md (old COW-based design)
- specs-addendums1.md (ELM, replay protocol)
- Other context documents

**⚠️ For implementation, use the documents in THIS folder, not the archive.**

**See:** [reference-archive/README.md](reference-archive/README.md) for details.

---

## How to Use This Documentation

### For Implementation (NEW CODE):

1. **Read:** [IMPLEMENTATION-SPECIFICATION.md](IMPLEMENTATION-SPECIFICATION.md) ← START HERE
2. **Reference:** [detailed-design-overview.md](detailed-design-overview.md) for class details
3. **Visual:** [design-visual-reference.md](design-visual-reference.md) for diagrams
4. **FDP Changes:** [fdp-api-requirements.md](fdp-api-requirements.md) for kernel modifications

### For Understanding Design Decisions:

1. **Why SoD?** → [ADR-001-Snapshot-on-Demand.md](ADR-001-Snapshot-on-Demand.md)
2. **SoD Deep Dive?** → [B-One-FDP-Data-Lake.md](B-One-FDP-Data-Lake.md)
3. **Event History?** → [FDP-EventsInSnapshots.md](FDP-EventsInSnapshots.md)
4. **Event Scheduling?** → [FDP-module-scheduling-support.md](FDP-module-scheduling-support.md)

### For Understanding Original Requirements:

1. **Original System?** → [reference-archive/specification.md](reference-archive/specification.md)
2. **ELM Protocol?** → [reference-archive/specs-addendums1.md](reference-archive/specs-addendums1.md)
3. **FDP Basics?** → [reference-archive/fdp-overview.md](reference-archive/fdp-overview.md)

**⚠️ Remember:** Archive docs are for context only, not implementation.

---

## Quick Navigation

### By Role:

**If you are implementing:**
- Start: [IMPLEMENTATION-SPECIFICATION.md](IMPLEMENTATION-SPECIFICATION.md)
- Design: [detailed-design-overview.md](detailed-design-overview.md)
- FDP: [fdp-api-requirements.md](fdp-api-requirements.md)

**If you are reviewing:**
- Architecture: [ADR-001-Snapshot-on-Demand.md](ADR-001-Snapshot-on-Demand.md)
- Rationale: [B-One-FDP-Data-Lake.md](B-One-FDP-Data-Lake.md)
- Visuals: [design-visual-reference.md](design-visual-reference.md)

**If you need historical context:**
- Original requirements: [reference-archive/](reference-archive/)

---

## Document Statistics

**Current Docs:** 9 implementation files  
**Archive:** 7 reference files (moved to reference-archive/)

**Key Metrics:**
- Interfaces Defined: 9
- Classes Designed: ~25
- Test Cases: 200+
- Performance Targets: 6
- Implementation Weeks: 6

---

## Key Architectural Decisions

All current decisions documented with rationale:

| Decision | Document | Status |
|----------|----------|--------|
| **Snapshot-on-Demand** (not COW) | ADR-001 | ✅ Final |
| **Event History** (3s, 180 frames) | FDP-EventsInSnapshots | ✅ Final |
| **Event-Driven Scheduling** | FDP-module-scheduling-support | ✅ Final |
| **Dynamic Buffer Expansion** | fdp-api-requirements | ✅ Final |
| **Event Filtering** (per-module) | fdp-api-requirements | ✅ Final |
| **Generic API** (JIT branching) | IMPLEMENTATION-SPECIFICATION | ✅ Final |

---

## Implementation Phases

**Week 1:** FDP Event-Driven APIs  
**Week 2:** FDP Event History  
**Week 3:** Snapshot Core  
**Week 4:** Module Framework & Host  
**Week 5:** Services  
**Week 6:** Advanced (ELM, Resilience)

**Total Timeline:** 6 weeks

---

## Verification Status

| Aspect | Status |
|--------|--------|
| Requirements Complete | ✅ Yes |
| Architecture Approved | ✅ Yes |
| Interfaces Defined | ✅ Yes (9 complete) |
| Tests Specified | ✅ Yes (200+) |
| Performance Targets | ✅ Yes (6 metrics) |
| Implementation Plan | ✅ Yes (6 weeks) |
| Documentation Consistent | ✅ Yes (verified) |

---

## Folder Structure

```
docs/
├── README.md (this file)
├── IMPLEMENTATION-SPECIFICATION.md ⭐ MASTER
├── ADR-001-Snapshot-on-Demand.md
├── B-One-FDP-Data-Lake.md
├── detailed-design-overview.md
├── design-visual-reference.md
├── fdp-api-requirements.md
├── FDP-EventsInSnapshots.md
├── FDP-module-scheduling-support.md
│
└── reference-archive/ (original requirements - reference only)
    ├── README.md (explains archive purpose)
    ├── specification.md
    ├── specs-addendums1.md
    ├── sst-rules.md
    ├── FDP-SST-001-Integration-Architecture.md
    ├── fdp-overview.md
    ├── b-one-vision.md
    └── drill-clock-sync.md
```

---

## Document Ownership

**Created for ModuleHost (Authoritative):**
- All 9 files in main docs/ folder
- These supersede original requirements

**Archived (Reference Only):**
- All 7 files in reference-archive/ folder
- Historical context and original requirements

---

## Next Steps

1. ✅ **DONE:** All design and requirements complete
2. ✅ **DONE:** Documentation consolidated and organized
3. ⏳ **NEXT:** Begin Week 1 implementation (FDP Event-Driven APIs)
4. ⏳ **TODO:** Update IMPLEMENTATION-SPECIFICATION.md checklist as work progresses

---

**Status:** 🎯 **READY FOR IMPLEMENTATION**

**Last Updated:** January 3, 2026

---

*All documentation is final, organized, and approved. Archive contains original requirements for reference only. Implementation may begin using the documents in this folder.*
