using GregsEx.SourceGeneration;

namespace GregsEx.Benchmarks.Compiled;

// The C# grammar's subexpression-call patterns (test/corpora/oniguruma/csharp-subexp-calls.json) compiled by the GregsEx source
// generator at build time: the "codegen" engine of CompiledOnigurumaCallBenchmarks. Generated from the fixture; keep in step.
internal static partial class GeneratedOnigurumaCallShapes
{
    [GeneratedPattern(@"(?x)
(?:\b(delegate)\b)\s+
(?<type_name>
  (?:
    (?:ref\s+(?:readonly\s+)?)?   # ref return
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s+
(\g<identifier>)\s*
(<([^<>]+)>)?\s*
(?=\()", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern DelegateDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?:
  (@?[_[:alpha:]][_[:alnum:]]*)\s*(\.)  # qualified name part
)*
(@?[_[:alpha:]][_[:alnum:]]*)\s*        # type name
(
  <
  (?<type_args>
    [^<>()]|
    \((?:[^<>()]|<[^<>()]*>|\([^<>()]*\))*\)|
    <\g<type_args>*>
  )*
  >\s*
)?                                      # optional type arguments
(?=\()                                  # followed by argument list", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern BaseClassConstructorCallBegin();
    [GeneratedPattern(@"(?x)
\b(fixed)\b\s+
(?<type_name>
  (?:
    (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
    (?<name_and_type_args> # identifier + type arguments (if any)
      \g<identifier>\s*
      (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
    )
    (?:\s*\.\s*\g<name_and_type_args>)* # Are there any more names being dotted into?
  )
)\s+
(\g<identifier>)\s* # buffer name
(?=\[)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern FixedSizeBufferDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s+
(\g<identifier>)\s* # first field name
(?!=>|==)(?=,|;|=|$)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern FieldDeclarationBegin();
    [GeneratedPattern(@"(?x)

# The negative lookahead below ensures that we don't match nested types
# or other declarations as properties.
(?![[:word:][:space:]]*\b(?:class|interface|struct|union|enum|event)\b)

(?<return_type>
  (?<type_name>
    (?:
      (?:ref\s+(?:readonly\s+)?)?   # ref return
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\?\s*)? # nullable suffix?
      (?:\s* # array suffix?
        \[
          (?:\s*,\s*)* # commata for multi-dimensional arrays
        \]
        \s*
        (?:\?)? # arrays can be nullable reference types
        \s*
      )*
    )
  )\s+
)
(?<interface_name>\g<type_name>\s*\.\s*)?
(?<property_name>\g<identifier>)\s*
(?=\{|=>|//|/\*|$)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern PropertyDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?<return_type>
  (?<type_name>
    (?:
      (?:ref\s+(?:readonly\s+)?)?   # ref return
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\?\s*)? # nullable suffix?
      (?:\s* # array suffix?
        \[
          (?:\s*,\s*)* # commata for multi-dimensional arrays
        \]
        \s*
        (?:\?)? # arrays can be nullable reference types
        \s*
      )*
    )
  )\s+
)
(?<interface_name>\g<type_name>\s*\.\s*)?
(?<indexer_name>this)\s*
(?=\[)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern IndexerDeclarationBegin();
    [GeneratedPattern(@"(?x)
\b(event)\b\s*
(?<return_type>
  (?<type_name>
    (?:
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\?\s*)? # nullable suffix?
      (?:\s* # array suffix?
        \[
          (?:\s*,\s*)* # commata for multi-dimensional arrays
        \]
        \s*
        (?:\?)? # arrays can be nullable reference types
        \s*
      )*
    )
  )\s+
)
(?<interface_name>\g<type_name>\s*\.\s*)?
(\g<identifier>)\s* # first event name
(?=\{|;|,|=|//|/\*|$)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern EventDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?<return_type>
  (?<type_name>
    (?:
      (?:ref\s+(?:readonly\s+)?)?   # ref return
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\?\s*)? # nullable suffix?
      (?:\s* # array suffix?
        \[
          (?:\s*,\s*)* # commata for multi-dimensional arrays
        \]
        \s*
        (?:\?)? # arrays can be nullable reference types
        \s*
      )*
    )
  )\s+
)
(?<interface_name>\g<type_name>\s*\.\s*)?
(\g<identifier>)\s*
(<([^<>]+)>)?\s*
(?=\()", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern MethodDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?<type_name>
  (?:
    (?:ref\s+(?:readonly\s+)?)?   # ref return
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s*
\b(?<operator_keyword>operator)\b\s*
(?<operator>[+\-*/%&|\^!=~<>]+|true|false)\s*
(?=\()", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern OperatorDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?<explicit_or_implicit_keyword>(?:\b(?:explicit|implicit)))\s*
(?<operator_keyword>(?:\b(?:operator)))\s*
(?<type_name>
  (?:
    (?:ref\s+(?:readonly\s+)?)?   # ref return
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s*
(?=\()", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ConversionOperatorDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?:
  (?:(\bref)\s+)?(\bvar\b)| # ref local
  (?<type_name>
    (?:
      (?:ref\s+)?   # ref local
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\?\s*)? # nullable suffix?
      (?:\s* # array suffix?
        \[
          (?:\s*,\s*)* # commata for multi-dimensional arrays
        \]
        \s*
        (?:\?)? # arrays can be nullable reference types
        \s*
      )*
    )
  )
)\s+
(\g<identifier>)\s+
\b(in)\b", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ForeachStatementPatterns1Patterns1Match();
    [GeneratedPattern(@"(?x) # match foreach (var (x, y) in ...)
(?:\b(var)\b\s*)?
(?<tuple>\((?:[^\(\)]|\g<tuple>)+\))\s+
\b(in)\b", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ForeachStatementPatterns1Patterns2Match();
    [GeneratedPattern(@"(?x)
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s*
(?:(\g<identifier>)\b)?", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern CatchClausePatterns0Patterns0Match();
    [GeneratedPattern(@"(?x)
(?:
  (?:(\bref)\s+(?:(\breadonly)\s+)?)?(\bvar\b)| # ref local
  (?<type_name>
    (?:
      (?:ref\s+(?:readonly\s+)?)?   # ref local
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\*\s*)* # pointer suffix?
      (?:\s*\?\s*)? # nullable suffix?
      (?:\s* # array suffix?
        \[
          (?:\s*,\s*)* # commata for multi-dimensional arrays
        \]
        \s*
        (?:\?)? # arrays can be nullable reference types
        \s*
      )*
    )
  )
)\s+
(\g<identifier>)\s*
(?!=>)
(?=,|;|=|\))", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern LocalVariableDeclarationBegin();
    [GeneratedPattern(@"(?x)
(?<const_keyword>\b(?:const)\b)\s*
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s+
(\g<identifier>)\s*
(?=,|;|=)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern LocalConstantDeclarationBegin();
    [GeneratedPattern(@"(?x)
\b((?:(?:async|unsafe|static|extern)\s+)*)
(?<type_name>
  (?:ref\s+(?:readonly\s+)?)?   # ref return
  (?:
    (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
    (?<name_and_type_args> # identifier + type arguments (if any)
      \g<identifier>\s*
      (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
    )
    (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
    (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
  )
  (?:\s*\?)? # nullable suffix?
  (?:\s* # array suffix?
    \[
      \s*(?:,\s*)* # commata for multi-dimensional arrays
    \]
    (?:\s*\?)? # arrays can be nullable reference types
  )*
)\s+
(\g<identifier>)\s*
(<[^<>]+>)?\s*
(?=\()", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern LocalFunctionDeclarationBegin();
    [GeneratedPattern(@"(?x) # e.g. var (x, y) = GetPoint();
(?:\b(var)\b\s*)
(?<tuple>\((?:[^\(\)]|\g<tuple>)+\))\s*
(?=;|=|\))", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern LocalTupleVarDeconstructionBegin();
    [GeneratedPattern(@"(?x) # e.g. (int x, var y) = GetPoint();
(?<tuple>\((?:[^\(\)]|\g<tuple>)+\))\s*
(?!=>|==)(?==)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern LocalTupleDeclarationDeconstructionMatch();
    [GeneratedPattern(@"(?x)
(?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))\s*
(?!=>|==)(?==)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern TupleDeconstructionAssignmentMatch();
    [GeneratedPattern(@"(?x) # e.g. int x OR var x
(?:
  \b(var)\b|
  (?<type_name>
    (?:
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\?\s*)? # nullable suffix?
      (?:\s* # array suffix?
        \[
          (?:\s*,\s*)* # commata for multi-dimensional arrays
        \]
        \s*
        (?:\?)? # arrays can be nullable reference types
        \s*
      )*
    )
  )
)\s+
(\g<identifier>)\b\s*
(?=[,)\]])", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern DeclarationExpressionLocalMatch();
    [GeneratedPattern(@"(?x) # e.g. int x OR var x
(?:
  \b(var)\b|
  (?<type_name>
    (?:
      (?:
        (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
        (?<name_and_type_args> # identifier + type arguments (if any)
          \g<identifier>\s*
          (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
        )
        (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
        (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
      )
      (?:\s*\?\s*)? # nullable suffix?
                (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
    )
  )
)\s+
(\g<identifier>)\b\s*
(?=[,)])", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern DeclarationExpressionTupleMatch();
    [GeneratedPattern(@"(?x)
(\()\s*
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s*
(\))(?=\s*-*!*@?[_[:alnum:]\(])", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern CastExpressionMatch();
    [GeneratedPattern(@"(?x)
(?<!\.)\b(as)\b\s*
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?(?!\?))? # nullable suffix?
    (?:\s* # array suffix?
      \[
        \s*(?:,\s*)* # commata for multi-dimensional arrays
      \]
      (?:\s*\?(?!\?))? # arrays can be nullable reference types
    )*
  )
)?", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern AsExpressionMatch();
    [GeneratedPattern(@"(?x)
(?:
  (?:(\?)\s*)?                                    # preceding null-conditional operator?
  (\.)\s*|                                        # preceding dot?
  (->)\s*                                         # preceding pointer arrow?
)?
(@?[_[:alpha:]][_[:alnum:]]*)\s*                  # method name
(
  <
  (?<type_args>
    [^<>()]|
    \((?:[^<>()]|<[^<>()]*>|\([^<>()]*\))*\)|
    <\g<type_args>*>
  )*
  >\s*
)?                                                # type arguments
(?=\()                                            # open paren of argument list", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern InvocationExpressionBegin();
    [GeneratedPattern(@"(?x)
(\.)?\s*
(@?[_[:alpha:]][_[:alnum:]]*)
(?<type_params>\s*<([^<>]|\g<type_params>)+>\s*)
(?=
  (\s*\?)?
  \s*\.\s*@?[_[:alpha:]][_[:alnum:]]*
)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern MemberAccessExpressionPatterns1Match();
    [GeneratedPattern(@"(?x)
(new)(?:\s+
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
))?\s*
(?=\()", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ObjectCreationExpressionWithParametersBegin();
    [GeneratedPattern(@"(?x)
(new)\s+
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s*
(?=\{|//|/\*|$)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ObjectCreationExpressionWithNoParametersMatch();
    [GeneratedPattern(@"(?x)
\b(new|stackalloc)\b\s*
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\*\s*)* # pointer suffix?
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)?\s*
(?=\[)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ArrayCreationExpressionBegin();
    [GeneratedPattern(@"(?x)
(?:(?:\b(ref|params|out|in|this)\b)\s+)?
(?<type_name>
  (?:
    (?:ref\s+)?   # ref return
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^()]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s+
(\g<identifier>)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ParameterMatch();
    [GeneratedPattern(@"(?x)
\b(from)\b\s*
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)?
\s+(\g<identifier>)\b\s*
\b(in)\b\s*", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern QueryExpressionBegin();
    [GeneratedPattern(@"(?x)
\b(join)\b\s*
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)?
\s+(\g<identifier>)\b\s*
\b(in)\b\s*", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern JoinClauseBegin();
    [GeneratedPattern(@"(?x)
((?:\b(?:async|static)\b\s*)*)
(?:
  (@?[_[:alpha:]][_[:alnum:]]*)\b|
  (\()
    (?<tuple>(?:[^()]|\(\g<tuple>\))*)
  (\))
)\s*
(=>)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern AnonymousMethodExpressionPatterns0Begin();
    [GeneratedPattern(@"(?x)
(?:\b(ref|params|out|in)\b\s*)?
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args><(?:[^<>]|\g<type_args>)*>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^()]|\g<tuple>)*\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)\s*
\b(\g<identifier>)\b", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ExplicitAnonymousFunctionParameterMatch();
    [GeneratedPattern(@"(?x)
(?<type_name>
  (?:
    (?:
      (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
      (?<name_and_type_args> # identifier + type arguments (if any)
        \g<identifier>\s*
        (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
      )
      (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
      (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
    )
    (?:\s*\?\s*)? # nullable suffix?
    (?:\s* # array suffix?
      \[
        (?:\s*,\s*)* # commata for multi-dimensional arrays
      \]
      \s*
      (?:\?)? # arrays can be nullable reference types
      \s*
    )*
  )
)
(?:(?<tuple_name>\g<identifier>)\b)?", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern TupleElementMatch();

    internal static readonly Dictionary<string, Func<OnigurumaPattern>> Map = new()
    {
        ["repository.delegate-declaration.begin"] = DelegateDeclarationBegin,
        ["repository.base-class-constructor-call.begin"] = BaseClassConstructorCallBegin,
        ["repository.fixed-size-buffer-declaration.begin"] = FixedSizeBufferDeclarationBegin,
        ["repository.field-declaration.begin"] = FieldDeclarationBegin,
        ["repository.property-declaration.begin"] = PropertyDeclarationBegin,
        ["repository.indexer-declaration.begin"] = IndexerDeclarationBegin,
        ["repository.event-declaration.begin"] = EventDeclarationBegin,
        ["repository.method-declaration.begin"] = MethodDeclarationBegin,
        ["repository.operator-declaration.begin"] = OperatorDeclarationBegin,
        ["repository.conversion-operator-declaration.begin"] = ConversionOperatorDeclarationBegin,
        ["repository.foreach-statement.patterns[1].patterns[1].match"] = ForeachStatementPatterns1Patterns1Match,
        ["repository.foreach-statement.patterns[1].patterns[2].match"] = ForeachStatementPatterns1Patterns2Match,
        ["repository.catch-clause.patterns[0].patterns[0].match"] = CatchClausePatterns0Patterns0Match,
        ["repository.local-variable-declaration.begin"] = LocalVariableDeclarationBegin,
        ["repository.local-constant-declaration.begin"] = LocalConstantDeclarationBegin,
        ["repository.local-function-declaration.begin"] = LocalFunctionDeclarationBegin,
        ["repository.local-tuple-var-deconstruction.begin"] = LocalTupleVarDeconstructionBegin,
        ["repository.local-tuple-declaration-deconstruction.match"] = LocalTupleDeclarationDeconstructionMatch,
        ["repository.tuple-deconstruction-assignment.match"] = TupleDeconstructionAssignmentMatch,
        ["repository.declaration-expression-local.match"] = DeclarationExpressionLocalMatch,
        ["repository.declaration-expression-tuple.match"] = DeclarationExpressionTupleMatch,
        ["repository.cast-expression.match"] = CastExpressionMatch,
        ["repository.as-expression.match"] = AsExpressionMatch,
        ["repository.invocation-expression.begin"] = InvocationExpressionBegin,
        ["repository.member-access-expression.patterns[1].match"] = MemberAccessExpressionPatterns1Match,
        ["repository.object-creation-expression-with-parameters.begin"] = ObjectCreationExpressionWithParametersBegin,
        ["repository.object-creation-expression-with-no-parameters.match"] = ObjectCreationExpressionWithNoParametersMatch,
        ["repository.array-creation-expression.begin"] = ArrayCreationExpressionBegin,
        ["repository.parameter.match"] = ParameterMatch,
        ["repository.query-expression.begin"] = QueryExpressionBegin,
        ["repository.join-clause.begin"] = JoinClauseBegin,
        ["repository.anonymous-method-expression.patterns[0].begin"] = AnonymousMethodExpressionPatterns0Begin,
        ["repository.explicit-anonymous-function-parameter.match"] = ExplicitAnonymousFunctionParameterMatch,
        ["repository.tuple-element.match"] = TupleElementMatch,
    };
}
