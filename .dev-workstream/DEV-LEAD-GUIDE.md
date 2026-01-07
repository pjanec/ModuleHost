# Development Lead - Batch Management Guide

**Project:** ModuleHost Advanced Features  
**Dev Lead:** [Your Name]  
**Start Date:** 2026-01-07

---

## 📋 Quick Reference

### Current Status
- **Active Batch:** BATCH-01 (Non-Blocking Execution)
- **Status:** Ready to assign to developer
- **Next Batch:** BATCH-02 (Reactive Scheduling) - awaiting BATCH-01 completion

### Key Files
- **Instructions Location:** `.dev-workstream/batches/BATCH-XX-INSTRUCTIONS.md`
- **Reports Location:** `.dev-workstream/reports/BATCH-XX-REPORT.md`
- **Questions Location:** `.dev-workstream/questions/BATCH-XX-QUESTIONS.md`
- **Reviews Location:** `.dev-workstream/reviews/BATCH-XX-REVIEW.md`

---

## 🔄 Your Workflow as Dev Lead

### Phase 1: Batch Assignment
1. ✅ Batch instructions created (BATCH-01 done)
2. Point developer to: `.dev-workstream/batches/BATCH-01-INSTRUCTIONS.md`
3. Developer reads:
   - Workflow: `.dev-workstream/README.md`
   - Instructions: `.dev-workstream/batches/BATCH-01-INSTRUCTIONS.md`
   - Design: `docs/DESIGN-IMPLEMENTATION-PLAN.md` (Chapter 1)
   - Tracker: `.dev-workstream/TASK-TRACKER.md`

### Phase 2: Developer Working
**You do nothing during this phase.** Developer works autonomously.

**Possible interruptions:**
- Developer creates `.dev-workstream/questions/BATCH-01-QUESTIONS.md`
- You answer questions in the same file
- Developer continues

### Phase 3: Batch Completion
Developer submits: `.dev-workstream/reports/BATCH-01-REPORT.md`

**Your review checklist:**

#### A. Report Completeness
- [ ] All tasks marked complete
- [ ] Test results included (unit + integration + performance)
- [ ] Deviations documented
- [ ] Known issues listed
- [ ] Pre-submission checklist completed

#### B. Code Review
**Files to check:**
- `ModuleHost.Core/ModuleHostKernel.cs` - Main implementation
- `ModuleHost.Core/Providers/OnDemandProvider.cs` - Pool sizing
- `ModuleHost.Core/Providers/SharedSnapshotProvider.cs` - Ref counting
- `ModuleHost.Core.Tests/` - Unit tests
- `ModuleHost.Tests/` - Integration tests
- `ModuleHost.Benchmarks/` - Performance benchmarks

**Review for:**
- [ ] Architectural fit (follows existing patterns)
- [ ] Performance (meets targets)
- [ ] Test coverage (>90%)
- [ ] Test quality (testing behavior, not implementation)
- [ ] Code quality (readable, documented, no warnings)

#### C. Testing Review
Run tests yourself:
```bash
cd d:\Work\ModuleHost
dotnet test ModuleHost.Core.Tests
dotnet test ModuleHost.Tests
cd ModuleHost.Benchmarks
dotnet run -c Release
```

**Verify:**
- [ ] All tests pass on your machine
- [ ] Performance numbers match report
- [ ] No flaky tests (run 5 times)
- [ ] Tests are meaningful (not just coverage padding)

#### D. Deviations Review
**For each deviation in report:**
- **Accept:** Comment why it's good, update design doc if needed
- **Reject:** Explain why, request revert or alternative
- **Discuss:** Ask clarifying questions

**Critical violations (must reject):**
- Breaking architectural principles
- Performance regression
- Removing safety guarantees
- Breaking existing tests

### Phase 4: Provide Feedback
Create: `.dev-workstream/reviews/BATCH-01-REVIEW.md`

**Use this template:**
```markdown
# BATCH 01 Review

**Reviewer:** [Your Name]
**Date:** YYYY-MM-DD
**Status:** [APPROVED / CHANGES REQUESTED / REJECTED]

## ✅ Approved Items
- Task X: Excellent implementation of ...
- Test Y: Comprehensive coverage of ...

## ⚠️ Changes Requested
- Issue 1: [Description and why it needs changing]
  - **Action:** [What developer should do]
  - **Priority:** [High/Medium/Low]

## ❌ Rejected Items
- Deviation X: [Why it violates architecture]
  - **Action:** Revert this change and use [approach] instead

## 📊 Performance Review
- Benchmark X: Met target ✅
- Benchmark Y: Below target, acceptable because ...

## 🎯 Overall Assessment
[Summary paragraph]

## 📝 Next Steps
- [ ] Developer: Address change requests
- [ ] Developer: Submit updated report
- [ ] Lead: Re-review and approve
- [ ] Lead: Prepare BATCH-02 assignment

**Approved:** [YES / NO]
```

### Phase 5: Approval or Iteration
**If APPROVED:**
1. Close BATCH-01
2. Update `.dev-workstream/TASK-TRACKER.md` (mark batch complete)
3. Assign BATCH-02 (point to `.dev-workstream/batches/BATCH-02-INSTRUCTIONS.md`)

**If CHANGES REQUESTED:**
1. Developer reads your review
2. Makes changes
3. Updates report
4. Return to Phase 3

---

## 🎯 Batch Preparation Status

### ✅ Completed
- [x] BATCH-01 Instructions created
- [x] Task Tracker created
- [x] Templates created
- [x] Workflow README created

