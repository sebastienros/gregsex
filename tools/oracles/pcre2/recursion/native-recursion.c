#include <stdio.h>
#include <string.h>
#include <pcre2.h>
struct sample { const char *id,*pattern,*subject; unsigned flags; };
static const struct sample samples[]={
 {"whole-positive","a(?R)?b","aaabbb",0},
 {"whole-negative","a(?R)?b","aab",0},
 {"whole-empty","a(?R)?b","",0},
 {"non-atomic","(a|ab)(?1)c$","aabc",0},
 {"atomic","(a|ab)(?>(?1))c$","aabc",0},
 {"define","(?(DEFINE)(?<g>ab))(?&g)","ab",0},
 {"define-negative","(?(DEFINE)(?<g>ab))(?&g)","ac",0},
 {"inner-capture-retry","(?(DEFINE)(?<g>(a)|(ab)))(?&g)c$","abc",0},
 {"recursive-captures","(a(?1)?(b))$","aabb",0},
 {"most-recent","(?(DEFINE)(?<g>a(?&h))(?<h>(?(R1)x|b)))(?&g)","ab",0},
 {"inactive","(?(DEFINE)(?<g>a|aa))(?&g)(?(R)x|b)$","aab",0},
 {"any-recursion","(?(R)a|a(?R)?)","aa",0},
 {"r0","(?(R0)a|a(?R)?)","aa",0},
 {"capture-false","(a)?(?(1)b|c)","c",0},
 {"capture-true","(a)?(?(1)b|c)","ab",0},
 {"open-repeated","(a(?(1)b|c))+","acab",0},
 {"version-equal","(?(VERSION=10.46)a|b)","a",0},
 {"version-greater","(?(VERSION>=10.47)a|b)","b",0},
 {"loop","(?R)","",0},
 {"disabled-loop","(?R)","",PCRE2_DISABLE_RECURSELOOP_CHECK},
 {"positive-predicate","(?(?=(a))a|b)","a",0},
 {"false-negative-capture","(?(?!(a))b|a)","a",0},
 {"duplicate-name-capture","(?J)(?:(?<R>a)|(?<R>b))(?(R)c|d)","bc",0},
 {"absent-else","(?(VERSION=99.0)a)","",0}
};
int main(void) {
 for(int utf=0;utf<2;utf++) for(unsigned i=0;i<sizeof(samples)/sizeof(samples[0]);i++) {
  PCRE2_UCHAR pattern[512], subject[128]; const struct sample *s=&samples[i];
  size_t plen=strlen(s->pattern), slen=strlen(s->subject);
  for(size_t j=0;j<plen;j++) pattern[j]=(unsigned char)s->pattern[j];
  for(size_t j=0;j<slen;j++) subject[j]=(unsigned char)s->subject[j];
  int error; PCRE2_SIZE offset; pcre2_code *code=pcre2_compile(pattern,plen,utf?PCRE2_UTF:0,&error,&offset,NULL);
  if(!code) { fprintf(stderr,"compile %s: %d at %zu\n",s->id,error,offset); return 1; }
  pcre2_match_data *md=pcre2_match_data_create_from_pattern(code,NULL); pcre2_match_context *mc=pcre2_match_context_create(NULL);
  pcre2_set_depth_limit(mc,64); pcre2_set_match_limit(mc,10000);
  int rc=pcre2_match(code,subject,slen,0,PCRE2_ANCHORED|s->flags,md,mc);
  unsigned groups; pcre2_pattern_info(code,PCRE2_INFO_CAPTURECOUNT,&groups); PCRE2_SIZE *ov=pcre2_get_ovector_pointer(md);
  printf("{\"id\":\"%s\",\"width\":%d,\"utf\":%s,\"returnCode\":%d,\"pairs\":[",s->id,PCRE2_CODE_UNIT_WIDTH,utf?"true":"false",rc);
  if(rc>=0) for(unsigned g=0;g<=groups;g++) {
   if(g) putchar(',');
   if(ov[g*2]==PCRE2_UNSET) printf("[null,null]"); else printf("[%zu,%zu]",ov[g*2],ov[g*2+1]);
  }
  printf("]}\n"); pcre2_match_context_free(mc);pcre2_match_data_free(md);pcre2_code_free(code);
 }
 return 0;
}
