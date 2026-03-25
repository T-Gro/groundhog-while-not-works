--- ORACLE COMPARISON VERIFIER (EXECUTABLE) ---

TYPE: executable
GATE: soft (metric, not blocking — informs convergence)

WHAT IT CHECKS:
- Run the ported tool on each test sample
- Compare output vs golden reference (expected output from original tool)
- Report: total samples, passing, failing, error categories

MECHANISM:
1. For each test sample in {project.oracleDir}:
   a. Run ported tool: `{project.oracleRunCommand} {sample}`
   b. Load golden reference: `{project.goldenDir}/{sample}.expected.json`
   c. Compare outputs according to project-specific comparison rules
   d. Score: match = pass, difference = fail
2. Aggregate: passing / total = convergence %

FAILURE CATEGORIZATION:
- "crash" — ported tool panics/crashes
- "timeout" — analysis takes too long
- "missing_output" — expected output not produced
- "extra_output" — unexpected output produced
- "wrong_location" — right content but wrong position
- "wrong_content" — right position but wrong content
- "parse_error" — input parsing fails
