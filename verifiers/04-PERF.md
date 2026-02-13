--- PERF VERIFIER ---

Find performance issues in compiler code.
This is a COMPILER - no SQL, no web, no ORM, no EF.

YOUR FOCUS (BE RUTHLESS ABOUT HAPPY PATHS AND 99pct SCENARIOS):
- Allocations in happy path loops
- Closures in hot paths
- String concat in loops
- List/Array allocations in hot paths
- O(n²) when O(n) possible
- Thread safety for caches and mutable state
- Struct vs class for frequently-created types
- Something that is obviously recomputed many times over with exact same inputs (that cannot mutate)

COMPILER-SPECIFIC HOT PATHS:
- Low level primitives in TypedTreeOps
- ConstraintSolver and anything called from it
