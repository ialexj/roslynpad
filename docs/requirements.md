# RoslynPad — Requirements & Architecture Brainstorm

Status: draft / pre-implementation. Captures decisions made during initial design discussion.

## 1. Goal

Write Roslyn rewriters as plain C# classes deriving from `CSharpSyntaxRewriter`/`CSharpSyntaxWalker`, and apply
them at the push of a button to either a single file or a whole codebase, with a preview/diff step and
accept/reject before anything is written to disk.

## 2. Hosting strategy

Rejected: a custom-editor standalone app (LINQPad-style) and building a custom diff/preview UI — too much
UI investment for v1.

Chosen direction: keep the door open to **both a Visual Studio extension and a VS Code extension**, but start
with a **console app** as the first, common front-end.

### 2.1 Engine vs. CLI split

- **Core engine**: a plain class library with no UI dependencies. Takes one or more rewriter sources (`.cs`
  files compiled at runtime, or precompiled `.dll` assemblies loaded via reflection) + a target scope
  (file/project/solution) and an already-obtained `Microsoft.CodeAnalysis.Solution`/`Project`. It doesn't care
  how the `Workspace` was constructed.
- **Console app (CLI)**: thin wrapper. Parses arguments, builds an `MSBuildWorkspace` from the given
  path(s), calls the engine, and prints/writes results.
