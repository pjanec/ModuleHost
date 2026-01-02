# Developer Workstream - Instructions

**Project:** B-One NG Module Host - Hybrid GDB+SoD Architecture  
**Development Leader:** [Your Manager]  
**Developer:** [You]  
**Start Date:** January 4, 2026

---

## 📋 Overview

You will receive implementation tasks in **batches**. Each batch contains multiple related tasks that form a cohesive development phase. Work through all tasks in a batch before submitting your report.

**Your responsibilities:**
1. Implement all tasks in the batch
2. Write unit tests (and integration tests where specified)
3. Ensure all tests pass
4. Report results using the specified format
5. Raise blockers or questions immediately

---

## 📁 Folder Structure

```
.dev-workstream/
├── README.md                    (This file - your instructions)
├── templates/                   (Templates for your reports)
│   ├── BATCH-REPORT-TEMPLATE.md
│   ├── QUESTIONS-TEMPLATE.md
│   └── BLOCKERS-TEMPLATE.md
├── batches/                     (Task batches from manager)
│   ├── BATCH-01-INSTRUCTIONS.md
│   ├── BATCH-02-INSTRUCTIONS.md
│   └── ...
├── reports/                     (Your submissions)
│   ├── BATCH-01-REPORT.md
│   ├── BATCH-01-QUESTIONS.md (if needed)
│   └── BLOCKERS-ACTIVE.md (if blocked)
└── reviews/                     (Manager feedback)
    ├── BATCH-01-REVIEW.md
    └── ...
```

---

## 🔄 Workflow

### Step 1: Receive Batch Instructions

Manager will create: `.dev-workstream/batches/BATCH-{NN}-INSTRUCTIONS.md`

**You will be notified with the full path.**

### Step 2: Read Instructions Carefully

- Review all tasks in the batch
- Read referenced design documents
- Understand acceptance criteria
- Note test requirements

### Step 3: Work on Tasks

**Guidelines:**
- ✅ Work on tasks in the order specified (unless dependencies allow parallelization)
- ✅ **Max 2 tasks in progress simultaneously** (to avoid context switching)
- ✅ Follow existing code conventions and architecture
- ✅ Write tests as you go (TDD encouraged)
- ✅ Ensure code compiles **without warnings** (`dotnet build` must be clean)

**If you take initiative beyond instructions:**
- ✅ Document it clearly in your report
- ✅ Explain rationale
- ✅ Mark as "Additional work - needs review"

### Step 4: Handle Blockers or Questions