### 🔵 To Do
- [ ] BATCH-02 Instructions (create when BATCH-01 approved)
- [ ] BATCH-03 Instructions
- [ ] BATCH-04 Instructions
- [ ] BATCH-05 Instructions
- [ ] BATCH-06 Instructions
- [ ] BATCH-07 Instructions
- [ ] BATCH-08 Instructions

---

## 🚨 Watch For These Red Flags

### During Implementation
1. **Too quiet:** Developer hasn't asked questions in 3+ days on critical batch
   - **Action:** Check in, see if they're blocked but not asking
   
2. **Too many questions:** Developer asking basic questions
   - **Action:** Point to design docs, suggest reviewing existing code
   
3. **Long delay:** Batch taking way longer than estimate
   - **Action:** Ask for status update, consider breaking into smaller batches

### During Review
1. **No deviations listed:** Either developer is perfect (unlikely) or not documenting
   - **Action:** Do extra thorough code review
   
2. **All tests passing but performance bad:** Tests not covering the right things
   - **Action:** Request additional performance tests
   
3. **High test count but low coverage:** Padding metrics
   - **Action:** Review test quality, request meaningful tests
   
4. **Changes outside batch scope:** Feature creep
   - **Action:** Discuss scope, decide to accept or defer to future batch

---

## 📊 Tracking Progress

Update `.dev-workstream/TASK-TRACKER.md` after each batch:

**When batch starts:**
```markdown
| 01 | Non-Blocking Execution | 5 | 🟡 In Progress | 2026-01-07 | - | - |
```

**When batch completes:**
```markdown
| 01 | Non-Blocking Execution | 5 | 🟢 Complete | 2026-01-07 | 2026-01-14 | 7 days |
```

**If blocked:**
```markdown
| 01 | Non-Blocking Execution | 5 | 🔴 Blocked | 2026-01-07 | - | - |
```

---

## ✅ Review Checklist Template

Copy this for each review:

```markdown
## BATCH-XX Review Checklist

### Report Review
- [ ] All tasks marked complete
- [ ] All tests passing (output included)
- [ ] Performance targets met
- [ ] Deviations documented
- [ ] Known issues acceptable

### Code Review
- [ ] Architectural fit
- [ ] Follows existing patterns
- [ ] No compiler warnings
- [ ] Public APIs documented
- [ ] Error handling appropriate

### Test Review
- [ ] Unit tests >90% coverage
- [ ] Integration tests present
- [ ] Performance benchmarks run
- [ ] Tests are meaningful
- [ ] Tests are not flaky

### Performance Review
- [ ] Main thread frame time target met
- [ ] Memory usage acceptable
- [ ] No performance regressions
- [ ] Benchmarks documented

### Decision
- [ ] APPROVED / CHANGES REQUESTED / REJECTED
```

---

## 💡 Tips for Effective Reviews

### Be Specific
❌ "This code is messy"  
✅ "The `Update()` method is 200 lines. Extract harvest logic into `HarvestEntry()` for readability."

### Explain Why
❌ "Change this"  
✅ "This violates the non-blocking principle because it calls WaitAll on all tasks. Use the harvest pattern instead."

### Recognize Good Work
✅ "Excellent defensive programming with the null checks in HarvestEntry."  
✅ "The parameterized test approach in `ProviderLeaseTests` is very clean."

### Provide Alternatives
❌ "This is wrong"  
✅ "This works but creates GC pressure. Consider using `ArrayPool<T>` instead."

### Balance Pragmatism
- **P0 issues:** Must fix (crashes, security, core arch violations)
- **P1 issues:** Should fix (performance, maintainability)
- **P2 issues:** Nice to have (style, micro-optimizations)

---

## 📁 File Organization Summary

```
.dev-workstream/
├── README.md                           # Developer workflow guide
├── TASK-TRACKER.md                     # Master task list (YOU update)
├── DEV-LEAD-GUIDE.md                   # This file (YOUR reference)
│
├── templates/
│   ├── BATCH-REPORT-TEMPLATE.md       # For developer reports
│   └── QUESTIONS-TEMPLATE.md          # For developer questions
│
├── batches/
│   ├── BATCH-01-INSTRUCTIONS.md       # ✅ Done
│   ├── BATCH-02-INSTRUCTIONS.md       # 🔵 Create next
│   └── ...
│
├── reports/                            # Developer submissions
│   ├── BATCH-01-REPORT.md             # ⏳ Waiting
│   └── ...
│
├── questions/                          # Developer questions
│   ├── BATCH-01-QUESTIONS.md          # ⏳ If needed
│   └── ...
│
└── reviews/                            # YOUR feedback
    ├── BATCH-01-REVIEW.md             # 🔵 After report
    └── ...
```

---

## 🎯 Next Actions

1. **Assign BATCH-01:**
   - Point developer to: `d:\Work\ModuleHost\.dev-workstream\batches\BATCH-01-INSTRUCTIONS.md`
   - Developer reads workflow: `d:\Work\ModuleHost\.dev-workstream\README.md`

2. **Wait for completion or questions**

3. **When report submitted:**
   - Review code changes (see folder `.dev-workstream/`)
   - Run tests yourself
   - Create review: `.dev-workstream/reviews/BATCH-01-REVIEW.md`

4. **If approved:**
   - Create BATCH-02 instructions
   - Update task tracker
   - Assign next batch

---

**Current Instruction File for Developer:**  
📄 **`d:\Work\ModuleHost\.dev-workstream\batches\BATCH-01-INSTRUCTIONS.md`**

---

Good luck managing the development! 🚀
