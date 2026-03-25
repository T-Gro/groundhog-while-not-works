Check that source-language types are faithfully represented in the target.

RUTHLESS ON:
- Untyped escape hatches (any, interface{}, void*) without justification
- Missing fields on structs vs source interfaces
- Wrong numeric types, lossy enum translations
- Ignoring source generics/parameterized types
- Optional/nullable handling mismatches
