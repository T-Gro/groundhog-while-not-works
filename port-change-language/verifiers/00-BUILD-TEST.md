Build the code, run the tests, assess the results.

1. Read .github/copilot-instructions.md for build and test commands.
2. Run the build. If it fails, report the errors.
3. Run the tests. Record results.
4. Check for regressions: are any previously-passing tests now failing?
5. Check for improvement: are more tests passing than before this sprint?
6. Check test count has not decreased (no deleting tests to fake progress).

If build fails or tests regressed: VERIFY_FAILED with exact errors.
If tests improved or held steady with no regressions: VERIFY_PASSED.
