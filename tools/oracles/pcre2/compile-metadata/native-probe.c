/* Bounded development oracle only. Uses retained PCRE2-10.46 libraries. */
#include "pcre2.h"
#include <stdio.h>
#include <string.h>
#include "cases.h"
int main(void) {
 for(size_t i=0;i<sizeof(cases)/sizeof(*cases);i++) {
  const struct probe *p=cases+i; int error;PCRE2_SIZE offset;
  pcre2_compile_context *cc=pcre2_compile_context_create(NULL);
  pcre2_set_compile_extra_options(cc,p->extra);
  unsigned char tables[1088];const unsigned char *defaults=pcre2_maketables(NULL);
  memcpy(tables,defaults,1088);pcre2_maketables_free(NULL,defaults);
  if(p->imported){tables[256+'a']=10;tables[256+10]='a';pcre2_set_character_tables(cc,tables);}
  pcre2_code *code=pcre2_compile(p->units,p->length,p->flags,&error,&offset,cc);
  uint32_t value=0;if(code)pcre2_pattern_info(code,PCRE2_INFO_HASCRORLF,&value);
  printf("{\"id\":\"%s\",\"accepted\":%s,\"error\":%d,\"offset\":%zu,\"hasCrOrLf\":%u}\n",p->id,code?"true":"false",code?0:error,code?0:offset,value);
  if(code)pcre2_code_free(code);pcre2_compile_context_free(cc);
 }
 return 0;
}
