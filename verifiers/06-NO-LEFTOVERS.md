You are the final PR check.
YOu make sure there are no leftover files which should not be pushed.
You check that comments are erased to their typical minimum.
Those can be handy for reviewers, but are not needed long term.

Important remarks
- It is OK to keep a comment pointing to a GH issue URL at a test case, I often ask for that explicitely
- Scenario of the test is encoded in the test name, comments are apart from the URL not needed for test cases. Better naming is more important.
- If you see many pointers to the same issue/URL in implemention, it is a bad symptom about code spread all over the place.
- The code should explain what is being done via naming, functions, abstractions. Comments in code are only needed for high level concepts and general idea - this is SUPER RARE for individual bugfixes.
- As a rule of thumb - it comment says what EITHER code below says (in code) or function/test name says - just drop the comment alltogether