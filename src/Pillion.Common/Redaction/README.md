# Redaction: secret scanner and the redaction round trip

Design doc sections 12 (Secret scanner), 6 (Streaming, caching and resilience) and 15 (Redaction round trip).

## What is here

| Type | Role |
| --- | --- |
| `SecretScanner.Scan(text)` | Finds secrets. Returns non-overlapping `SecretMatch` (kind, start, length, severity) in document order. |
| `RedactionSession` | Reversible redaction for **one model request**: `Redact`, `Restore`, `FindUnresolved`, `Redactions`, `Dispose`. |
| `LogRedactor.Redact(text)` | One-way, stateless redaction for logs, the audit trail and index writes. |
| `SecretKind`, `SecretSeverity`, `SecretMatch`, `RedactionSummary` | Value types. |

## The one rule: redact at the model boundary, never at the process boundary

Pillion Host and the Visual Studio extension talk over a local named pipe. Nothing is redacted on that pipe: the
extension must send real file contents so that the user sees, approves and applies real edits, and the pipe never
leaves the machine.

Redaction happens in the **Model Gateway**, at the moment text is handed to a cloud model provider, and again
before anything is written to logs, the audit trail or the index. In practice:

1. The gateway creates a `RedactionSession` per outbound request and passes every message, file excerpt and tool
   result through `session.Redact(...)`.
2. The Context Inspector shows `session.Redactions.Describe()` (for example `3 redactions: aws-access-key, jwt`).
   It never shows originals.
3. Model output goes through `session.FindUnresolved(...)` and then `session.Restore(...)` **before** an edit is
   applied. An edit that contains a placeholder-shaped token the session did not issue is rejected: it would write
   `<redacted:...>` into the user's source.
4. The session is disposed when the request ends (originals are dropped from memory).
5. Local models (Ollama, LM Studio and similar) are not a cloud boundary and are not redacted, unless the user
   asks for it.
6. Logs, audit and index writes call `LogRedactor.Redact` and cannot restore. Do not put a `RedactionSession`
   in a log call.

If scanning throws `RegexMatchTimeoutException`, the text could not be verified. The gateway must not send it.
`LogRedactor` fails closed too: it returns `<redacted:scan-failed>` instead of the text.

## Placeholders

* `<redacted:kind>` when the request contains one distinct value of that kind, for example
  `<redacted:aws-access-key>`.
* `<redacted:kind#n>` once it contains more than one: `<redacted:aws-access-key#1>`, `<redacted:aws-access-key#2>`.
* The same original always maps to the same placeholder within a session. Text already sent to the model never
  changes: the first value of a kind that was issued unnumbered stays `<redacted:kind>` (and `#1` is accepted as an
  alias on the way back); later distinct values get `#2`, `#3`. If several distinct values of a kind first appear in
  the same `Redact` call they are numbered from `#1`.
* `Restore` replaces only placeholders this session issued and makes a single pass, so an original that looks like
  a placeholder is never expanded twice. `FindUnresolved` returns every other `<redacted:...>`-shaped token
  (including malformed ones such as `<redacted:not a token>`), distinct, in order of first appearance. An unnumbered
  placeholder is unresolved when the session issued numbered ones for that kind: it is ambiguous, so it is not
  guessed.
* Placeholders carry no session id (the format is fixed by the design). The same placeholder text can therefore mean
  different originals in different sessions. Never restore with the session of another request.
* `Redactions.Count` is the number of replacements (a secret used three times counts three); `DistinctCount` is the
  number of distinct values; `Kinds` is the list of kind ids in order of first appearance.
* Originals are held only in the session's dictionaries and are dropped on `Dispose`. .NET strings are immutable and
  cannot be zeroed, so this releases them to the garbage collector rather than overwriting them.
* Thread safety: a session serialises its operations with a lock.

## Patterns

Each match covers the **secret value only**, not the context: `Password=hunter2;` matches `hunter2`, and
`"apiKey": "..."` matches the characters between the quotes. Samples below show the shape; the tests build
fabricated values with the same shape at run time, and the repository deliberately contains no complete
secret-shaped literal (GitHub push protection would reject it).

