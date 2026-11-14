**Risks.** What could go wrong when this repository is changed{{task_clause}}: code that many other files depend on
(the code map's top definitions), public API or file formats other people rely on, generated or vendored code,
migrations and persisted data, concurrency, security-sensitive code (authentication, input parsing, cryptography,
secrets), platform or version specifics, and gaps in the tests near the areas likely to change. Use the test
baseline: known-red and flaky tests, and what they mean for verifying a change. Rank the risks, most serious first,
and say for each how a change would avoid or detect it.
