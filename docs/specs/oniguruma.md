# Oniguruma contract v1

`Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1` is Oniguruma 6.9.10 (tag v6.9.10, commit
`4ef89209a239c1aea328cf13c05a2807e5c146d1`) with `ONIG_SYNTAX_ONIGURUMA` (`ONIG_SYNTAX_DEFAULT`, the Ruby syntax of
doc/RE) over `ONIG_ENCODING_UTF16_LE` subjects: the regex dialect of TextMate grammars as Onigwrap (TextMateSharp) runs
them. vscode-oniguruma compiles the same patterns with `ONIG_ENCODING_UTF8` over UTF-8 subjects; the one
encoding-dependent result grammars observe, a nested negated class holding U+0000, follows UTF-8 (below). Data identity `Oniguruma-6.9.10-Unicode16.0.0-4ef89209`: Oniguruma's own generated
property, case-fold and Grapheme_Cluster_Break tables, not .NET's or GregsEx's ECMAScript tables.

## Profiles and API

| Preset | Options |
| --- | --- |
| `RegexProfiles.Oniguruma` | `ONIG_OPTION_NONE`: with a named group, unnamed groups do not capture and numbered references are errors. |
| `RegexProfiles.OnigurumaTextMate` | `ONIG_OPTION_CAPTURE_GROUP`, as TextMate hosts compile grammar patterns: every group captures, numbered left to right. |

