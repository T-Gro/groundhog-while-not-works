--- TEST CODE QUALITY VERIFIER ---

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
