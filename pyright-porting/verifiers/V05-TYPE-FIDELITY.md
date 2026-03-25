--- TYPE/STRUCTURE FIDELITY VERIFIER (LLM) ---

TYPE: llm-review
GATE: soft (quality gate, can retry)

YOUR FOCUS:
- Source language types/structures are faithfully represented in the target language
- No lossy translations (rich types collapsed to untyped containers)
- Nullable/optional handling follows target language idioms
- Generic/parametric types preserved where the target language supports them

CHECKS:
- For each source interface/class/type, verify a target equivalent exists
- For each source enum, verify equivalent constants exist
- Union/sum types use the target language's idiomatic pattern
- Optional fields use the target language's nullable idiom
- Collection types match (arrays, maps, sets)

RUTHLESS ON:
- Untyped escape hatches (any, object, interface{}, void*, etc.)
- Missing fields on structures compared to source
- Wrong numeric types
- Ignoring source generics
- Lossy enum translations
