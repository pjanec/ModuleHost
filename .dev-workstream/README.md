# ModuleHost Development Workflow

## 🎯 Overview

This folder contains the development workflow for implementing ModuleHost advanced features. You are the developer responsible for implementing batches of tasks according to specifications provided by the Development Lead.

## 📁 Folder Structure

```
.dev-workstream/
├── README.md                    ← This file (workflow instructions)
├── templates/                   ← Templates for your submissions
│   ├── BATCH-REPORT-TEMPLATE.md
│   ├── QUESTIONS-TEMPLATE.md
│   └── BLOCKERS-TEMPLATE.md
├── batches/                     ← Task instructions from Dev Lead
│   ├── BATCH-01-INSTRUCTIONS.md
│   ├── BATCH-02-INSTRUCTIONS.md
│   └── ...
├── reports/                     ← Your completed batch reports
│   ├── BATCH-01-REPORT.md
│   ├── BATCH-02-REPORT.md
│   └── ...
├── questions/                   ← Your questions if blocked
│   ├── BATCH-01-QUESTIONS.md
│   └── ...
└── reviews/                     ← Dev Lead feedback
    ├── BATCH-01-REVIEW.md
    └── ...
```

## 🔄 Workflow Process

### Step 1: Receive Batch Instructions
- Check `batches/BATCH-XX-INSTRUCTIONS.md` for your tasks
- Read all referenced design documents
- Review existing codebase to understand current implementation

### Step 2: Work on Tasks
- Implement features according to specifications
- Write unit tests as specified in the instructions
- Run all tests (unit + integration) frequently
- Document any deviations or improvements you make
- If blocked or uncertain, create a questions file

### Step 3: Submit Report
When all tasks are complete and tests passing:

1. **Copy the report template:**
   ```
   cp templates/BATCH-REPORT-TEMPLATE.md reports/BATCH-XX-REPORT.md
   ```

2. **Fill out the report with:**
   - Task completion status
   - Test results (all must pass)
   - Any deviations or improvements made
   - Performance observations
   - Integration notes
   - Known issues or limitations

3. **Notify the Dev Lead** that the batch is ready for review

### Step 4: Handle Questions/Blockers
If you encounter questions or blockers during development:

1. **Create a questions file:**
   ```
   cp templates/QUESTIONS-TEMPLATE.md questions/BATCH-XX-QUESTIONS.md
   ```

2. **Document your questions clearly:**
   - What you're trying to accomplish
   - What's unclear or blocking you
   - What options you've considered
   - Your recommendation (if any)

3. **Notify the Dev Lead** and wait for answers before proceeding

## ✅ Definition of Done

A batch is considered DONE when:

1. ✅ All tasks implemented according to specifications
2. ✅ All unit tests written and PASSING
3. ✅ All integration tests PASSING
4. ✅ Code follows existing architecture patterns
5. ✅ Performance benchmarks run (if applicable)
6. ✅ Report submitted with complete information
7. ✅ No compilation warnings
8. ✅ Code is committed to version control

## 📋 Important Guidelines

### Code Quality
- Follow existing code style and patterns in the codebase
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and single-purpose

### Testing
- Test behavior, not implementation details
- Include edge cases and error conditions
- Use descriptive test names: `Test_NonBlockingModule_WhenStillRunning_SkipsAndContinues`
- Verify performance characteristics where specified

### Deviations
If you identify improvements or необходимости to deviate from specs:
- Document WHY in your report
- Explain the benefit
- Note any risks or tradeoffs
- Implement it BUT also flag it for review

### Performance
- Run benchmarks when specified
- Report timing and memory metrics
- Flag any concerning performance issues

## 🚨 When to Ask Questions

Ask questions when:
- Specification is ambiguous or contradictory
- Integration point with existing code is unclear
- Performance target seems impossible to meet
- Architecture decision affects multiple systems
- You discover a fundamental design issue

DON'T ask questions for:
- Minor implementation details (use your judgment)
- Code style preferences (follow existing patterns)
- Basic C# syntax (research first)

## 📊 Reporting Standards

### Test Results Format
```
✅ All Tests Passing
- Unit Tests: 47/47 passing
- Integration Tests: 12/12 passing
- Performance Tests: 5/5 passing (all within targets)
```

### Performance Metrics Format
```
Benchmark: ModuleDispatch_NonBlocking_100Modules
- Average: 2.3ms
- P95: 3.1ms
- P99: 4.2ms
- Target: <5ms ✅
```

### Code Changes Summary
```
Files Added: 5
Files Modified: 8
Lines Added: 1,234
Lines Removed: 67
Test Coverage: 94% (target: >90%)
```

## 🔧 Development Environment

### Running Tests
```bash
# Unit tests only
dotnet test ModuleHost.Core.Tests

# Integration tests only  
dotnet test ModuleHost.Tests

# All tests with verbosity
dotnet test --verbosity normal

# With coverage
dotnet test /p:CollectCoverage=true
```

### Running Benchmarks
```bash
cd ModuleHost.Benchmarks
dotnet run -c Release
```

## 📚 Key Reference Documents

Always review these before starting a batch:
- **Design Document:** `docs/DESIGN-IMPLEMENTATION-PLAN.md`
- **Architecture Overview:** `docs/ModuleHost-TODO.md`
- **Current Implementation:** Review existing code in `ModuleHost.Core/`

## ⚡ Quick Start Checklist

Before starting each batch:
- [ ] Read batch instructions completely
- [ ] Review referenced design sections
- [ ] Examine existing code structure
- [ ] Set up test project if needed
- [ ] Understand acceptance criteria
- [ ] Create questions file if anything unclear

During development:
- [ ] Write tests first (TDD approach recommended)
- [ ] Run tests frequently
- [ ] Document significant decisions
- [ ] Track any deviations

Before submitting:
- [ ] All tests passing
- [ ] No compiler warnings
- [ ] Code committed
- [ ] Report filled out completely
- [ ] Performance benchmarks run (if applicable)

---

**Remember:** You're a capable developer. Use your initiative, but document your decisions. The Lead trusts your judgment but needs to review the results.

**Communication:** All communication happens through markdown files in this folder structure. No verbal discussions assumed.

Good luck! 🚀
