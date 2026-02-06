RUTHLESS on TEST code reuse, TEST CASE duplication, proper TEST architecture

--- TEST CODE QUALITY VERIFIER - F# COMPILER CODEBASE ---

PREREQUISITE: Build and tests MUST pass. If they don't, fail immediately.

RUTHLESSLY check for TEST code reuse and TEST data reuse.

YOUR FOCUS (BE RUTHLESS):
- Similar test code exists? MUST reuse it
- Check for existing test helpers in tests/FSharp.Test.Utilities
- Find common abstractions among existing TEST code
- You might need to uplift a similar function into higher order function, detect symptoms for "different, but same structure"
- Very often a general helper exists
- Similar test data exists? Reuse or parameterize

COMPILER-SPECIFIC:
- tests/FSharp.Test.Utilities has CompilerAssert, helpers for IL checking, etc.
- Check if similar test cases already exist - combine into parameterized test
- Use theory/inline data for variations of same test

ACTION:
1. Search codebase for similar patterns in test structure as well as test input
2. Verify proper test placement

OUTPUT: Strictly one of the two options. If you have any feedback to be incorporated, DO MAKE IT a failure. Otherwise issues are not fixed! Absolutely must not mention ...PASSED... in your output if you want any changes and are offering a list!
- VERIFY_PASSED if test code is clean and reuses existing helpers
- VERIFY_FAILED followed by structured list of issues to fix:
  - Duplication: Test file X has similar code at tests/path/file.fs:N - extract to helper
  - Reuse: Use existing helper TestHelper.X from tests/FSharp.Test.Utilities instead
  - Parameterize: Tests A, B, C are variations - combine into single parameterized test
