# Historical Pikafish source

The `upstream` directory retains unmodified source from the official
`Pikafish-2026-09-06` tag:
https://github.com/official-pikafish/Pikafish/tree/Pikafish-2026-09-06

The application no longer builds or ships a standalone native rules helper.
Candidate generation and repetition/check/chase adjudication are implemented
in managed code in `Engine/PikafishRulesClient.cs`; normal Pikafish engine
search continues to use the engine's own rule implementation.

The retained upstream source is not part of the application build or runtime
distribution. Its license and author notices remain in `upstream/Copying.txt`
and `upstream/AUTHORS`.
