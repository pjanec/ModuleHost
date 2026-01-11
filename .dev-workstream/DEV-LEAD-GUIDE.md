# Development Lead Guide - Batch Management System

**Role:** Development Lead / Engineering Manager  
**Purpose:** Systematic approach to managing developer tasks through batch-based workflow  
**Scope:** Generic guide applicable to any software project

---

## 🎯 Your Role & Responsibilities

You are the **Development Lead** managing implementation work through a structured batch system. Your responsibilities:

1. **Plan Work** - Break down large features into manageable batches
2. **Write Instructions** - Create clear, complete batch specifications
3. **Review Work** - Systematically evaluate completed batches
4. **Provide Feedback** - Give actionable, specific guidance
5. **Maintain Tracker** - Keep project progress up to date
6. **Generate Commit Messages** - Document work in version control
7. **Issue Corrections** - Create corrective batches when needed

**Key Principle:** Each batch may be executed by a **different developer**. Always include complete onboarding instructions.

---

## 📋 Folder Structure Overview

```
.dev-workstream/
├── README.md                      # Developer workflow guide (generic)
├── DEV-LEAD-GUIDE.md             # This file (your guide)
├── TASK-TRACKER.md               # Master progress tracker (you maintain)
│
├── templates/                     # Reusable templates
│   ├── BATCH-REPORT-TEMPLATE.md
│   ├── QUESTIONS-TEMPLATE.md
│   └── BLOCKERS-TEMPLATE.md
│
├── batches/                       # Batch instructions (you write)
│   ├── BATCH-01-INSTRUCTIONS.md
│   ├── BATCH-02-INSTRUCTIONS.md
│   ├── BATCH-03.1-INSTRUCTIONS.md  # Corrective batch example
│   └── ...
│
├── reports/                       # Developer submissions
│   ├── BATCH-01-REPORT.md
│   └── ...
│
├── questions/                     # Developer questions
│   ├── BATCH-01-QUESTIONS.md     # If developer needs clarification
│   └── ...
│
└── reviews/                       # Your feedback
    ├── BATCH-01-REVIEW.md
    └── ...
```

---

## 📝 Writing Batch Instructions

### Critical Rule: Complete Onboarding in Every Batch

**Each batch MUST include:**

```markdown
## 📋 Onboarding & Workflow

### Developer Instructions
[Brief introduction to this batch's goals]

### Required Reading (IN ORDER)
1. **Workflow Guide:** `.dev-workstream/README.md` - How to work with batches
2. **Design Document:** `docs/[relevant-design-doc].md` - Technical specifications
3. **Previous Review:** `.dev-workstream/reviews/BATCH-XX-REVIEW.md` - Learn from feedback
4. [Additional project-specific documents]

### Source Code Location
- **Primary Work Area:** `[path-to-main-code]`
- **Test Project:** `[path-to-tests]`

### Report Submission
**When done, submit your report to:**  
`.dev-workstream/reports/BATCH-XX-REPORT.md`

**If you have questions, create:**  
`.dev-workstream/questions/BATCH-XX-QUESTIONS.md`
```

**Why this matters:** Different developers may work on different batches. Each must be self-contained.

### Batch Instruction Structure

Every batch instruction file should follow this structure:

```markdown
# BATCH-XX: [Feature Name]

**Batch Number:** BATCH-XX  
**Phase:** [Phase Name]  
**Estimated Effort:** [hours]  
**Priority:** [HIGH/MEDIUM/LOW]  
**Dependencies:** [Previous batches required]

---

## 📋 Onboarding & Workflow
[Complete onboarding section - see above]

---

## 🎯 Batch Objectives
[What this batch accomplishes, why it matters]

---

## ✅ Tasks

### Task 1: [Task Name]
**File:** `[path/to/file]` (NEW FILE / UPDATE / REFACTOR)
**Description:** [What needs to be done]
**Requirements:**
[Detailed specifications, code examples, edge cases]

**Reference:** [Link to design doc section]

**Tests Required:**
- ✅ [Specific test scenario 1]
- ✅ [Specific test scenario 2]
- ✅ [Edge case test 3]

[Repeat for each task]

---

## 🧪 Testing Requirements
[Minimum test counts, test categories, quality standards]

---

## 📊 Report Requirements
[What developer must document in their report]

### Specific Questions You MUST Answer
1. [Question about design decision]
2. [Question about challenges]
3. [Question about integration]
[These ensure thoughtful reporting]

---

## 🎯 Success Criteria
[Checklist of what "done" means for this batch]

---

## ⚠️ Common Pitfalls to Avoid
[Known issues, mistakes to watch for]

---

## 📚 Reference Materials
[Links to docs, existing code to study, examples]
```