`OnigurumaOptions` also admits `IgnoreCase`, `Multiline` (dot matches newline, Oniguruma's `(?m)`), `Extended` and
`DontCaptureGroup`. `OnigurumaPattern.Parse`/`TryParse` produce an immutable pattern; `OnigurumaWorkspace`
(`IsMatch`, `Find`, `FindCaptures` from a start offset over `ReadOnlySpan<char>`) and `OnigurumaScanner` own the
mutable backtracking storage. `OnigurumaScanner` is TextMate's multi-pattern search (vscode-oniguruma's
`findNextMatchSync`, Onigwrap's `OnigScanner`): the earliest match start wins, the first pattern on a tie, the search
stops at a match at the offset, and for string-backed input each pattern reuses its last result for the same string
while it is still valid (Onigwrap's `OnigRegExp` cache). Warm searches allocate nothing; captures land in caller or
scanner buffers.

Offsets are UTF-16 code units. `startAt` bounds the match start only: look-behind, `\A`, `^` and `\b` see the whole
input, and `\G` matches at `startAt`, as `onig_search` with `start` inside the string does.

## Syntax inventory (OG-001)

Admitted: literals and escapes (`\t \n \r \f \v \a \e`, `\cx`, `\C-x`, `\M-x`, `\M-\C-x`, `\uHHHH`, `\x{H...}` with
space-separated sequences, `\o{O...}`, and encoded-byte `\xHH`/octal escapes assembled into UTF-16LE units, an
incomplete character being "too short multibyte code string"); `.`, `\O`, `\N`, `\R`, `\X`; `\w \W \s \S \d \D \h \H`;
`\p{..}`, `\p{^..}`, `\P{..}` with Oniguruma's 886 property names (ASCII, case-insensitive, ignoring space, `-` and
`_`); bracket classes with ranges, POSIX brackets (`[:alpha:]`, `[:^alpha:]`), nested classes, `&&` intersection and
negation; quantifiers `* + ? {n} {n,} {,m} {n,m}`, reluctant `?`, possessive `*+ ++ ?+`, `{n,m}` with n > m as
possessive, `{n}?` as `(?:x{n})?`; anchors `^ $ \A \z \Z \G \b \B \y \Y \K`; groups `( )`, `(?: )`, `(?<name> )`,
`(?'name' )`, `(?> )`, `(?= )`, `(?! )`, `(?<= )`, `(?<! )` (variable length), `(?#...)`; options
`(?imxWDSP-imxWDSP)` isolated (to the end of the enclosing group, alternatives included) and scoped, `(?y{g})`;
backreferences `\n`, `\k<n>`, `\k<-n>`, `\k<+n>`, `\k<name>` (the last group of a name first), with ignore-case;
subexpression calls `\g<name>`, `\g<n>`, `\g<0>`, `\g<-n>`, `\g<+n>`, recursion included; conditionals `(?(n)..|..)`,
`(?(<name>)..|..)` and `(?(expression)..|..)`; the absent repeater `(?~absent)`.

Rejected as Oniguruma rejects them (OG-002): a quantifier on an anchor or look-around (also inside `(?:)`), a class as
a range endpoint (`[a-\w]`, `[\w-a]`), empty ranges, unknown properties and POSIX brackets, undefined names and
group numbers, left-most recursion ("never ending recursion"), lone surrogates in the pattern and surrogate code
points outside classes, group names starting with a digit or a non-word character, and in a look-behind (Oniguruma's
`check_node_in_look_behind`) a look-ahead, `\z`, `\Z`, a negative look-behind inside a positive one, or a capture
inside a negative one. Nested greedy `?`, `*` and `+` reduce as Oniguruma reduces them (`a**` is `a*`); other stacked
quantifiers count toward the nesting limit.

Unsupported, with an `UnsupportedFeature` diagnostic (OG-003): callouts (`(?{..})`, `(*name)`), absent expressions and
stoppers (`(?~|..|..)`, `(?~|..)`), backreferences with a recursion level (`\k<n+level>`), text-segment word mode
`(?y{w})`, whole options `(?C)`, `(?I)`, `(?L)`. None occurs in tm-grammars 1.32.22.

## Semantics (OG-004)

- `^` matches at the start or after a newline but not at the end after a final newline; `$` at the end or before any
  newline; `\Z` at the end or before a final newline. Only `\n` is a newline; dot excludes it unless `(?m)`.
- `\w`, `\d`, `\s`, POSIX brackets and `\b` are Unicode (Oniguruma's tables) unless `(?W)`, `(?D)`, `(?S)`, `(?P)`.
- Ignore-case uses Oniguruma's simple folds and its folds to several code points: `(?i)ss` matches `ß`, `(?i)ß`
  matches `ss`, and an outermost non-negated class holding such a character also matches its fold string (also in a
  look-behind). As `expand_case_fold_string` does, a run of ignore-case characters, through plain `(?:...)` groups and
  `{1}`, is read left to right, the longest fold taken where it starts (`(?i)sss` matches `ßs`, not `sß`); look-behind
  strings keep single code point folding. A bracket class is folded once, as a whole, before its negation; nested
  classes are not folded on their own.
- A nested negated class contributes its true complement, as UTF-8 Oniguruma (vscode-oniguruma) computes it:
  `[a[^\x{00}-\x{7F}]]` matches `a` and non-ASCII characters, not a space. This deliberately departs from 6.9.10 over
  UTF-16LE, where `not_code_range_buf` underflows (`from - 1` for a range starting at U+0000) and such a class
  contributes every code point, which breaks grammars written against VS Code (CSS and Less identifiers
  `[[-\w][^\x{00}-\x{7F}]]`). The oracle carries the one-line Oniguruma fix (`if (from > pre)`).
- Captures are final values; an iteration that fails restores them. Captures set inside a subexpression call stay
  set after it returns, and a call saves and restores the open start of the groups it re-enters.
- A repeat whose body matched empty stops, unless the iteration changed a group that a backreference outside the body
  refers to (Oniguruma's `EMPTY_CHECK_END_MEMST`).
- A look-behind body cannot consume past the look-behind position (Oniguruma's right range); its anchors still see
  the whole input. Its candidate starts are character heads, nearest first. Alternatives of different lengths are
  divided as Oniguruma divides them; equal-length alternatives, and a positive look-ahead's, stay one atomic
  look-around.
- `\K` beyond the match end (inside a look-ahead) is clamped to it.
- As Oniguruma's `tune_next` rewrites the tree, a greedy `x*` or `x+` that is the body of a capture group (through
  nested captures) is possessive when the node after the group cannot start with `x`'s head literal
  (`is_exclusive` under UTF-16: strings and classes compare, two classes never exclude each other, `\w` excludes
  only `\W`; a character with case variants is a class of them). For a called group that holds for the calls
  too: `(?<n>\d+)\.\g<n>\d` does not match `12.345`.
- `(?~X)` is `(?:(?!X)\O)*`: the longest range holding no start of a match of `X`, then shorter.
- UTF-16 decoding follows `ONIG_ENCODING_UTF16_LE`: a high surrogate always starts a two-unit character (whatever
  follows), any other unit is a character; the character before a low surrogate starts one unit earlier. A high
  surrogate at the end is incomplete: only negated classes (`[^..]`, `\S`, `\D`, `\H`, `\W`, `\P{..}`) take it, as
  one unit. Two boundaries on such malformed text: Oniguruma's `\W` takes that last unit as two (an end past the
  input), and a high surrogate followed by another high surrogate decodes to a character negated property classes
  accept natively; GregsEx takes one unit, and decodes the pair arithmetically.
- `\X` is `(?>\O(?:\Y\O)*)` and `\y`/`\Y` are UAX #29 extended grapheme cluster boundaries (Unicode 16.0).

## Limits and failures (OG-005)

`RegexCompileLimits` bound every compilation resource (the TextMate adapter in TextMateSharp passes Oniguruma-sized
limits: grammars hold patterns of 282k units): pattern units, nesting, groups, instruction slots (checked while they
are emitted), class intervals across the program's sets (`ClassLimitExceeded`), compile work (pattern units, parse
nodes, class intervals built and the instruction slots of every follow pass), peak compile memory (parse tree, class
intervals, the live pass's code) and the retained plan's bytes (`PlanLimitExceeded`). Repeats nest counters rather
than copies of nested repeats, so a pattern compiles in time and space linear in its length. A tree deeper than a
thread pool stack holds (a raised nesting limit) compiles on a thread with a stack sized for it, up to 1 GiB; deeper
is `NestingLimitExceeded`, never a stack overflow.

`RegexMatchLimits.MaxWork` bounds a whole search, every start position included. Work counts what can repeat
without consuming input (backtracks, loop iterations and backward jumps, calls, look-around entries and start
positions) and the units runs consume; code between two of these is straight-line, so time is bounded by work times
the program's length. `MaxWorkspaceBytes` bounds the stacks. Either ends a search with `LimitExceeded`. Oniguruma's
retry limit (default 10,000,000) counts backtracks per start position instead and its per-search limit is off by
default, so where native exceeds its retry limit from one start, a later start may exhaust GregsEx's shared budget
although its own native attempt completes; the work count is not the retry count, so a search near either limit may
fail in one engine and not the other. `RegexMatchOptions` adds a cancellation token and a timeout, polled every 4,096
work units and before any result, even an immediate one (`Cancelled`, `TimedOut`; Oniguruma's optional
`onig_set_time_limit`), and lower limits; overrides cannot raise the pattern's ceilings. `OnigurumaScanner`'s options
overloads apply the timeout and cancellation to the whole call and the limits to each pattern's search; a search
that runs out of work counts as no match, as Onigwrap reports the retry-limit error, and nothing stopped is cached.
As at Oniguruma's `SubexpCallMaxNestLevel` (20), a call nested 19 deep fails that path and the search goes on.

A search that has done 1,024 work units starts a failure memo when what follows each backtrack resume depends only
on its instruction and position: no look-arounds, atomic groups or possessive loops, conditions, absent operators,
backreferences or calls, and every counted loop unbounded with a minimum of at most one and a body that cannot match
empty. Captures then only shape the result, so a resume (or the preferred branch of an alternative or loop) at a
pair already tried is skipped. The memo holds one bit per resume point and position, at most 2^25 bits within
`MaxWorkspaceBytes`, and lasts for the search's attempts. Results are the plain backtracker's, but ambiguous
alternations under stars such as `(w|ww)*(w|ww)*\(\(` take polynomial rather than exponential work, so some searches
complete where native Oniguruma reaches its retry limit.

## Implementation (OG-006)

A dedicated backtracking machine over compact instructions (`Flavors/Oniguruma`): not the ECMAScript or PCRE2
executors, and no other regex engine. Register writes are undone through a log per choice frame; atomic groups and
look-arounds cut to barrier frames. Per pattern the compiler derives search prefilters that preserve results: start
anchors, a required literal or a required unit set (one of which every match contains, alternatives united), the
first units of a match (look-aheads included), a leading look-behind's preceding units, a literal prefix, and the
minimum length; no start after the last occurrence of the required literal or units is tried. Matching uses
follow sets: a greedy run whose follow cannot start with one of its units becomes possessive, and runs give units
back only where the follow can start, found with one vectorized search. A called group's follow is the union of
those at its definition and at each call, computed to a fixpoint (at most four extra compile passes, else unknown); alternatives skip branches whose first units
exclude the next unit; a failed attempt that started with an unbounded run resumes after the run when no
backreference or condition can depend on where the run started; literal and single-unit look-arounds are table and set tests. NativeAOT compatible: no runtime code generation.

### Compiled kernels

`OnigurumaPattern.Compile()` and `[GeneratedPattern(pattern, OnigurumaOptions)]` on a `static partial OnigurumaPattern`
method lower the analysed parse tree (renumbered and possessified as the compiler sees it) to the shared compiled
kernel ([compilation.md](compilation.md)), with the interpreter's required-literal/required-units, prefix and
minimum-length prefilters. The kernel models subjects without surrogates: a search over a subject holding one, a
search with `RegexMatchOptions`, an exhausted kernel step budget and abandoned backtracking re-enter the interpreter
unchanged, so results, captures, limits, cancellation and timeouts are the interpreter's. Lowered: literals and
classes (ignore-case as the parser resolves it, folds to several code points included), dot, anchors (`\b` and `\B`
over Oniguruma's word set), alternation, greedy/lazy/possessive repeats, capture and atomic groups, look-ahead,
look-behind (candidate starts nearest first, the body limited to and ending at the look-behind position, a positive
look-behind over alternatives of different lengths divided as the compiler divides it) and `\G`. Left to the
interpreter: backreferences, subexpression calls, conditionals, the absent operator, `\K`, text-segment boundaries,
non-BMP literals, loops over a nullable body, and patterns beyond the lowering's node and class bounds.
`OnigurumaScanner` runs each compiled pattern's kernel under the same rules. A source generator that cannot use
`[GeneratedPattern]` obtains the same kernel source from `GregsEx.SourceGeneration.KernelSource.TryEmit` in the
generator assembly and binds it with `WithGeneratedKernels` ([compilation.md](compilation.md)).

## Conformance evidence (OG-007)

[`test/corpora/oniguruma`](../../test/corpora/oniguruma/README.md) holds native observations recorded with an
oracle built from Oniguruma's source at tag v6.9.10 with the nested negated class fix above ([`tools/oracles/oniguruma`](../../tools/oracles/oniguruma/README.md)):
hand-written edge cases and every pattern of the shellscript, typescript, javascript, html, css, json, yaml and csharp
grammars of tm-grammars 1.32.22 over lines of the shikijs/textmate-grammars-themes samples. Scanner selection (earliest
start, first pattern on a tie, stopping at a match at the offset, the per-string result cache, `\G` patterns not
cached) has unit tests. The checked-in `oracle differential` compares GregsEx with that oracle over all 15,034
distinct patterns of the 260 tm-grammars 1.32.22 grammars and every line of the 238 samples: on 2026-10-24 it agreed on
the validity of every pattern (83 rejected by both) and on all 88,475,049 searches, apart from the OG-005 retry-limit
boundary (95 native retry-limit errors: 92 LimitExceeded and 3 completed GregsEx searches).
