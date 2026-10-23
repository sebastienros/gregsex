# Oniguruma contract v1

`Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1` is Oniguruma 6.9.10 (tag v6.9.10, commit
`4ef89209a239c1aea328cf13c05a2807e5c146d1`) with `ONIG_SYNTAX_ONIGURUMA` (`ONIG_SYNTAX_DEFAULT`, the Ruby syntax of
doc/RE) over `ONIG_ENCODING_UTF16_LE` subjects: the regex dialect of TextMate grammars as vscode-oniguruma and
Onigwrap (TextMateSharp) run them. Data identity `Oniguruma-6.9.10-Unicode16.0.0-4ef89209`: Oniguruma's own generated
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
- A nested negated class is merged as Oniguruma's `not_code_range_buf` builds it: when it holds U+0000 it contributes
  every code point (so `[a[^\x{00}-\x{7F}]]` matches a space).
- Captures are final values; an iteration that fails restores them. Captures set inside a subexpression call stay
  set after it returns, and a call saves and restores the open start of the groups it re-enters.
- A repeat whose body matched empty stops, unless the iteration changed a group that a backreference outside the body
  refers to (Oniguruma's `EMPTY_CHECK_END_MEMST`).
- A look-behind body cannot consume past the look-behind position (Oniguruma's right range); its anchors still see
  the whole input. Its candidate starts are character heads, nearest first. Alternatives of different lengths are
  divided as Oniguruma divides them; equal-length alternatives, and a positive look-ahead's, stay one atomic
  look-around.
- `\K` beyond the match end (inside a look-ahead) is clamped to it.
- `(?~X)` is `(?:(?!X)\O)*`: the longest range holding no start of a match of `X`, then shorter.
- UTF-16 decoding follows `ONIG_ENCODING_UTF16_LE`: a high surrogate always starts a two-unit character (whatever
  follows), any other unit is a character; the character before a low surrogate starts one unit earlier. A high
  surrogate at the end is incomplete: only negated classes (`[^..]`, `\S`, `\D`, `\H`, `\W`, `\P{..}`) take it, as
  one unit. Two boundaries on such malformed text: Oniguruma's `\W` takes that last unit as two (an end past the
  input), and a high surrogate followed by another high surrogate decodes to a character negated property classes
  accept natively; GregsEx takes one unit, and decodes the pair arithmetically.
- `\X` is `(?>\O(?:\Y\O)*)` and `\y`/`\Y` are UAX #29 extended grapheme cluster boundaries (Unicode 16.0).

## Limits and failures (OG-005)

`RegexCompileLimits` bound pattern units, nesting, instructions, class ranges and groups (the TextMate adapter in
TextMateSharp passes Oniguruma-sized limits: grammars hold patterns of 282k units). `RegexMatchLimits.MaxWork`
counts backtracks per match attempt, as Oniguruma's retry limit (default 10,000,000) does, and `MaxWorkspaceBytes`
bounds the stacks; either ends a search with `LimitExceeded` where Oniguruma returns a negative error code. As at
Oniguruma's `SubexpCallMaxNestLevel` (20), a call nested 19 deep fails that path and the search goes on. Code size is
checked while instructions are emitted, and repeats nest counters rather than copies of nested repeats, so a pattern
compiles in time and space linear in its length. A tree deeper than a thread pool stack holds (a raised nesting limit) compiles on a thread with a stack
sized for it, up to 1 GiB; deeper is `NestingLimitExceeded`, never a stack overflow. The work count is not Oniguruma's exact retry count, so a search near the
limit may fail in one engine and not the other.

## Implementation (OG-006)

A dedicated backtracking machine over compact instructions (`Flavors/Oniguruma`): not the ECMAScript or PCRE2
executors, and no other regex engine. Register writes are undone through a log per choice frame; atomic groups and
look-arounds cut to barrier frames. Per pattern the compiler derives search prefilters that preserve results: start
anchors, a required literal or a required unit set (one of which every match contains, alternatives united), the
first units of a match (look-aheads included), a leading look-behind's preceding units, a literal prefix, and the
minimum length. Matching uses follow sets: a greedy run whose follow cannot start with one of its units becomes
possessive, and runs give units back only where the follow can start; alternatives skip branches whose first units
exclude the next unit; a failed attempt that started with an unbounded run resumes after the run when no
backreference or condition can depend on where the run started; literal and single-unit look-arounds are table and set tests. NativeAOT compatible: no runtime code generation.

## Conformance evidence (OG-007)

[`test/corpora/oniguruma`](../../test/corpora/oniguruma/README.md) holds native observations recorded through
Onigwrap 1.0.11's `libonigwrap` (Oniguruma 6.9.10): hand-written edge cases, every pattern of the shellscript,
typescript, javascript, html, css, json, yaml and csharp grammars of tm-grammars 1.32.22 over code lines, and 46,402
TextMateSharp scanner calls with their selected pattern and captures. During development the same oracle agreed on
the validity of all 22,489 distinct tm-grammars patterns and on 30.9 million searches over all 326 grammars.