### Rules for Writing Good Batch Instructions

#### 1. **Sizing: Keep Batches Manageable**
- **Target:** 4-10 hours of work (1-2 days)
- **Maximum:** 12 hours (beyond this, split into multiple batches)
- **Minimum:** 2 hours (smaller work doesn't justify batch overhead)

**Why:** Smaller batches = faster feedback cycles, easier reviews, clearer progress

#### 2. **Scope: One Clear Goal Per Batch**
- ✅ Good: "Implement Ghost entity lifecycle state"
- ❌ Bad: "Implement Ghost entities and network synchronization and ownership transfer"

**Why:** Single focus makes reviews easier and allows parallel work

#### 3. **Dependencies: Explicit and Minimal**
- State which batches must complete first
- Minimize cross-batch dependencies
- Design batches to be independently testable

#### 4. **Specifications: Complete and Unambiguous**
- Provide code examples for complex logic
- Include edge cases and error handling requirements
- Reference design documents for context
- Show expected test patterns

**Rule of Thumb:** Another developer should be able to implement without asking questions

#### 5. **Tests: Specify Quality, Not Just Quantity**
- ✅ Good: "Test that Ghost entities are excluded from standard queries"
- ❌ Bad: "Write tests for Ghost entities"

**Include:**
- Minimum test counts (e.g., "15-20 unit tests")
- Specific scenarios to cover
- Quality standards (e.g., "tests must validate behavior, not just compilation")

#### 6. **Standards: Set Clear Quality Bars**

Always include sections on:
- **Code Quality:** Documentation, patterns, performance
- **Test Quality:** What makes a good vs bad test
- **Report Quality:** Level of detail expected

**Example:**
```markdown
## ⚠️ Quality Standards

**❗ TEST QUALITY EXPECTATIONS**
- **NOT ACCEPTABLE:** Tests that only verify "can I set this value"
- **REQUIRED:** Tests that verify actual behavior and edge cases

**❗ REPORT QUALITY EXPECTATIONS**
- **REQUIRED:** Thoroughly answer ALL specific questions
- **REQUIRED:** Document design decisions YOU made beyond the spec
```

#### 7. **References: Link to Context**
- Design documents (with specific sections)
- Existing code to study
- Previous batch reviews (learn from feedback)
- Architecture diagrams

#### 8. **Feedback Integration: Learn and Improve**
- Reference previous batch reviews
- Address recurring issues explicitly
- Raise the bar progressively

**Example:**
```markdown
### Based on BATCH-XX Review Feedback:
- Previous batch lacked edge case testing → This batch requires explicit edge case tests
- Previous report was too brief → This batch includes mandatory questions to answer
```

---

## 🔍 Reviewing Completed Batches

### Review Workflow

When developer submits `.dev-workstream/reports/BATCH-XX-REPORT.md`:

#### Step 1: Read the Report (10-15 minutes)

**Check for:**
- [ ] All tasks marked complete
- [ ] Test results included (full output, not just "passing")
- [ ] Deviations documented with rationale
- [ ] Specific questions answered
- [ ] Known issues/limitations listed
- [ ] Pre-submission checklist completed

**Red flags:**
- No deviations listed (suspicious - either perfect or not documenting)
- Brief answers to specific questions
- Missing sections from template
- Test counts but no test descriptions

#### Step 2: Review Code Changes (30-60 minutes)

**Examine:**

1. **Files Changed**
   ```bash
   git status
   git diff --stat
   git diff [specific-files]
   ```

2. **Architecture Fit**
   - [ ] Follows existing patterns
   - [ ] Doesn't violate architectural principles
   - [ ] Integrates cleanly with existing code
   - [ ] No circular dependencies introduced

3. **Code Quality**
   - [ ] Readable and maintainable
   - [ ] Appropriately documented (XML comments on public APIs)
   - [ ] No compiler warnings
   - [ ] Error handling present
   - [ ] Edge cases handled

4. **Performance Considerations**
   - [ ] No obvious performance issues
   - [ ] Allocations minimized where important
   - [ ] No blocking calls in async paths
   - [ ] Meets specified performance targets

#### Step 3: Review Tests (20-30 minutes)

**Critical: Test QUALITY, not just quantity**

**Check for:**
- [ ] Tests verify behavior, not implementation
- [ ] Edge cases covered
- [ ] Error conditions tested
- [ ] Integration scenarios included
- [ ] Tests are readable and maintainable
- [ ] Tests don't have copy-paste duplication

**Bad Test Example:**
```csharp
[Fact]
public void ComponentExists() {
    var component = new NetworkSpawnRequest();
    Assert.NotNull(component); // ❌ Tests nothing meaningful
}
```

**Good Test Example:**
```csharp
[Fact]
public void EntityStateTranslator_StateBeforeMaster_CreatesGhostWithPosition() {
    // Arrange
    var translator = CreateTranslator();
    var desc = new EntityStateDescriptor { Location = new Vector3(10, 20, 30) };
    
    // Act
    translator.PollIngress(mockReader, cmd, repo);
    
    // Assert
    var entity = GetEntityByNetworkId(123);
    Assert.Equal(EntityLifecycle.Ghost, repo.GetLifecycleState(entity));
    var pos = repo.GetComponentRO<Position>(entity);
    Assert.Equal(10, pos.Value.X); // ✅ Tests actual behavior
}
```

**Ask yourself:**
- If I broke the implementation, would these tests catch it?
- Do tests verify WHAT MATTERS, not just coverage?
- Are tests testing the right abstraction level?

#### Step 4: Evaluate Deviations (10-20 minutes)

**For each deviation developer documented:**

1. **Understand the rationale**
   - Why did they deviate?
   - What problem were they solving?

2. **Assess the impact**
   - Does it violate architecture?
   - Does it create technical debt?
   - Does it affect other systems?

3. **Make a decision**

**ACCEPT if:**
- Improves on original design
- Well-reasoned and documented
- Benefits outweigh risks
- Doesn't violate core principles

**REJECT if:**
- Violates architectural principles
- Creates maintainability issues
- Undocumented or poorly reasoned
- Introduces security/safety issues

**DISCUSS if:**
- Unclear trade-offs
- Multiple valid approaches exist
- Affects future work significantly

#### Step 5: Check Testing Execution (Optional but Recommended)

**Run tests yourself if:**
- Complex integration logic
- Performance-critical code
- Previous batches had test issues
- Developer's environment differs from production

```bash
# Clone the branch or pull changes
git pull

# Run tests
[project-specific test commands]

# Check for flakiness (run 3-5 times)
for i in {1..5}; do
  [test command]
done
```

### Writing Your Review

Create: `.dev-workstream/reviews/BATCH-XX-REVIEW.md`

**Review Template:**

```markdown
# BATCH-XX Review

**Reviewer:** [Your Name]  
**Date:** [YYYY-MM-DD]  
**Batch Status:** [APPROVED / APPROVED WITH NOTES / CHANGES REQUIRED / REJECTED]

---

## Overall Assessment

[2-3 sentence summary of batch quality]

**Quality Score:** [X/10]

---

## ✅ What Was Done Well

1. [Specific praise for good work]
2. [Highlight excellent decisions]
3. [Recognize quality implementations]

---

## ⚠️ Issues Found

### Issue 1: [Issue Title]

**Severity:** [CRITICAL / HIGH / MEDIUM / LOW]

**Description:** [What's wrong and why it matters]

**Impact:** [How this affects the system]

**Action Required:** [What developer should do]

**Reasoning:** [Why this needs to change]

[Repeat for each issue]

---

## 📊 Code Review Details

### [Component/File Name]
- ✅ [What's good]
- ⚠️ [What needs attention]
- ❌ [What must change]

[Repeat for major components]

---

## 🧪 Test Review

**Test Count:** [X] (Target: [Y])

**Coverage Analysis:**
| Component | Tests | Quality | Notes |
|-----------|-------|---------|-------|
| [Name] | [Count] | [Good/Adequate/Weak] | [Comments] |

**What Tests Validate:** [Summary]
**What Tests Miss:** [Gaps]
**Verdict:** [Assessment]

---

## 🔧 Action Items

### For Developer (If Changes Required)
1. [Specific action]
2. [Specific action]

### For Future Batches
1. [Lessons learned]
2. [Process improvements]

---

## ✅ Approval Decision

**Status:** [APPROVED / CHANGES REQUIRED]

**Reasoning:**
- [Point 1]
- [Point 2]

**Next Steps:**
1. [What happens next]

---

**Reviewed by:** [Your Name]  
**Approval Date:** [YYYY-MM-DD]  
**Next Batch:** [BATCH-XX or "TBD"]
```

### Review Quality Standards

**Your reviews should be:**
- **Specific:** Point to exact lines/files, not vague criticism
- **Constructive:** Explain why and suggest alternatives
- **Balanced:** Recognize good work, not just problems
- **Actionable:** Developer knows exactly what to do
- **Educational:** Help developer improve, not just fix

**Examples:**

❌ **Bad Review:**
> "Tests are not good enough."

✅ **Good Review:**
> "Tests verify basic functionality but lack edge cases. For example, `NetworkSpawnerSystem` tests don't cover what happens when TKB template is missing. Add tests for:
> 1. Missing template → Error handling
> 2. Null entity reference → Graceful failure
> 3. Invalid DIS type → Logged and skipped"

---

## 🔧 Corrective Batches - When and How

### When to Create a Corrective Batch

Use **sub-numbered batches** (e.g., BATCH-12.1) when:

1. **Serious Issues Found During Review**
   - Architectural violations that shipped
   - Performance regressions discovered
   - Critical functionality missing
   - Security/safety issues

2. **Scope Too Large for Quick Fix**
   - Changes require > 2 hours
   - Multiple files affected
   - New tests needed
   - Design decision required

3. **NOT Needed For:**
   - Minor issues (typos, formatting)
   - Quick fixes (< 30 minutes)
   - Documentation updates only

### How to Create a Corrective Batch

**File naming:** `BATCH-XX.1-INSTRUCTIONS.md` (or .2, .3 for multiple corrections)

**Structure:**

```markdown
# BATCH-XX.1: [Original Batch Name] - Corrections

**Batch Number:** BATCH-XX.1 (Corrective)  
**Parent Batch:** BATCH-XX  
**Estimated Effort:** [hours]  
**Priority:** HIGH (Corrective)

---

## 📋 Onboarding & Workflow
[Standard onboarding section - ALWAYS include]

### Background
This is a **corrective batch** addressing issues found in BATCH-XX review.

**Original Batch:** `.dev-workstream/batches/BATCH-XX-INSTRUCTIONS.md`  
**Review with Issues:** `.dev-workstream/reviews/BATCH-XX-REVIEW.md`

Please read both before starting.

---

## 🎯 Objectives

This batch corrects the following issues from BATCH-XX:

1. **Issue 1:** [Description]
   - **Why it's a problem:** [Impact]
   - **What needs to change:** [Solution]

2. **Issue 2:** [Description]
   - **Why it's a problem:** [Impact]
   - **What needs to change:** [Solution]

---

## ✅ Tasks

### Task 1: Fix [Issue from Review]
[Detailed instructions on what to change]

**Original Implementation:**
```[language]
// Current code that's wrong
```

**Required Change:**
```[language]
// Corrected code
```

**Why This Matters:** [Explanation]

**Tests Required:**
- ✅ [Test validating fix]

[Repeat for each correction]

---

## 🧪 Testing Requirements

**Existing tests that must still pass:** All tests from BATCH-XX

**New tests required:** [Specific tests for corrections]

---

## 🎯 Success Criteria

This batch is DONE when:
1. ✅ All issues from review addressed
2. ✅ All original tests still passing
3. ✅ New tests covering corrections
4. ✅ No new issues introduced

---

**Report to:** `.dev-workstream/reports/BATCH-XX.1-REPORT.md`
```

### Tracking Corrective Batches

Update TASK-TRACKER.md:

```markdown
| 12   | Network-ELM Foundation | 4-6  | 🟢 Complete* | 2026-01-10 | 2026-01-11 | 1 day |
| 12.1 | Foundation Corrections | 2    | 🟡 In Progress | 2026-01-11 | -         | -     |

*Corrections required - see BATCH-12.1
```

---

## 📝 Git Commit Message Generation

### Your Responsibility: Generate, Don't Execute

**CRITICAL RULE:** You **GENERATE** commit messages, you **DO NOT** run `git commit`.

**Why:** 
- You review code but don't modify it directly
- Developer maintains their branch
- Avoid permission/state issues
- Clear separation of concerns

### How to Generate Commit Messages

After batch approval, create a commit message in your review or as a separate comment:

**Format:**

```
[type]: [Brief summary] (BATCH-XX)

[Detailed description of changes]

[Component sections]

[Testing section]

[Related references]
```

**Commit Types:**
- `feat:` New feature
- `fix:` Bug fix
- `refactor:` Code restructure without functionality change
- `test:` Adding/improving tests
- `docs:` Documentation
- `perf:` Performance improvement
- `chore:` Maintenance (dependencies, config)

**Example: Feature Batch**

```
feat: Add Network-ELM integration foundation layer (BATCH-12)

Implements foundational infrastructure for Network-ELM integration to support
distributed entity lifecycle management and partial ownership.

New Components:
- NetworkConstants: Centralized descriptor IDs, message IDs, and timeouts
- MasterFlags enum: ReliableInit flag for distributed construction coordination
- NetworkSpawnRequest: Transient component for entity spawner system
- PendingNetworkAck: Tag for entities awaiting network acknowledgment
- ForceNetworkPublish: Tag for immediate descriptor publication
- DescriptorAuthorityChanged event: Ownership change notifications

New Interfaces:
- IOwnershipDistributionStrategy: Strategy pattern for partial ownership assignment
- INetworkTopology: Abstraction for peer discovery and topology management
- ITkbDatabase: TKB template access without concrete dependencies

New Messages:
- EntityLifecycleStatusDescriptor: Reliable init ACK protocol support

Utilities:
- OwnershipExtensions.PackKey/UnpackKey: Composite key packing for (TypeId, InstanceId)
- DefaultOwnershipStrategy: Default implementation (all descriptors to master)

Testing:
- 17 unit tests covering all new components and logic
- Fixed async/await patterns in ModuleCircuitBreakerTests and SnapshotPoolTests

This batch establishes the foundation for BATCH-13 (translators and spawner system).

Related: docs/[design-doc-name].md
```

**Example: Corrective Batch**

```
fix: Correct ownership event emission in OwnershipUpdateTranslator (BATCH-12.1)

Addresses critical issue where DescriptorAuthorityChanged events were not emitted
during ownership transfers, preventing modules from reacting to ownership changes.

Changes:
- OwnershipUpdateTranslator: Added event emission logic
- OwnershipUpdateTranslator: Added ForceNetworkPublish component for SST confirmation
- Added integration test for event consumption by subscribing modules

Testing:
- 5 new tests for ownership transfer events
- All BATCH-12 tests still passing

Fixes: Issue #1 from BATCH-12 review
Related: .dev-workstream/reviews/BATCH-12-REVIEW.md
```

**Provide to Developer:**

In your review or via separate communication:

```markdown
## 📝 Git Commit Message

When you commit this batch, use the following message:

\`\`\`
[paste commit message here]
\`\`\`
```

---

## 📊 Maintaining the Task Tracker

### Your Responsibility

Keep `.dev-workstream/TASK-TRACKER.md` up to date after each batch milestone.

### Tracker Structure

```markdown
# Project Task Tracker

## Status Legend
- 🟢 Complete
- 🟡 In Progress
- 🔴 Blocked
- ⚪ Not Started
- ⏸️ Deferred

## Task Overview

| # | Task Name | Est. Days | Status | Started | Completed | Actual |
|---|-----------|-----------|--------|---------|-----------|--------|
| 01 | [Feature Name] | 2 | 🟢 Complete | 2026-01-10 | 2026-01-11 | 1 day |
| 02 | [Feature Name] | 3 | 🟡 In Progress | 2026-01-12 | - | - |
| 03 | [Feature Name] | 4 | ⚪ Not Started | - | - | - |

## Detailed Status

### BATCH-01: [Feature Name] 🟢
**Status:** Complete  
**Developer:** [Name]  
**Files:** `.dev-workstream/batches/BATCH-01-INSTRUCTIONS.md`  
**Report:** `.dev-workstream/reports/BATCH-01-REPORT.md`  
**Review:** `.dev-workstream/reviews/BATCH-01-REVIEW.md`  
**Commit:** [commit hash if available]
```

### When to Update

1. **Batch Assigned:** Status → 🟡 In Progress, add Started date
2. **Batch Completed:** Status → 🟢 Complete, add Completed date and Actual days
3. **Batch Blocked:** Status → 🔴 Blocked, add notes in Detailed Status
4. **Batch Deferred:** Status → ⏸️ Deferred, explain why

### Update Frequency

- **After each batch review** (approved or requiring changes)
- **When priorities change**
- **When new batches are created**
- **Weekly summary** (snapshot of overall progress)

---

## 🔄 Complete Workflow Summary

### Phase 1: Planning & Assignment

1. **Break down feature** into batches (4-10 hours each)
2. **Write batch instructions** following structure above
3. **Include complete onboarding** (different developer may work on it)
4. **Update task tracker** (new batch added)
5. **Assign to developer** (point to instruction file)

### Phase 2: Development (Developer Works)

**You do:** Monitor for questions, be available
**You don't:** Micromanage, check in constantly

**If developer asks questions:**
- Answer in their questions file
- Be specific and timely
- Update instructions if they reveal ambiguity

### Phase 3: Review

1. **Read report** (10-15 min)
2. **Review code** (30-60 min)
3. **Evaluate tests** (20-30 min)
4. **Assess deviations** (10-20 min)
5. **Write review** (20-30 min)

**Total: 1.5-3 hours per batch**

### Phase 4: Decision

#### If APPROVED:
1. **Write review** with approval
2. **Generate git commit message** (don't run git commit!)
3. **Update task tracker** (mark complete)
4. **Prepare next batch** or celebrate completion

#### If CHANGES REQUIRED (Minor):
1. **Write review** with specific changes
2. **Developer fixes** and updates report
3. **Quick re-review** (15-30 min)
4. **Approve** and continue

#### If SERIOUS ISSUES (Need Corrective Batch):
1. **Write review** documenting issues
2. **Create BATCH-XX.1-INSTRUCTIONS.md**
3. **Assign corrective batch** to developer
4. **Update task tracker**

---

## 🚨 Watch for Red Flags

### During Development

🚨 **Too quiet** - No questions in 3+ days on complex batch
- **Action:** Check in, ask if blocked

🚨 **Too many basic questions** - Developer doesn't understand fundamentals
- **Action:** Point to docs, consider pairing session

🚨 **Scope creep** - Developer working beyond batch scope
- **Action:** Clarify scope, defer extras to future batch

🚨 **Long delays** - Batch taking 2x+ estimate
- **Action:** Status check, consider breaking into smaller batches

### During Review

🚨 **No deviations documented** - Suspiciously perfect or not documenting
- **Action:** Extra thorough code review

🚨 **Shallow tests** - High count but testing nothing meaningful
- **Action:** Request quality tests, provide examples

🚨 **Brief report** - Skipped sections, minimal answers
- **Action:** Reject, request complete report

🚨 **Performance issues** - Tests pass but performance bad
- **Action:** Request benchmarks, investigate

🚨 **Architectural violations** - Doesn't follow design
- **Action:** Serious discussion, possible rejection

---

## 💡 Tips for Effective Leadership

### Be Specific
❌ "This code is messy"  
✅ "The `ProcessEntity()` method is 200 lines. Extract Ghost promotion logic into `PromoteGhostToConstructing()` for clarity."

### Explain Why
❌ "Change this"  
✅ "This creates a race condition because X accesses Y without synchronization. Use lock or make Y thread-local."

### Recognize Good Work
✅ "Excellent edge case handling with the null template check - exactly what was needed."  
✅ "The test structure in `NetworkIntegrationTests` is very clear and maintainable."

### Provide Alternatives
❌ "This is wrong"  
✅ "This works but causes N+1 queries. Consider loading all entities upfront or using a batch query."

### Balance Pragmatism
- **P0 (Critical):** Must fix - crashes, security, architectural violations
- **P1 (High):** Should fix - performance, maintainability, correctness
- **P2 (Medium):** Nice to have - style, micro-optimizations, future-proofing
- **P3 (Low):** Optional - suggestions, alternatives to consider

### Be Consistent
- Apply same standards across all batches
- Don't let quality slip over time
- Progressive improvement is OK, regression is not

### Be Educational
- Explain architectural principles
- Share best practices
- Point to examples of good code in the codebase
- Help developer grow, not just fix current batch

---

## ✅ Review Checklist Template

Copy this for each review:

```markdown
## BATCH-XX Review Checklist

### Report Quality
- [ ] All tasks marked complete with details
- [ ] Test results included (full output)
- [ ] Specific questions thoroughly answered
- [ ] Deviations documented with rationale
- [ ] Known issues/limitations listed
- [ ] Pre-submission checklist completed

### Code Quality
- [ ] Follows existing patterns and architecture
- [ ] No compiler warnings
- [ ] Public APIs documented (XML comments)
- [ ] Error handling appropriate
- [ ] Edge cases handled
- [ ] No obvious performance issues

### Test Quality  
- [ ] Tests verify behavior (not just compilation)
- [ ] Edge cases covered
- [ ] Error conditions tested
- [ ] Integration scenarios present
- [ ] Tests are maintainable
- [ ] Minimum test count met

### Architecture
- [ ] Fits with existing design
- [ ] Doesn't violate principles
- [ ] Integrates cleanly
- [ ] No circular dependencies
- [ ] Appropriate abstractions

### Performance
- [ ] Meets specified targets
- [ ] No obvious regressions
- [ ] Allocation patterns reasonable
- [ ] Benchmarks run (if applicable)

### Decision
- [ ] **APPROVED** - Ready to merge
- [ ] **APPROVED WITH NOTES** - Minor suggestions for future
- [ ] **CHANGES REQUIRED** - Specific fixes needed
- [ ] **CORRECTIVE BATCH REQUIRED** - Serious issues need dedicated work
```

---

## 📚 Quick Reference

### File Locations

```
Instruction:  .dev-workstream/batches/BATCH-XX-INSTRUCTIONS.md
Report:       .dev-workstream/reports/BATCH-XX-REPORT.md
Questions:    .dev-workstream/questions/BATCH-XX-QUESTIONS.md  (if needed)
Review:       .dev-workstream/reviews/BATCH-XX-REVIEW.md
Tracker:      .dev-workstream/TASK-TRACKER.md
```

### Batch Numbering

- **Sequential:** BATCH-01, BATCH-02, BATCH-03...
- **Corrective:** BATCH-12.1, BATCH-12.2 (sub-batches)
- **Parallel work:** BATCH-05a, BATCH-05b (if needed, but avoid)

### Time Estimates

- **Write batch:** 1-2 hours (first time), 30-45 min (with practice)
- **Review batch:** 1.5-3 hours (thorough)
- **Quick re-review:** 15-30 min (after minor fixes)

---

## 🎯 Success Metrics

Track these to improve your batch management:

- **Batch acceptance rate** - Target: >80% approved first time
- **Rework rate** - Target: <20% need corrections
- **Estimate accuracy** - Target: ±25% of estimated time
- **Test quality trend** - Improving over time
- **Developer questions** - Declining over time (better instructions)

---

**Remember:** You're managing work, not doing it. Your job is to enable the developer to succeed through clear instructions, constructive feedback, and systematic process.

Good luck leading the development! 🚀
