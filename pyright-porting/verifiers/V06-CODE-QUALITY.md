--- CODE QUALITY VERIFIER (LLM) ---

TYPE: llm-review
GATE: soft (quality gate, can retry)

RUTHLESSLY check for code reuse and proper architecture.

YOUR FOCUS (BE RUTHLESS):
- Check size of target output compared to source input. 3x larger? Problem.
- Check if the target code is idiomatic (not "source language written in target syntax")
- Find common abstractions — don't reimplement helpers that exist in earlier layers
- Error handling follows target language conventions
- Naming follows target language conventions
- Package/module boundaries respected per the layer architecture
- No God functions — break up oversized functions

ANTI-PATTERNS TO FLAG:
- Untyped escape hatches used as a crutch
- Giant switch/match statements that should be method dispatch
- Copy-pasted code blocks (should be extracted to helper)
- Exported symbols that should be unexported/private
- Ignored errors / missing error checks
- Bare string comparisons where enums/constants should be used
- TODO/FIXME/HACK comments left behind
