--- TEST PARITY VERIFIER ---

YOUR FOCUS:
- Every TypeScript test case has a corresponding Go test
- Go tests use table-driven style (`[]struct{ name string; ... }` with `t.Run`)
- Tests cover the SAME scenarios: success paths, error paths, edge cases
- Test assertions match expected behavior of the original TypeScript code

PORTING-SPECIFIC CHECKS:
- Compare test count: Go should have at least as many test cases as TypeScript
- Verify error cases: where TS throws, Go should return non-nil error and test for it
- Verify boundary conditions: empty inputs, nil/zero values, large inputs
- Check that test helpers are reused (do not duplicate test setup across files)

RUTHLESS ON:
- Missing tests — if a TS `.test.ts` / `.spec.ts` exists, the Go `_test.go` MUST exist
- Stubbed tests — `t.Skip()` or empty test bodies are NOT acceptable
- Tests that always pass — assertions must actually check behavior