**If Blocked:**
1. Create/Update: `reports/BLOCKERS-ACTIVE.md` using the template
2. **Update immediately** when blocked (don't wait for batch completion)
3. Manager will respond in-line with guidance
4. Continue with other tasks while waiting (if possible)

**If Questions:**
1. Create: `reports/BATCH-{NN}-QUESTIONS.md` using the template
2. Continue with tasks you can complete
3. Manager will provide `BATCH-{NN}-ANSWERS.md`

### Step 5: Complete Batch

When all tasks are complete (or blocked):

1. **Run all tests:**
   ```powershell
   dotnet test --nologo --verbosity minimal
   ```

2. **Check for warnings:**
   ```powershell
   dotnet build --nologo | Select-String "warning"
   ```
   ⚠️ **Must be zero warnings!**

3. **Create Batch Report:**
   - Use template: `templates/BATCH-REPORT-TEMPLATE.md`
   - Save as: `reports/BATCH-{NN}-REPORT.md`
   - Fill in ALL sections (see template)

4. **Notify manager** that batch is complete

### Step 6: Receive Review

Manager will create: `reviews/BATCH-{NN}-REVIEW.md`

**Possible outcomes:**
- ✅ **Approved** → Manager assigns next batch
- ⚠️ **Changes Requested** → Fix issues, resubmit report
- ❌ **Major Issues** → Manager provides corrective instructions

---

## 📝 Report Requirements

### Batch Report Must Include:

1. **Executive Summary**
   - Batch status (Complete / Partial / Blocked)
   - Overall progress
   - Critical issues

2. **Task Status Table**
   - Each task: Status, Tests Status, Notes

3. **Files Changed**
   - List all modified/created files
   - Brief description of changes

4. **Test Results**
   - Total tests: Pass / Fail
   - Performance benchmark results (if applicable)
   - Test output summary

5. **Additional Work**
   - Any work done beyond instructions
   - Rationale for each item

6. **Known Issues**
   - Any bugs or concerns discovered
   - Suggestions for resolution

7. **Next Steps**
   - Blockers preventing progress
   - Recommendations for next batch

**Use the template!** (`templates/BATCH-REPORT-TEMPLATE.md`)

---

## ✅ Definition of Done (DoD)

A task is **DONE** when:

- [x] Code implemented according to acceptance criteria
- [x] All specified unit tests written and **passing**
- [x] Integration tests written (if specified) and passing
- [x] Code compiles **without warnings** (treat warnings as errors)
- [x] Performance benchmarks pass (if tests include them)
- [x] XML documentation comments added for public APIs
- [x] Code follows existing conventions (naming, structure, patterns)
- [x] No regressions (existing tests still pass)

**If any DoD criteria fails, task is NOT done.**

---

## 🚨 Critical Rules

### Mandatory

1. ⛔ **NO warnings allowed** - Treat warnings as errors
2. ⛔ **ALL tests must pass** - No exceptions
3. ⛔ **Follow architecture strictly** - Don't violate documented patterns
4. ⛔ **Tier 2 immutability** - Managed components MUST be immutable records
5. ⛔ **No breaking changes** - Unless explicitly instructed

### Encouraged

1. ✅ **Ask questions early** - Don't guess if unclear
2. ✅ **Report blockers immediately** - Don't wait for batch end
3. ✅ **Take reasonable initiative** - But document it clearly
4. ✅ **Optimize for readability** - Code will be reviewed
5. ✅ **Test edge cases** - Not just happy path

---

## 📚 Reference Documents

**Always available in `/docs`:**

- **IMPLEMENTATION-SPECIFICATION.md** - Master specification
- **API-REFERENCE.md** - Complete API documentation
- **IMPLEMENTATION-TASKS.md** - Full task list
- **HYBRID-ARCHITECTURE-QUICK-REFERENCE.md** - Quick reference
- **MODULE-IMPLEMENTATION-EXAMPLES.md** - Code examples
- **MIGRATION-PLAN-Hybrid-Architecture.md** - Migration strategy
- **MEMORY-LAYOUT-DIAGRAMS.md** - Memory architecture

**Refer to these documents when implementing!**

---

## 📧 File Naming Conventions

**You must use these exact names:**

| Type | Filename | Location |
|------|----------|----------|
| **Batch Report** | `BATCH-{NN}-REPORT.md` | `reports/` |
| **Questions** | `BATCH-{NN}-QUESTIONS.md` | `reports/` |
| **Active Blockers** | `BLOCKERS-ACTIVE.md` | `reports/` |

**Examples:**
- `reports/BATCH-01-REPORT.md`
- `reports/BATCH-01-QUESTIONS.md`
- `reports/BLOCKERS-ACTIVE.md` (single file, update in-place)

---

## 🔍 Code Review Checklist

Before submitting batch report, self-review:

**Architecture:**
- [ ] Follows 3-world topology (Live, Fast GDB, Slow SoD)
- [ ] Respects mutability boundaries (Live=RW, Replicas=RO)
- [ ] Uses correct synchronization API (`SyncFrom`)
- [ ] Tier 2 components are immutable records

**Code Quality:**
- [ ] No compiler warnings
- [ ] No magic numbers (use constants)
- [ ] Clear variable names
- [ ] Methods < 50 lines (reasonable complexity)
- [ ] XML comments on public APIs

**Testing:**
- [ ] Unit tests cover happy path
- [ ] Unit tests cover error cases
- [ ] Integration tests validate component interaction
- [ ] Performance benchmarks pass (if applicable)
- [ ] All tests pass (`dotnet test`)

**Performance:**
- [ ] No obvious performance issues
- [ ] Dirty tracking used correctly
- [ ] No unnecessary allocations
- [ ] Chunk versioning implemented correctly

---

## 🎯 Success Criteria

**You are successful when:**

1. ✅ All batch tasks marked DONE (pass DoD)
2. ✅ All tests passing (100% pass rate)
3. ✅ Zero compiler warnings
4. ✅ Batch report complete and accurate
5. ✅ Manager approves batch in review

**Manager will evaluate:**
- Correctness (does it work as specified?)
- Quality (is the code maintainable?)
- Completeness (are all DoD criteria met?)
- Architecture compliance (follows design documents?)
- Testing (adequate coverage and quality?)

---

## 🚀 Getting Started

1. **Wait for manager notification** with first batch path
2. **Read batch instructions carefully**
3. **Review reference documents**
4. **Start implementing tasks**
5. **Submit batch report when complete**

---

## 💡 Tips for Success

**Do:**
- ✅ Read all batch instructions before starting
- ✅ Refer to design docs frequently
- ✅ Write tests as you implement (TDD)
- ✅ Ask questions early if unclear
- ✅ Report blockers immediately
- ✅ Take initiative when it aligns with architecture

**Don't:**
- ❌ Skip reading design documents
- ❌ Ignore compiler warnings
- ❌ Write code without tests
- ❌ Deviate from architecture without asking
- ❌ Wait until batch end to report blockers
- ❌ Submit incomplete work

---

## 📞 Communication

**All communication via markdown files in this folder.**

- **Manager → You:** Batch instructions, reviews, answers
- **You → Manager:** Reports, questions, blockers

**No email, no chat - everything documented in markdown.**

---

**Good luck! 🚀**

*Last Updated: January 4, 2026*
