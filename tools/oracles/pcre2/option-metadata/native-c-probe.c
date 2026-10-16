/* Development-only supplementary compilation oracle. */
#include "pcre2.h"
#include <stdio.h>
#if PCRE2_CODE_UNIT_WIDTH == 8
#include "native-c-cases-8.h"
#else
#include "native-c-cases-16.h"
#endif
int main(void) {
  for (size_t i=0; i<sizeof(cases)/sizeof(*cases); i++) {
    const struct probe *p=cases+i;
    int error;
    PCRE2_SIZE offset;
    pcre2_code *code=pcre2_compile(p->units,p->length,p->flags,&error,&offset,NULL);
    uint32_t c=0,j=0;
    if (code && (pcre2_pattern_info(code,PCRE2_INFO_HASBACKSLASHC,&c)!=0 ||
                 pcre2_pattern_info(code,PCRE2_INFO_JCHANGED,&j)!=0)) return 1;
    printf("{\"id\":\"%s\",\"accepted\":%s,\"error\":%d,\"offset\":%zu,\"jchanged\":%u,\"hasBackslashC\":%u}\n",
        p->id,code?"true":"false",code?0:error,code?0:offset,j,c);
    if(code) pcre2_code_free(code);
  }
  return 0;
}
