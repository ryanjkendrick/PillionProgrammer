# Contributing to Pillion Programmer

Thanks for helping. A few rules keep the history and the build healthy.

## Branches and pull requests

- One branch per work item, named after it, for example `phase0/scaffold-and-contracts`.
- Open a pull request to `main`. Draft pull requests are welcome for early feedback.
- `main` must always build and pass its tests; CI runs on every pull request.

## Tests are required

- Every change that adds or changes behaviour comes with tests in the matching `tests/` project.
- Before pushing, build and test locally (see the README):

  ```
  msbuild PillionProgrammer.sln /restore /p:Configuration=Release /m
  dotnet test PillionProgrammer.NoVsix.slnf -c Release
  ```

- Warnings are errors. Fix the warning rather than suppressing it; if a suppression is genuinely needed, scope it
  as narrowly as possible and explain why in a comment.

## Contract changes

`src/Pillion.Contracts` is shared by the extension and the host. Read its README before changing it: adding a
method or appending a DTO field is a minor version bump, anything else is a major bump, and the contract tests must
be updated in the same commit.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/) style subjects: `<type>: <summary>` in the
imperative, 72 characters or fewer. Use a scope or area as the type where it is clearer, for example:

```
feat: add inline completion provider
fix: handle host restart during chat
contracts: add IndexStatus.LastError
scaffold: add solution filter
test: cover handshake version mismatch
ci: upload test results
docs: describe the approval tiers
```

## Code style

`.editorconfig` defines the conventions (file-scoped namespaces, four-space indents, `using` directives outside
the namespace with `System` first) and the build enforces them. Every public type and member needs an XML doc
comment.

## Package versions

All package versions live in `Directory.Packages.props`. The Visual Studio SDK, StreamJsonRpc and MessagePack
versions are tied to what Visual Studio 17.14 ships; do not bump them without reading the comment there.
