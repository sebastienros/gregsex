/* Development oracle only: bounded compilation using retained PCRE2-10.46. */
#include "pcre2.h"
#include <stdio.h>
#if PCRE2_CODE_UNIT_WIDTH == 8
#include "native-cases-8.h"
#else
#include "native-cases-16.h"
#endif
int main(void) {
  for (size_t i = 0; i < sizeof(cases)/sizeof(*cases); i++) {
    const struct probe *p = cases + i;
    int error;
    PCRE2_SIZE offset;
    pcre2_code *code = pcre2_compile(p->units, p->length, p->flags, &error, &offset, NULL);
    uint32_t value = 0;
    if (code && pcre2_pattern_info(code, PCRE2_INFO_JCHANGED, &value) != 0) return 1;
    printf("{\"id\":\"%s\",\"accepted\":%s,\"error\":%d,\"offset\":%zu,\"jchanged\":%u}\n",
        p->id, code ? "true" : "false", code ? 0 : error, code ? 0 : offset, value);
    if (code) pcre2_code_free(code);
  }
  return 0;
}
