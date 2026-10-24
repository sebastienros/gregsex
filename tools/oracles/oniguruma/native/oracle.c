/* Development-only shim over Oniguruma for tools/oracles/oniguruma/oracle (see ../README.md). Patterns compile as
 * TextMate hosts compile them (Onigwrap's onigwrap_create): UTF-16LE, ONIG_SYNTAX_DEFAULT, ONIG_OPTION_CAPTURE_GROUP;
 * searches run from a start offset to the end of the subject with ONIG_OPTION_NONE. Lengths and offsets are UTF-16
 * code units. */
#include <stddef.h>
#include "oniguruma.h"

#define EXPORT __attribute__((visibility("default")))

/* Returns the regex, or NULL with *error set to Oniguruma's (negative) error code. */
EXPORT regex_t *oracle_create(const unsigned short *pattern, int length, int *error)
{
  static int initialized;
  OnigEncoding encodings[1] = { ONIG_ENCODING_UTF16_LE };
  regex_t *regex;
  OnigErrorInfo info;
  int r;

  if (!initialized) {
    onig_initialize(encodings, 1);
    initialized = 1;
  }

  r = onig_new(&regex, (const OnigUChar *)pattern, (const OnigUChar *)(pattern + length), ONIG_OPTION_CAPTURE_GROUP,
               ONIG_ENCODING_UTF16_LE, ONIG_SYNTAX_DEFAULT, &info);
  *error = r == ONIG_NORMAL ? 0 : r;
  return r == ONIG_NORMAL ? regex : NULL;
}

EXPORT void oracle_free(regex_t *regex)
{
  onig_free(regex);
}

/* Searches subject[start..length). Returns the number of groups (group 0 included) and writes their start and end
 * offsets to captures (-1, -1 for a group that did not participate; at most capacity groups), or ONIG_MISMATCH (-1)
 * for no match, or another negative Oniguruma error code (such as ONIGERR_RETRY_LIMIT_IN_MATCH_OVER, -17). */
EXPORT int oracle_search(regex_t *regex, const unsigned short *subject, int length, int start, int *captures, int capacity)
{
  OnigRegion *region = onig_region_new();
  const OnigUChar *s = (const OnigUChar *)subject;
  const OnigUChar *end = (const OnigUChar *)(subject + length);
  int r = onig_search(regex, s, end, s + 2 * start, end, region, ONIG_OPTION_NONE);
  if (r >= 0) {
    int g;
    r = region->num_regs;
    for (g = 0; g < r && g < capacity; g++) {
      captures[2 * g] = region->beg[g] < 0 ? -1 : region->beg[g] / 2;
      captures[2 * g + 1] = region->end[g] < 0 ? -1 : region->end[g] / 2;
    }
  }
  onig_region_free(region, 1);
  return r;
}
