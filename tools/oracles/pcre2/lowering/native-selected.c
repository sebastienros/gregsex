/* Development-only PCRE2-10.46 native oracle. No production forwarding. */
#define PCRE2_CODE_UNIT_WIDTH 8
#include "pcre2.h"
#include <stdio.h>
#include <string.h>
#include <stdint.h>
static const char *active;
static int site(pcre2_callout_enumerate_block *b, void *unused) {
 (void)unused;
 printf("{\"id\":\"%s\",\"event\":\"site\",\"number\":%u,\"position\":%zu,\"length\":%zu}\n",active,b->callout_number,b->pattern_position,b->next_item_length);return 0;
}
static int call(pcre2_callout_block *b, void *unused) {
 (void)unused;
 printf("{\"id\":\"%s\",\"event\":\"call\",\"position\":%zu,\"current\":%zu,\"top\":%u,\"flags\":%u}\n",active,b->pattern_position,b->current_position,b->capture_top,b->callout_flags);return 0;
}
struct probe {const char *id,*pattern,*subject;uint32_t flags;};
static const struct probe probes[]={
 {"algebra-empty","(?[ [a] - [a] ])","a",4},
 {"algebra-negative","(?[ ![a] ])","b",4},
 {"algebra-disjoint","(?[ [a-z] - [aeiou] ])+1","bc1",4},
 {"literal-lf","\n","\n",0},
 {"hex-lf","\\x0a","\n",0},
 {"space-type","[\\s]","\n",0},
 {"negative-multiple","[^\\na]","b",0},
 {"nested-explicit","(?[ [\\n] | [a] ])","a",0},
 {"nested-vertical","(?[ [\\v] | [a] ])","a",0},
 {"zero-group-newline","(\\n){0}a","a",0},
 {"define-newline","(?(DEFINE)(a\\n))a","a",0},
 {"unreachable-newline","a(*ACCEPT)|\\n","a",0},
 {"cr-literal","\\r","\r",0},
 {"lf-class","[\\n]","\n",0},
 {"vertical-type","[\\v]","\n",0},
 {"newline-inside-range","[\\x09-\\x0e]","\n",0},
 {"newline-endpoint","[\\x0a-\\x0e]","\n",0},
 {"negative-newline","[^\\n]","a",0},
 {"zero-newline","\\n{0}a","a",0},
 {"absolute-start","\\Aabc","abc",4},
 {"negative-type","\\D","a",4},
 {"negative-class","[^ab]","c",4},
 {"negative-single","[^a]","b",4},
 {"native-type","\\d","Q",0},
 {"imported-type","\\d","Q",0},
 {"wide-range","[\\x{100}-\\x{300}]a","a",0x80004},
 {"repeat-inner","(a+){2,3}b","aaab",4},
 {"manual-condition","(?(?C1)(?=a)a|b)","a",4},
 {"repeat-bounded","(a){2,3}b","aaab",4},
 {"repeat-exact","(a){2}b","aab",4},
 {"repeat-zero","(?:a){0}b","b",4},
 {"literal-zero","a{0}b","b",4},
 {"lazy-disjoint","a+?b","aaab",4},
 {"lazy-end","a+?","aaa",4},
 {"greedy-end","a+","aaa",4},
 {"class-pair","[aA]+b","AAb",4},
 {"class-ranges","[a-zA-Z]+[0-9]","aa1",4},
 {"properties","\\p{L}+\\p{Nd}","aa1",4},
 {"nested-alts","a+(b|c)","aaac",4},
 {"assertion-repeat","(?<=(?=.)*)a","a",4},
 {"conditional-assertion","(?(?=a)a|b)","a",4},
 {"conditional-backward","(?(?<=a)a|b)","b",4},
 {"asserted-empty","(?=ab)","ab",0},
 {"fixed-long-alts","(?<=a{300}|b)c","bc",0},
 {"commit-full","(*COMMIT)ABC","DEFABC",0},
 {"commit-none","(*NO_START_OPT)(*COMMIT)ABC","DEFABC",0},
 {"imported-range","(?i)[A-C]","x",0},
 {"imported-single","(?i)[A]","x",0},
 {"imported-negative","(?i)[^A-C]","x",0},
};
int main(void){
 for(size_t i=0;i<sizeof(probes)/sizeof(*probes);i++){
  const struct probe *p=probes+i;active=p->id;int error;PCRE2_SIZE off;
  pcre2_compile_context *cc=pcre2_compile_context_create(NULL);
  uint8_t tables[1088];const uint8_t *defaults=pcre2_maketables(NULL);memcpy(tables,defaults,1088);pcre2_maketables_free(NULL,defaults);
  if(strcmp(p->id,"imported-type")==0){tables[832+'Q']|=8;pcre2_set_character_tables(cc,tables);}
  else if(strncmp(p->id,"imported-",9)==0){tables[256+'A']='x';tables[256+'x']='x';pcre2_set_character_tables(cc,tables);}
  pcre2_code *code=pcre2_compile((const uint8_t*)p->pattern,strlen(p->pattern),p->flags,&error,&off,cc);
  printf("{\"id\":\"%s\",\"event\":\"compile\",\"error\":%d,\"offset\":%zu}\n",active,code?0:error,off);
  if(code){uint32_t first,ftype,last,ltype,min,opts;pcre2_pattern_info(code,PCRE2_INFO_FIRSTCODEUNIT,&first);pcre2_pattern_info(code,PCRE2_INFO_FIRSTCODETYPE,&ftype);pcre2_pattern_info(code,PCRE2_INFO_LASTCODEUNIT,&last);pcre2_pattern_info(code,PCRE2_INFO_LASTCODETYPE,&ltype);pcre2_pattern_info(code,PCRE2_INFO_MINLENGTH,&min);pcre2_pattern_info(code,PCRE2_INFO_ALLOPTIONS,&opts);
   printf("{\"id\":\"%s\",\"event\":\"info\",\"first\":%u,\"firstType\":%u,\"required\":%u,\"requiredType\":%u,\"minimum\":%u,\"options\":%u}\n",active,first,ftype,last,ltype,min,opts);
   uint32_t behind,empty,crlf;const uint8_t *bitmap=NULL;pcre2_pattern_info(code,PCRE2_INFO_MAXLOOKBEHIND,&behind);pcre2_pattern_info(code,PCRE2_INFO_MATCHEMPTY,&empty);pcre2_pattern_info(code,PCRE2_INFO_FIRSTBITMAP,&bitmap);pcre2_pattern_info(code,PCRE2_INFO_HASCRORLF,&crlf);
   printf("{\"id\":\"%s\",\"event\":\"study\",\"behind\":%u,\"empty\":%u,\"crlf\":%u,\"bitmap\":\"",active,behind,empty,crlf);if(bitmap)for(int j=0;j<32;j++)printf("%02x",bitmap[j]);printf("\"}\n");
   pcre2_callout_enumerate(code,site,NULL);pcre2_match_context *mc=pcre2_match_context_create(NULL);pcre2_set_callout(mc,call,NULL);pcre2_set_match_limit(mc,100000);
   pcre2_match_data *md=pcre2_match_data_create(8,NULL);int rc=pcre2_match(code,(const uint8_t*)p->subject,strlen(p->subject),0,0,md,mc);
   printf("{\"id\":\"%s\",\"event\":\"match\",\"return\":%d}\n",active,rc);pcre2_match_data_free(md);pcre2_match_context_free(mc);pcre2_code_free(code);
  }pcre2_compile_context_free(cc);
 }
 return 0;
}
