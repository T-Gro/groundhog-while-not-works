---
---
<!-- 
  SPRINT FILE FORMAT
  ==================
  The --- markers above are optional YAML frontmatter (currently unused but reserved for future metadata).
  You can omit them, but keeping them ensures consistency.
  
  REQUIRED SECTIONS:
  1. # Sprint: [title]
  2. ## Context
  3. ## Description  
  4. ## Definition of Done
  
  CRITICAL: This file is ALL the implementor agent sees. Include EVERY detail needed.
-->

# Sprint: [Replace with sprint title]

## Context
[Why this sprint exists. What problem it solves. What user need it addresses.]

## Description
[DETAILED implementation guidance for the implementor agent:]

### Files to Modify
- `path/to/file.fs` - what to change and why

### Implementation Steps
1. First, do X
2. Then, do Y  
3. Finally, do Z

### Patterns to Follow
- Reference existing code: `path/to/similar.fs` function `existingHelper`
- Follow the existing style in the codebase

### What to Avoid
- Do NOT break existing behavior X
- Do NOT modify file Y

### Expected Behavior
When user does A, the result should be B.
Example: `input -> expected output`

## Definition of Done
- Build succeeds with no new warnings
- Feature works as described in Expected Behavior
- Unit tests added for new functionality
- All tests pass locally (dotnet test -c Release)
- Changes committed with descriptive message
