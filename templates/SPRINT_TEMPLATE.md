---
---
<!-- 
  SPRINT FILE FORMAT
  ==================
  The --- markers above are optional YAML frontmatter. They are currently unused and reserved for future metadata.
  You can omit them, but keeping them is recommended for consistency.
  
  REQUIRED SECTIONS:
  1. # Sprint: [title]
  2. ## Context
  3. ## Description  
  4. ## Validation scope and completion contract
  5. ## Definition of Done
  
  The implementor agent sees only this file. Include every detail needed to implement the sprint.
-->

# Sprint: [Replace with sprint title]

## Context
[State why this sprint exists, what problem it solves, and what user need it addresses.]

## Description
[Give the implementor agent complete implementation guidance:]

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

## Validation scope and completion contract
[List exact commands, working directories, budgets, and evidence required for this sprint.
Select scoped checks for the changed code; do not default to unrelated root build/test commands.
If a real smoke trial is required, specify its acceptance criteria; unavailable is not passing.]

## Definition of Done
- Required validation checks succeed with recorded evidence
- Feature works as described in Expected Behavior
- Unit tests added for new functionality
- All tests required by the validation contract pass locally
- Changes committed with descriptive message
