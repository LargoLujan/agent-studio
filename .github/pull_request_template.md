## Summary

<!-- What changes and why, in a few sentences. -->

Closes #

## How it was tested

<!-- Commands you ran and their results, for example:
     dotnet build AgentStudio.slnx -c Release  ->  0 warnings
     dotnet test AgentStudio.slnx -c Release --no-build -- --filter-not-trait "Category=LocalTools" ...  ->  N passed
     Plus any manual check in the app. -->

## Checklist

- [ ] `dotnet build AgentStudio.slnx` finishes with 0 warnings.
- [ ] Tests pass locally (at least the CI filter), and new behaviour has tests.
- [ ] An ADR or an ADR addendum is included if a recorded decision changed or a new one was made.
- [ ] Documentation is updated (README or `docs/`) where behaviour changed.
- [ ] No secrets, tokens, credentials or personal paths in code, fixtures, logs or the journal.
- [ ] Layering rules hold: modules only depend on `Application`, and `Desktop` composes (ADR-0002).