| Kind id | Severity | Shape and context |
| --- | --- | --- |
| `aws-access-key` | high | `AKIA`, `ASIA`, `ABIA` or `ACCA` + 16 characters of `A-Z2-7`, not embedded in a longer alphanumeric run |
| `aws-secret-key` | high | 40 characters of `A-Za-z0-9/+` after `aws_secret_access_key`, `AWSSecretKey`, `SecretAccessKey` and similar, with `=` or `:` |
| `azure-storage-key` | high | 86 base64 characters + `==` after a name containing `storage` or `account` and ending in `key` (`storageAccountKey: ...`) |
| `azure-connection-string` | high | The value after `AccountKey=` or `SharedAccessKey=` (20 to 128 base64 characters, up to two `=`). The rest of the connection string (account name, endpoint) is left readable |
| `gcp-api-key` | medium | `AIza` + 35 characters of `A-Za-z0-9_-` |
| `gcp-service-account` | high | In a service account JSON file: the 40 hex characters of `"private_key_id"` and the whole `"private_key": "-----BEGIN ...-----\n...\n-----END ...-----\n"` string. PEM blocks that are not in that JSON shape are `private-key-block` |
| `github-token` | high | `ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_` + 36 or more alphanumerics; `github_pat_` + 22 alphanumerics + `_` + 59 alphanumerics (lengths accepted loosely: 20-40 and 40-100) |
| `slack-token` | high | `xoxb-`, `xoxp-`, `xoxa-`, `xoxr-`, `xoxs-` followed by two to seven hyphen separated alphanumeric groups, 20 or more characters in all |
| `slack-webhook` | medium | `https://hooks.slack.com/services/T.../B.../...` (also `workflows` and `triggers`); the whole URL, because the path is the secret |
| `stripe-key` | high / medium / low | `sk_live_`, `rk_live_` (high); `sk_test_`, `rk_test_` (medium); `pk_live_`, `pk_test_` (low) + 16 to 99 alphanumerics |
| `openai-key` | high | `sk-` + 32 or more alphanumerics, or `sk-proj-` / `sk-svcacct-` / `sk-admin-` + 32 or more of `A-Za-z0-9_-`; never matches `sk-ant-` |
| `anthropic-key` | high | `sk-ant-` + 32 or more of `A-Za-z0-9_-` (for example `sk-ant-api03-...`) |
| `jwt` | medium | `eyJ` + base64url, `.`, base64url (8+), `.`, base64url signature (8+). Unsigned (`alg: none`) tokens with an empty signature are not matched |
| `connection-string-password` | high | `Password=value` or `Pwd=value` with no spaces around `=` (unquoted value up to the next `;`, quote or whitespace; or `'quoted'`), at the start of a name or after `_` (so `DB_PASSWORD=...` in a `.env` matches); and `scheme://user:password@host`. Values starting with `$ % @ { < *` (variable and parameter references), containing `( ) [ ] ,`, fewer than 3 characters, dotted member accesses such as `settings.DbPassword`, and the allow-listed words below are ignored |
| `private-key-block` | high | `-----BEGIN [RSA \|EC \|DSA \|OPENSSH \|ENCRYPTED \|PGP ]PRIVATE KEY[ BLOCK]-----` through the matching `-----END` line, including line breaks of either kind and JSON-escaped `\n`. A block with no END line (truncated text) is redacted from the BEGIN line through the base64 lines that follow |
| `generic-high-entropy` | medium | See below |

### generic-high-entropy

A value of **32 or more characters** drawn from `A-Za-z0-9+/=_-` assigned to a name that **contains** `key`,
`secret`, `token`, `password`, `passwd`, `pwd` or `credential` (case-insensitive, up to 40 more name characters
before the separator), in these forms:

* `name = "..."`, `name: "..."`, `"name": "..."` (also single quotes and backticks, `:=` and `=>`, and a TypeScript
  type annotation such as `token: string = '...'`);
* `name=...` and `name: ...` unquoted (`.env` and YAML), ending at whitespace, a quote or punctuation;
* `<add key="name" value="..."/>` (either attribute order) and `<name>...</name>` elements.

A candidate is kept only if all of these hold:

1. it mixes letters and digits;
2. its Shannon entropy is **greater than 3.0 bits per character** (`SecretScanner.GenericEntropyThreshold`);
3. it is not a hyphenated GUID;
4. it is not phrase-like: splitting on `-` and `_` gives at least three segments of which 75% or more are plain
   words or numbers (`my-application-secret-store-name-v2`, `REPLACE_ME_WITH_YOUR_CLIENT_SECRET_VALUE_123`);
5. it is not allow-listed.

**Why 3.0.** The lowest-entropy credential shape we want to catch is a 32-character random hex string (an MD5-sized
API key). The measured entropy of 200,000 random 32-character hex strings has mean 3.61 bits and minimum 2.83;
only 17 of 200,000 (0.0085%) are at or below 3.0. A threshold of 3.5, the usual rule of thumb for base64, would
miss 18.6% of them; 3.25 would miss 0.8%. 40-character hex (SHA-1 sized) has mean 3.70 and a single value at 3.0 in
200,000. Random alphanumeric strings are far above (32 characters: mean 4.55, minimum 3.82 in 200,000; 88 base64url
characters: mean 5.40). So the threshold's job is to reject low-diversity filler such as `ab1ab1ab1...` or
`aaaa...1`, not to separate credentials from prose; prose, identifiers and file paths are handled by the character
class (no spaces, dots or colons), the digit requirement and the phrase-like rule. The trade is deliberate: a missed
secret leaks, a false positive only costs one `<redacted:...>` that is restored on the way back.

