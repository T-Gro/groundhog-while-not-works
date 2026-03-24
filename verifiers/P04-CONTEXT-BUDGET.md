--- CONTEXT BUDGET VERIFIER ---

YOUR FOCUS:
- Sprint stays within the allocated context budget
- Changes are scoped to the files listed in the sprint, not wider
- No over-reaching into modules that belong to other sprints
- Shared type mappings file was updated (not left stale)

PORTING-SPECIFIC CHECKS:
- Count lines of Go code generated — should be proportional to TypeScript source lines (±50%)
- Verify no files outside the target Go package were modified (unless explicitly listed)
- Verify the shared type_mappings.md was updated with any new type mappings
- Check that the sprint did not introduce circular dependencies between Go packages

WHY THIS MATTERS:
- Porting a huge codebase requires strict scoping so each sprint fits in an LLM context window
- Over-scoped sprints lead to context overflow, incomplete output, and hallucinated code
- Under-scoped sprints waste iterations on trivial changes
- Strict scoping enables parallelism — independent sprints can run concurrently
