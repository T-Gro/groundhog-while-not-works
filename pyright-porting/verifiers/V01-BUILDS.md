--- BUILDS VERIFIER (EXECUTABLE) ---

TYPE: executable
COMMAND: {project.buildCommand}
WORKING_DIR: {project.targetDir}
GATE: hard (must pass to proceed)

WHAT IT CHECKS:
- All target language source files compile without errors
- Static analysis / linting passes (if configured in project.json)
- No import/reference cycles
- Package/module declarations match directory structure

ON FAILURE:
- Extract compiler error messages (file, line, error text)
- Feed these back to the implementor agent as structured context
- The implementor must fix ALL build errors before any other verifier runs

EXPECTED OUTPUT FORMAT:
```json
{
  "passed": true|false,
  "errors": [{"file": "...", "line": N, "message": "..."}],
  "warnings": [...]
}
```
