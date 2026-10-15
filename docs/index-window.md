# The Index window

The Index window shows what Pillion has indexed from your repositories, lets you pause, rebuild and delete indexes, and lists
the files Pillion left out and why. It is part of the Context Engine (design document sections 5, 7 and 10; work item 2.15).
See also [pillionignore.md](pillionignore.md) for the rules that decide what is left out and [mentions.md](mentions.md) for
what the index feeds.

## Opening it

- **View > Other Windows > Pillion Index**, or **Tools > Pillion: Open Index**.
- Clicking the indexing indicator in the status bar (below) opens it.

The window, the status bar indicator and the Indexing options page are behind the `indexWindow` feature flag. With the flag off
the menu items are hidden, the status bar shows nothing, and the Indexing page says it is disabled by settings or policy. Pillion
Host indexes either way: that is the `indexing` flag (see the [feature flags in the README](../README.md#feature-flags)). The
extension reads the flag when Pillion Host becomes ready and when the Tools menu opens.

Until Pillion Host is ready the window says "Host not ready. The index loads when Pillion Host is ready." When the extension has
no status yet it says "No status yet".

## The status header

The top of the window summarises the whole host:

- **A sentence**, for example "Indexing: Embedding chunks, 30 of 40 files (75%), about 1 min remaining", "Indexing is paused:
  a build is running", "The index is up to date", "Indexing failed: ..." or "There is no index to build". The same sentence is
  the status bar tooltip and the text screen readers read.
- **A progress bar and counts** while a run is in progress: "{files done} of {files total} files, {chunks} chunks", with an
  estimate of the time left ("Less than a minute remaining", "About 12 min remaining", "About 1 h 20 min remaining"). The estimate
  appears once embedding has started.
- **Totals**: files in all roots, chunks, disk use and the number of secret-bearing files excluded.
- **Last error**, when a run failed (an unreadable key, an index written by a newer version, an embedding model that could not be
  reached). A failed index is not rebuilt on its own: use **Rebuild** or **Delete** on its root.
- **Last updated** time.

The phases, in order, are Idle (up to date), Discovering files, Hashing files, Parsing and chunking, Embedding chunks and
Storing; a run can also be Paused or Failed. The phase only moves forward within a run. Partial results are searchable from the
first stored batch, so `@file`, `@symbol` and chat retrieval start working while a first index is still building.

### Pause and resume

**Pause** stops background indexing until you press **Resume**; the host finishes the file or batch it is on first. The buttons
act on the whole host, not on one root. Indexing also pauses by itself, and the window says why:

| Reason shown | When |
| --- | --- |
| you paused it | The Pause button |
| a build is running | A Visual Studio build is in progress |
| the debugger is attached | The debugger is not in design mode |
| battery saver is on | Windows battery saver is on and `indexing.pauseOnBattery` is on (the default) |
| the memory limit was reached | The host's working set passed `indexing.maxMemoryMb` even after halving the embedding batch; it resumes below 90% of the limit |
| tests are running | Declared, but the extension does not report test runs yet, so a test run pauses indexing only through its build |
| disk space is low | Declared, but nothing raises it yet |

When several reasons hold, the window shows the first of: you, memory, build, tests, debugger, battery, low disk. The status is
read again whenever it changes; the host sends at most two updates a second, and the window applies them in place.

**Refresh** reads the status, the index folders and the excluded files again. The window does this once when it opens.

## The three tabs

### Roots

One row per repository root of the open solution or folder (the git top-level of each project, or the opened folder; a solution
that spans two repositories has two rows). A row shows:

- **Root**: the folder.
- **Role**: **Writer (this host owns the index)** or **Reader (another host owns the index)**. When two Visual Studio instances
  open the same repository, one host indexes and watches and the other only searches; if the owner goes away the other takes over.
  "Not open yet" appears before the index is opened.
- **Files indexed** "x of y", **chunks**, **disk use** and the **embedding model** (for example `bundled/bge-small-en-v1.5`).
- **Last full sweep**, or "Never".
- **Exclusions**: "{n} excluded, {m} secret-bearing".
- **Versions**: the chunker version and the index schema version.

Per root:

- **Rebuild** indexes the root again from scratch in the background (the stored files, chunks, vectors and symbols are cleared and
  the pipeline runs again). Progress arrives as status updates. A reader refuses with a message naming the other instance; rebuild
  it from the Visual Studio that owns it.
- **Delete** deletes the root's index folder from disk after asking. If the index is open for the current solution it is closed
  first, and it is built again from scratch the next time the solution or folder opens. It is refused while another running host
  owns the index.

### Excluded files

Choose a root, and the window lists what the last scan left out, **secret-bearing files first** (they carry a shield and the words
"Secret-bearing file" or "Secret-bearing folder", so colour is never the only signal), under the headings "Secret-bearing files
(never indexed)" and "Other exclusions". Each row has:

| Column | Meaning |
| --- | --- |
| Secret | The shield, for a secret-bearing file |
| Path | Relative to the root, with `/` separators |
| Kind | File or folder (secret-bearing or not) |
| Rule | Why: Secret-bearing file, Ignored by .gitignore, Ignored by .pillionignore, Built-in exclusion, Matches indexing.exclude, Generated file, Minified file, Lock file, Generated header, Binary file, Too large, Link or reparse point, Nested repository, Unreadable |
| Details | The rule that matched and where it is written, for example the `.gitignore` line, or the byte count against the 1 MB cap |

Controls: **Secret-bearing only** filters to the secret files; **Previous** and **Next** page through the list (200 entries a
page), with "Showing {a}-{b} of {n}"; **Copy path** copies the chosen entry; **Open .pillionignore** opens the root's
`.pillionignore` in the editor, creating it with a one-line comment when it does not exist. After you save it the host sees the
change and sweeps again; press **Refresh** to see the new list.

An excluded directory appears once, as a folder; its contents are not listed. A secret-bearing file is never opened, hashed or
stored by the indexer. It can still be pinned in chat with `@file` after a per-file approval (see [mentions.md](mentions.md)); an
approved file is never sent to a cloud model.

### Indexes

Every index folder on the machine (the retention view): `%LOCALAPPDATA%\PillionProgrammer\index\<index-id>`. The summary reads
"{n} index(es), {size} on disk, {k} stale". Each row shows the root, the embedding model, disk use, **last used**, whether the
**repository** still exists ("Root exists", "Root is gone (stale)", or "Root unknown (drive offline or no state file)") and the
**state**: "Open in this Visual Studio", "In use by another running host" or "Not in use".

**Delete** removes the folder after asking; it is not possible while another host uses it. Use it for stale indexes and for old
indexes left behind when you changed the embedding model (the model is part of an index's identity, so a different model builds a
new index beside the old one).

Stale indexes are not deleted automatically yet: the retention rule (a root that no longer exists, or an index unused for
`indexing.retentionDays`) is implemented in the host but not run on a schedule, so this tab is where you clean up. This is open
issue 4 of the [Gate 2 report](gate-2-report.md).

## The status bar indicator

The Visual Studio status bar shows a short indicator beside the Pillion host state while there is something to say, and nothing
when the index is idle:

| Text | Meaning |
| --- | --- |
| `↻ Indexing 42%` | A run is in progress and its fraction is known (`↻ Indexing` while it is not) |
| `‖ Index paused: build` | Paused; the reason is one word: you, build, tests, debugger, battery, memory, low disk |
| `⚠ Index error` | The last run failed; the tooltip has the error |

The tooltip is "Open the Pillion Index window." followed by the same sentence the window's header shows. Clicking opens the window.
The indicator updates at most twice a second and goes quiet while the host is away or the `indexWindow` flag is off.

## The Indexing options page

**Tools > Options > Pillion Programmer > Indexing.** The page is a view over the `indexing` section of Pillion Host's
`settings.json`; **Save** writes it and **Reload** reads it again.

| Setting (`settings.json`) | Default | What it does |
| --- | --- | --- |
| Exclude patterns (`indexing.exclude`) | none | One pattern per line in `.gitignore` syntax. Files that match are never indexed. Add more per repository with a `.pillionignore` file |
| Maximum memory for indexing (`indexing.maxMemoryMb`) | 1,536 | Whole megabytes from 256 to 65,536. Indexing pauses when the host reaches this limit, even after halving the embedding batch (see the pause table above) |
| Pause indexing on battery saver (`indexing.pauseOnBattery`) | on | Pauses while Windows battery saver is on |
| Retention (`indexing.retentionDays`) | 30 | Whole days from 0 to 3,650. An index not opened for this long, or whose repository folder no longer exists, is eligible for deletion; 0 turns the age rule off. The automatic sweep is not scheduled yet (see the Indexes tab) |

Below the settings the page shows the **active embedding model** (read only). It is chosen when the first index is built; the
bundled `bge-small-en-v1.5` is the default.

```json
{ "indexing": { "exclude": ["docs/generated/", "*.snap"], "maxMemoryMb": 1536, "pauseOnBattery": true, "retentionDays": 30 } }
```

A repository can also add exclusions for everyone who opens it with `exclude` in `.pillion/policy.json`; those patterns are combined
with the user's `indexing.exclude`. A policy can only add exclusions, never remove them.

## Keyboard and screen readers

Everything is reachable with the keyboard: Tab between controls, arrow keys in the lists, access keys on the buttons and tabs
(**P**ause, Res**u**me, Re**f**resh, **R**oots, **E**xcluded files, **I**ndexes, Re**b**uild, **D**elete, Pre**v**ious, **N**ext,
**C**opy path). Every control has an automation name, and state is never shown by colour alone: the phase and pause reason are
words, a secret-bearing file has a shield and the words, and a stale index says "Root is gone". The window follows Visual Studio's
theme colours, including the high-contrast themes.
