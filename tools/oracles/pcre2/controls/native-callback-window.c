#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include "pcre2.h"

static const char *case_id;
static int callback(pcre2_callout_block *cb, void *unused)
{
  (void)unused;
  printf("{\"id\":\"%s\",\"width\":%d,\"event\":\"callout\",\"number\":%u,\"subjectLength\":%zu,\"currentPosition\":%zu,\"startMatch\":%zu,\"flags\":%u,\"captureTop\":%u,\"captureLast\":%u}\n",
    case_id, PCRE2_CODE_UNIT_WIDTH, cb->callout_number, cb->subject_length,
    cb->current_position, cb->start_match, cb->callout_flags, cb->capture_top, cb->capture_last);
  return 0;
}
static void run(const char *id, const char *pattern, const uint32_t *values, size_t length, uint32_t options)
{
  PCRE2_UCHAR source[128], subject[8];
  size_t n = strlen(pattern); int error; PCRE2_SIZE offset;
  for (size_t i = 0; i < n; i++) source[i] = (PCRE2_UCHAR)(unsigned char)pattern[i];
  for (size_t i = 0; i < length; i++) subject[i] = (PCRE2_UCHAR)values[i];
  pcre2_code *code = pcre2_compile(source, n, options | PCRE2_NO_START_OPTIMIZE | PCRE2_NO_AUTO_POSSESS, &error, &offset, NULL);
  if (!code) { printf("{\"id\":\"%s\",\"compileError\":%d,\"offset\":%zu}\n", id, error, offset); return; }
  pcre2_match_context *context = pcre2_match_context_create(NULL);
  pcre2_match_data *data = pcre2_match_data_create_from_pattern(code, NULL);
  case_id = id; pcre2_set_callout(context, callback, NULL);
  int rc = pcre2_match(code, subject, length, 0, 0, data, context);
  printf("{\"id\":\"%s\",\"width\":%d,\"event\":\"match\",\"returnCode\":%d", id, PCRE2_CODE_UNIT_WIDTH, rc);
  if (rc >= 0) { PCRE2_SIZE *v = pcre2_get_ovector_pointer(data); printf(",\"start\":%zu,\"end\":%zu", v[0], v[1]); }
  printf("}\n");
  pcre2_match_data_free(data); pcre2_match_context_free(context); pcre2_code_free(code);
}
int main(void)
{
#if PCRE2_CODE_UNIT_WIDTH == 8
  uint32_t invalid = 255;
#else
  uint32_t invalid = 0xd800;
#endif
  uint32_t first[] = {97, invalid, 98}, prefix[] = {invalid, 97, invalid, 98}, scan[] = {97, 98};
  run("initial-fragment", "(?C1)b", first, 3, PCRE2_MATCH_INVALID_UTF);
  run("invalid-prefix", "(?C1)b", prefix, 4, PCRE2_MATCH_INVALID_UTF);
  run("scan-raw", "(a)(*scs:(1)(?C1)a)(?C2)b", scan, 2, 0);
  run("scan-utf", "(a)(*scs:(1)(?C1)a)(?C2)b", scan, 2, PCRE2_UTF);
  uint32_t ac[] = {97, 99};
  run("called-lexical-assert-accept", "(?=(?<r>a(*ACCEPT)b))(?&r)c", ac, 2, 0);
  run("scan-accept", "(a)(*scs:(1)(?C1)a(*ACCEPT)b)(?C2)b", scan, 2, 0);
  return 0;
}
