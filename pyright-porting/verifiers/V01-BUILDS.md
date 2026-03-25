EXECUTABLE GATE — run by orchestrator, not by LLM.
Command: {project.buildCommand} + {project.lintCommand}
Hard gate: must pass before any verifier runs.
On failure: compiler errors fed back to implementor for fixing.
