--- DEDUP VERIFIER (LLM) ---

TYPE: llm-review
GATE: soft (advisory)

YOUR FOCUS:
- Detect copy-paste between target packages (same logic, different names)
- Detect reimplementation of utilities that already exist in common/shared layers
- Detect structural duplication ("different but same shape" code)

CHECK ACROSS PACKAGES:
- Compare helper functions in this sprint's package against shared utilities
- Compare type definitions against existing types — are we redefining something?
- Compare error handling patterns — should they be unified?
- Compare tree/graph walking logic — should there be a shared walker?

WHAT TO REPORT:
- For each duplication found: the two locations, similarity %, and a suggestion
- Only flag duplications > 10 lines. Ignore trivial ones.

WHY THIS MATTERS FOR PORTING:
- LLMs love to generate fresh code rather than reuse existing code
- Over a long porting campaign, unchecked duplication leads to a mess
- The original source may have duplication too — this is a chance to improve
