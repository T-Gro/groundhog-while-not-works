--- TYPE FIDELITY VERIFIER ---

YOUR FOCUS:
- TypeScript types are faithfully represented in Go
- No lossy translations (e.g., union types collapsed to `interface{}`)
- Nullable types use pointer or `(value, ok)` patterns appropriately
- Generic TypeScript types use Go generics (Go 1.18+) where applicable

PORTING-SPECIFIC CHECKS:
- For each TypeScript `interface`, verify a Go `interface` or struct exists with matching fields/methods
- For each TypeScript `type` alias, verify a Go type alias or named type exists
- For each TypeScript `enum`, verify a Go `const` block with `iota` or explicit values exists
- Union types: use a sum-type pattern (interface with unexported marker method) or discriminated struct
- Optional fields (`field?: Type`): use pointer types or `(value, bool)` accessors
- Map types: verify key/value types match
- Array types: verify element types match

RUTHLESS ON:
- `interface{}` / `any` usage — must be justified by truly dynamic TypeScript code
- Missing fields on structs compared to TypeScript interfaces
- Wrong numeric types (e.g., using `int` where TypeScript uses fractional `number`)
- Ignoring TypeScript generics instead of using Go generics