- **Future VS extension**: instead of building its own `MSBuildWorkspace` (risk of assembly/version conflicts
  with `devenv.exe`'s own hosted MSBuild/Roslyn), it should obtain the `Solution` from VS's already-loaded
  `VisualStudioWorkspace` (MEF export) and call the engine in-process directly — no need to spawn the CLI.
- **Future VS Code extension**: has no in-process way to call a .NET library, so it spawns the console app (or
  a small companion process) and communicates via CLI args / stdout.

This split means the CLI *is* the common core for now (every host can shell out to it), while the actual logic
lives in a referenced library so a tighter in-process VS integration is additive later, not a rewrite.

### 2.2 CLI shape (proposed)

Two subcommands, split so accept/reject maps onto "compute, then selectively write":

```
rewriter-cli diff  --rewriter MyRewriter.cs [--rewriter Second.cs ...] [--type Full.Name ...]
                    (--file X.cs | --project X.csproj | --solution X.sln)
                    [--include <glob>] [--exclude <glob>]
                    [--format json|unified]

rewriter-cli apply --rewriter MyRewriter.cs [--rewriter Second.cs ...] [--type Full.Name ...]
                    (--file X.cs | --project X.csproj | --solution X.sln)
                    [--only <path1,path2,...>]   # accepted subset from a prior diff/preview
```

`diff` never touches disk — it emits structured results (per file: path, old text, new text) that any
front-end can render. `apply` re-runs the same rewrite deterministically and writes only the accepted files.
Each invocation is stateless; the front-end holds no state between calls.

**Multiple rewriters**: `--rewriter` is repeatable, and order is significant — it defines an ordered pipeline
executed in a single engine call (`System.CommandLine`'s array-typed option naturally preserves the order
options were given on the command line and allows the same value more than once, e.g.
`--rewriter A.cs --rewriter B.cs --rewriter A.cs` is a valid three-stage pipeline). Running the same rewriter
file more than once in one pipeline is supported.

**Multiple rewriter classes in one file / explicit type selection**: `--type` is also repeatable. If omitted,
every `--rewriter` file expands into all the rewriter classes it declares, in declaration order (see 3.2). If
one or more `--type` values are passed, they **fully define the pipeline**: the ordered list of `--type`
values *is* the pipeline (each name resolved against the types compiled from all `--rewriter` files),
regardless of how many types those files would otherwise have expanded into — `--rewriter` order no longer
determines pipeline order/membership once `--type` is used. Repeating the same type name in `--type` runs it
multiple times, consistent with running the same rewriter file multiple times. If a `--type` value matches
more than one loaded type (e.g. the same simple name declared in two different `--rewriter` files), that is a
hard error — the user must disambiguate (e.g. with a fully-qualified name) rather than the engine guessing;
this fails the whole run with exit code `2` (see 3.3).

**Precompiled rewriters (`.dll`)**: `--rewriter` also accepts one or more precompiled assembly paths, loaded
via reflection instead of compiled from source. Because a compiled assembly has no meaningful "declaration
order" to fall back on and may contain unrelated types, **`--type` is mandatory for the whole invocation** as
soon as any `--rewriter` value is a `.dll` — the default declaration-order expansion only applies when every
`--rewriter` value is a `.cs` source file.

## 3. Rewriter execution model

- The rewriter class is not known at compile time: compiled/loaded dynamically at runtime (Roslyn scripting or
  compile-to-assembly + load, or a precompiled `.dll` loaded directly via reflection), then located via
  reflection (`CSharpSyntaxRewriter`/`CSharpSyntaxWalker` subclasses in the given source file or assembly).
- A source file may declare multiple rewriter classes. By default all of them run, in declaration order,
  each becoming its own pipeline stage (see 3.2); an explicit `--type` list overrides this and fully defines
  the pipeline instead. A precompiled `.dll` has no declaration-order fallback, so `--type` is mandatory
  whenever a `.dll` is passed.
- A **fresh rewriter instance is created per file** when running against a project/solution. This is the
  natural model for stateless, per-file semantic data and avoids leaking per-file fields across unrelated
  documents.
- Constructor parameters are resolved via a small convention-based DI: the harness maintains a set of
  available service instances for the run and matches constructor parameters by type. No forced base class
  required, though a convenience base class may be offered later.

### 3.1 Semantic model support

- A `SemanticModel` is scoped to a `(Compilation, SyntaxTree)` pair, not to a `Document` in the abstract — in
  practice obtained via `document.GetSemanticModelAsync()`.
- Constraint (accepted as expected behavior, not a bug): semantic queries (`GetSymbolInfo`, `GetTypeInfo`,
  `GetDeclaredSymbol`, ...) only work on nodes from the **original** tree. Once a rewriter returns a
  replacement node, that new node cannot be queried — all semantic lookups must happen on the incoming node
  before constructing its replacement.
- The harness injects the current document's `SemanticModel` into the rewriter's constructor if a matching
  parameter is present.
- Whole-project runs use a single, consistent snapshot: all semantic models for the run derive from the
  original solution's compilation. Rewriting file A does not change what file B's semantic model sees during
  the same run. Convergence across *separate* tool invocations (where a change made by a prior run should
  affect understanding of a later run) requires re-running the tool on the updated solution — not something
  the harness solves internally. (Within a single run's rewriter *pipeline*, this is handled — see 3.2.)
- This behaves identically whether the `Solution` came from `MSBuildWorkspace` (CLI) or `VisualStudioWorkspace`
  (VS) — same `Project`/`Document`/`Compilation`/`SemanticModel` API surface. Getting a semantic model per
  document is cheap; the project's `Compilation` is computed/cached once and per-file models are derived
  lazily.
- VS-specific: `Document.GetTextAsync()` reflects the live editor buffer (including unsaved edits). When
  applying accepted changes inside VS, use `Workspace.TryApplyChanges(newSolution)` rather than raw file I/O,
  so undo stack, dirty flags, and open editors update correctly.

### 3.2 Multi-rewriter pipelines

- A single engine invocation can run an **ordered list of rewriters** against each target file, not just one.
  Order is significant and explicitly captured by the host (see CLI shape above for how the CLI captures it).
- The final stage list is built one of two ways:
  - **Default (no `--type`)**: only possible when every `--rewriter` value is a `.cs` source file. Expand each
    `--rewriter` occurrence, in the order given, into the rewriter type(s) declared in that file, in
    declaration order. E.g. `--rewriter A.cs --rewriter B.cs` where `A.cs` declares two rewriter classes
    produces a three-stage pipeline: `A`'s first type, `A`'s second type, then `B`'s type.
  - **Explicit (`--type` passed, or any `--rewriter` is a `.dll`)**: the ordered list of `--type` values *is*
    the pipeline, each name resolved against the set of types compiled/loaded from all `--rewriter` files and
    assemblies — regardless of how many types those sources declare. Pipeline order and membership come
    entirely from `--type`'s order in this mode.
- Running the same rewriter file more than once in one pipeline is supported — each occurrence is an
  independent stage and gets its own fresh instance, per the "fresh instance per file" rule in section 3,
  applied per (file, stage) rather than just per file.
- Per document, the syntax tree flows between stages as a live in-memory `SyntaxNode` object — the engine
  updates the document via `Solution.WithDocumentSyntaxRoot`/`Document.WithSyntaxRoot` (passing the tree
  object), never by reparsing from text. This means `SyntaxAnnotation`s added by an earlier stage remain
  visible to later stages in the same run (see section 3.1's discussion of annotations vs. captures).
- **Compilation is only recomputed between stages when the next stage needs it.** Before constructing a
  stage's rewriter instance, the engine checks whether that rewriter's constructor requests a `SemanticModel`.
  If not, the engine skips updating the `Solution`/`Compilation` and passes the tree straight to that stage —
  cheaper, and correct since the stage isn't consuming semantic info anyway. If it does, the engine first
  updates the document's syntax root to the current tree (object, not text) and obtains a fresh
  `SemanticModel` from the recomputed `Compilation` before instantiating that stage.
- This logic lives entirely in the shared engine, so behavior is identical across hosts — **provided the host
  calls the engine once for the whole pipeline**, rather than chaining separate single-rewriter invocations
  itself.
- **Caveat**: invoking the CLI twice as separate OS processes (e.g. `apply` for rewriter A, then a separate
  `apply` for rewriter B against the resulting file) is not a pipeline in this sense — it's two independent
  runs with a text round-trip on disk in between, so annotations from the first invocation do not survive.
  Pipeline semantics (annotation preservation, conditional recompilation) only apply within a single engine
  call over the full ordered rewriter list.

### 3.3 Change detection & convergence

- **Change detection**: after a file's pipeline pass completes, the engine compares the *normalized* form of
  the pre-run and post-run `SyntaxTree` (e.g. both sides run through `NormalizeWhitespace()` before
  comparing) to decide whether anything meaningful changed, rather than relying on raw text/`IsEquivalentTo`
  equality alone. This avoids false positives from incidental formatting/trivia differences that can appear
  when nodes are reconstructed without any real edit (a known Roslyn artifact). This is a distinct signal from
  the old-text/new-text diff used for preview/apply — the text written to disk (or shown in a diff) is always
  the rewriter's exact, unnormalized output; normalization is only used internally to decide "did this
  actually change anything."
- A run-level `HasAnyChanges` flag aggregates this per-file signal across every target file in the run.
- **`--detailed-return`** (CLI): distinguishes outcomes with granular exit codes instead of the default
  coarse 0/1 (see below). Mirrors the convention used by tools like `grep` (0 = match/success, 1 = ran fine
  but nothing to report, 2+ = error), keeping all codes POSIX-friendly (0–255, no negative/truncation
  concerns) while still letting callers treat `>= 2` as "hard error":
  - `0` — success, and at least one change was made (`HasAnyChanges` is true).
  - `1` — success, but no changes were made (`HasAnyChanges` is false). Not an error.
  - `2` — a compilation or rewriter type-load error occurred, **including an ambiguous `--type` name**
    matching more than one loaded type (error details written to `stderr`).
  - `3` — `--repeat-until-stable` hit `--max-iterations` without the pipeline converging.
- **Without `--detailed-return`**: exit code is always `0` for a successful run, regardless of whether
  changes were made, and `1` for any failure (collapses the `2`/`3` cases above into the same `1`).
- **`--repeat-until-stable`** (CLI): re-runs the full configured rewriter pipeline against the in-memory
  result of the previous iteration (not re-read from disk), looping until an entire iteration produces zero
  changes across all target files. Applies to both `diff` (computed entirely in memory — the emitted diff is
  original-vs-final-stable text, nothing is written to disk) and `apply` (each iteration's result feeds the
  next; only the final stabilized state is written).
- **`--max-iterations <n>`** (CLI): caps the number of iterations `--repeat-until-stable` will run. If the cap
  is reached without an iteration producing zero changes, the run stops and reports the pipeline as
  non-convergent (distinct from a normal stable/unstable result) rather than looping forever. Only meaningful
  alongside `--repeat-until-stable`. Defaults to `10`.
- This looping/change-detection logic lives in the shared engine, not the CLI, so `--repeat-until-stable` and
  `--detailed-return` semantics stay consistent across future hosts.

## 4. Captures — cross-run memory for rewriters

Rewriters are otherwise stateless per file/per run. A `Captures` facility lets a rewriter record
interesting nodes during a run and re-access them (validated) on subsequent runs.

### 4.1 Injected service

```csharp
public interface ICaptureSink
{
    void Add(SyntaxNode node, string? label = null);
}

public interface ICaptureStore : ICaptureSink
{
    IReadOnlyList<CaptureEntry> Previous { get; } // pre-validated by the harness before injection
}
```

- **Lifetime: run-scoped, not file-scoped.** One `ICaptureStore` instance is created per run and passed to
  every per-file rewriter instantiation, so captures accumulate across the whole project/solution pass.
- If the per-document loop is ever parallelized, the concrete sink implementation must be thread-safe.

### 4.2 Persisted shape

A `SyntaxNode` cannot be serialized across process runs, so a capture snapshots enough to relocate the
equivalent node later:

```csharp
public sealed record CaptureEntry(
    string FilePath,          // relative to project/solution root
    string FileContentHash,   // hash of the file's text at capture time
    TextSpan Span,            // character offset span
    LinePositionSpan LineSpan,// line/col, for JSON readability + UI display
    string Snippet,           // node's text, for human review without opening the file
    string? Label);
```

- Before trusting a loaded `CaptureEntry` on a later run, re-hash the current file text and compare to
  `FileContentHash`. If it matches, `root.FindNode(span)` reliably relocates the same node (and a fresh
  `SemanticModel` can be obtained from the current compilation). If it doesn't match, the entry is stale and
  should be excluded/flagged rather than silently pointing at the wrong code.
- A captured span refers to the *input* text for that run (before that run's own rewrite touches the file),
  matching the "before" side of the diff preview.

### 4.3 Per-host persistence/rendering

- **Console**: `--captures-out path.json` writes accumulated entries after a run; `--captures-in path.json`
  loads and hash-validates prior entries before a run, exposing the valid ones as `Previous`.
- **VS**: a tool window (list/tree) of captures with navigate-to-span, similar to Find All References/Error
  List, backed by the same JSON format.
- **VS Code**: a custom `TreeView` backed by the same JSON, click-to-reveal via `showTextDocument` + range
  reveal.

All three hosts share the same `CaptureEntry` format and hash-validation logic in the engine; only
persistence/display glue is host-specific.

## 5. Open questions / future work

- Confirm whether VS 2026's newer extensibility SDK (out-of-process model) still supports the classic
  MEF-based `VisualStudioWorkspace` access pattern assumed above.
- Decide on the rewriter/capture DI convention details (attribute-based vs. type-matching-only, ambiguous
  constructor resolution).
- Decide default locations for `--captures-out`/`--captures-in` files (e.g. `.roslynpad/captures.json`).
- VS Code preview UX: multi-file diff/bulk-edit vs. virtual SCM provider for accept/reject — not yet decided,
  deferred until the VS Code host is actually built.
- How a user selects/orders multiple rewriters to run as a pipeline in the VS/VS Code hosts is TBD — likely
  some custom UI (e.g. a reorderable list); deferred until those hosts are built. This includes how a UI would
  let a user narrow a file to one specific type vs. running all declared types.
- Lifetime/unloading of loaded `.dll` rewriter assemblies in long-lived hosts (VS/VS Code extension processes
  that may run many rewrites over a session) vs. a one-shot CLI process where it doesn't matter — likely reuse
  the existing collectible `AssemblyLoadContext` approach, but explicit unload timing is undecided.
- Iteration cap / safety valve for `--repeat-until-stable` when a pipeline doesn't converge: resolved by
  `--max-iterations` (3.3), defaulting to `10`.
