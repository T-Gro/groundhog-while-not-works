--- TESTS PASS VERIFIER (EXECUTABLE) ---

TYPE: executable
COMMAND: {project.testCommand}
WORKING_DIR: {project.targetDir}
GATE: hard (must pass to proceed)

WHAT IT CHECKS:
- All target-language unit tests pass
- No panics, crashes, or test timeouts
- Test output is parseable (count pass/fail)

ON FAILURE:
- Extract failing test names, error messages, expected vs actual values
- Feed structured failure report back to implementor
- Prioritize: fix failing tests from THIS sprint before worrying about others

EXPECTED OUTPUT FORMAT:
```json
{
  "passed": true|false,
  "total_tests": N,
  "passing": N,
  "failing": N,
  "failures": [
    {"test": "...", "package": "...", "error": "...", "expected": "...", "actual": "..."}
  ]
}
```
