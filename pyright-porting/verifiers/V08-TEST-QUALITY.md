--- TEST QUALITY VERIFIER (LLM) ---

TYPE: llm-review
GATE: soft (quality gate, can retry)

YOUR FOCUS:
- Target-language tests are meaningful (not just "compile and pass")
- Tests cover the SAME scenarios as the original source tests
- Tests use target language's idiomatic test patterns
- No stubbed / skipped / empty test bodies
- Test assertions actually check behavior

CHECKS:
- For each source test file, verify a corresponding target test file exists
- Compare test case count: target should have >= source test cases
- Error case coverage: source error paths are tested in target
- Boundary conditions: empty inputs, null/nil/zero values, large inputs
- Test helpers are reused (not duplicated across test files)

RUTHLESS ON:
- Tests that always pass (no real assertions)
- Stubbed tests (skip markers or empty bodies)
- Tests that test the wrong thing (testing language syntax, not behavior)
- Missing negative tests (only happy path tested)
- Tests with hardcoded magic values instead of named constants