### Overlaps

When two patterns match overlapping text, the more specific wins: tier 0 whole blocks (`private-key-block`,
`gcp-service-account`), tier 1 self-identifying token formats (AWS access key, GitHub, Slack, Stripe, Anthropic,
OpenAI, Google API key, JWT), tier 2 formats that need context (Azure, AWS secret, passwords), tier 3
`generic-high-entropy`. Within a tier the longer match wins, then the earlier. So `token = "eyJ..."` is a `jwt`,
not generic, and the rest of the line stays readable.

## Allow-list (non-secrets that look like secrets)

Kept small on purpose. A candidate value is skipped when:

1. it is one of the exact public documentation values: `AKIAIOSFODNN7EXAMPLE`,
   `wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY` (AWS docs), the Azurite/storage-emulator `devstoreaccount1` key, and
   Stripe's documentation key `sk_test_4eC39HqLyjWDarjtT1zdp7dc`;
2. it ends in `EXAMPLE` or `EXAMPLEKEY` (the AWS documentation convention, e.g. `ASIAIOSFODNN7EXAMPLE`);
3. it starts with `<redacted:` (already one of our placeholders);
4. it is one repeated character (`0000...`, `xxxx...`), or for values of 16 or more characters is 60% or more one
   character (`AKIAXXXXXXXXXXXXXXXX`, `ghp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`);
5. it is exactly (case-insensitive) one of `password passwd pass pwd secret changeme change-me redacted null none`;
6. it contains one of the words `your insert replace placeholder changeme dummy fake redacted example sample` or a
   run of three or more `x`, as a whole word (not flanked by letters): `your-api-key-here`, `REPLACE_ME`, `/XXXXXXXX`.

## Known false positives

* `generic-high-entropy` fires on any 32+ character letters-and-digits value assigned to a credential-looking name
  that is not caught by the rules above. Examples: a CamelCase identifier with a digit
  (`tokenCacheFileName = "ProductionTokenCacheFileNameForTheApplicationV2"`), a keyboard-row string
  (`keyboardLayout = "qwertyuiop...1234567890"`), a long non-hyphenated build or asset id assigned to `...Key`. The
  cost is a placeholder that is restored on the way back.
* `connection-string-password` fires on `password=dbpass` in code (Python keyword arguments, `.env` style assignments of
  identifiers; values of 3 or more characters) because a one-word identifier and a short password look the same. Dotted member accesses and values
  containing `( ) [ ] ,` are excluded.
* Literal example text such as `Password=hunter2` in documentation or comments is redacted.
* Public identifiers in a credential-looking field (an OAuth client id stored as `clientKey = "..."` with 32+ hex
  characters) are redacted.

## Known false negatives

* Short passwords that are not in a connection string or URL: `"password": "hunter2"`, `DB_PASS=...`, `Password = "x"`
  with spaces around `=`, `Password="..."` with double quotes. Only `generic-high-entropy` (32+ characters) covers
  assignments, so short human-chosen passwords in JSON, YAML and code are **not** redacted. Treat the scanner as a
  safety net, not a guarantee.
* Secrets whose name has no credential keyword (`"auth": "..."`, `"bearer": "..."`) unless they have a recognised
  format (token prefixes, JWT, PEM, and so on).
* Bare 40-character AWS secret keys and bare 88-character Azure storage keys with no naming context (they are
  indistinguishable from SHA-1 and SHA-512 digests, which appear everywhere).
* GUID-shaped keys (some Azure subscription keys are GUIDs without hyphens, which are caught; with hyphens they are
  allowed through as identifiers).
* Azure SAS tokens (`sig=...`), unsigned JWTs, `xapp-`/`xoxe` Slack variants, OpenAI keys shorter than 32 characters,
  encrypted PEM files with `Proc-Type` headers that are truncated before the END line.
* Secrets split by string concatenation (`"AKIA" + "..."`) or encoded (base64 of a connection string).
* Text longer than one regex cap: a generic value over 8192 characters is cut at 8192.

## Other limits

* A placeholder-shaped string already present in outbound text is indistinguishable from one the session issued: if
  a file literally contains `<redacted:aws-access-key>` and a real AWS key, `Restore` puts the real key at both
  places. Such literals only appear when a file was saved from redacted output.
* `Restore` works on complete text. If model output is streamed, a placeholder can be split across chunks; the
  gateway has to buffer from `<redacted:` to `>` before calling `Restore` (not implemented here).
* Scanning cost is a handful of linear regex passes: about 15 ms steady state for 1 MB of ordinary code on a
  development machine (the first scans after process start take longer while the JIT warms up). Every regex uses
  bounded quantifiers or atomic groups so pathological input stays linear; a 2 second match timeout is the backstop.
