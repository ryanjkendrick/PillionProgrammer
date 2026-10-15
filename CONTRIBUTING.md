# Contributing to Pillion Programmer

Thanks for helping. A few rules keep the history and the build healthy.

## Branches and pull requests

We follow [Gitflow](https://nvie.com/posts/a-successful-git-branching-model/):

- `main` holds released versions only; every commit on it is a tagged release.
- `develop` is the integration branch and the default branch; it must always build and pass its tests.
- One `feature/<slug>` branch per work item, branched from `develop`, merged back with a pull request to `develop`
  once CI is green and the lead has reviewed it. Merge with a merge commit (no squash, no rebase) so the branch
  history and the stacked pull requests stay traceable.
- `release/<version>` branches off `develop` to stabilise a release; only fixes land there, and it merges to both
  `main` (tagged) and `develop`.
- `hotfix/<version>` branches off `main` for an urgent fix to a released version; it merges to both `main` and
  `develop`.
- Draft pull requests are welcome for early feedback.

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

## Model files

Model binaries are never committed. `src/Pillion.ModelGateway/Models/models.manifest.json` lists every embedding model
the host may load, with a pinned URL, SHA-256 and size for each file. The first build downloads the bundled model
(`bge-small-en-v1.5`, about 34 MB, from huggingface.co) into
`%USERPROFILE%\.nuget\packages\pillion.models\` and copies it into the output under `models\`; later builds, clean
builds and other worktrees reuse that cache and need no network. A file whose hash or size differs from the manifest
fails the build and is deleted. To build somewhere that cannot reach huggingface.co, fill the cache from another
machine, point `-p:PillionModelCache=<folder>` at a copy, or pass `-p:PillionSkipModelDownload=true` (the build warns
and the bundled model is missing from the output, so the `Category=Model` tests fail).

To add or update a model, change the manifest: use a `resolve/<commit>/` URL so the bytes cannot change, take the
SHA-256 and size from the Hugging Face API (`/api/models/<repo>?blobs=true`) or from the downloaded file, record the
licence of the weights and of the export in `THIRD-PARTY-NOTICES.txt`, and keep the catalog's `embedding` entry for the
model in step (a test checks both).

Tests that run a model are marked `[Trait("Category", "Model")]`: they use the bundled model in the build output and
run with the rest of `--filter "Category!=Live"`. Tests of the models downloaded on request are `Live`, because they
download about 280 MB into `%TEMP%\pillion-model-tests` the first time.
