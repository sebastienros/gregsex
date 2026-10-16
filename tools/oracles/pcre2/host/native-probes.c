/* Development-only finite host oracle, linked against preserved pinned10.46
 * static libraries. No production native engine dependency. */
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include "pcre2.h"
typedef struct {const char *id; const PCRE2_UCHAR *p,*s,*r; size_t pn,sn,rn; uint32_t compile,extra,flags; unsigned cap,pairs,start; int subret,caseret;} Case;
#include "native-cases.h"
static const Case *current; static unsigned subs,cases;
static void units(const PCRE2_UCHAR *s,size_t n){putchar('[');for(size_t i=0;i<n;i++)printf("%s%u",i?",":"",(unsigned)s[i]);putchar(']');}
static int subcall(pcre2_substitute_callout_block *c,void *u){(void)u;subs++;return current->subret;}
static size_t casecall(PCRE2_SPTR in,size_t n,PCRE2_UCHAR *out,size_t cap,int mode,void *u){
 (void)u;cases++;
 printf("{\"id\":\"%s\",\"width\":%d,\"event\":\"case\",\"mode\":%d,\"capacity\":%zu,\"input\":",current->id,PCRE2_CODE_UNIT_WIDTH,mode,cap);units(in,n);printf("}\n");
 if(current->caseret==2)return PCRE2_UNSET;
 if(current->caseret==3){if(cap>=1)out[0]='!';return 1;}
 if(current->caseret==4){for(size_t i=0;i<cap&&i<n*2;i++)out[i]='X';return n*2;}
 size_t k=n<cap?n:cap;memmove(out,in,k*sizeof(*out));return n;
}
int main(void){
 for(size_t i=0;i<sizeof(inputs)/sizeof(inputs[0]);i++){
  current=&inputs[i];subs=cases=0;int error;size_t offset;
  pcre2_compile_context *cc=pcre2_compile_context_create(NULL);pcre2_set_compile_extra_options(cc,current->extra);
  pcre2_code *code=pcre2_compile(current->p,current->pn,current->compile,&error,&offset,cc);pcre2_compile_context_free(cc);
  if(!code){fprintf(stderr,"%s compile%d offset%zu\n",current->id,error,offset);return 2;}
  pcre2_match_context *mc=pcre2_match_context_create(NULL);
  if(current->subret!=999)pcre2_set_substitute_callout(mc,subcall,NULL);
  if(current->caseret)pcre2_set_substitute_case_callout(mc,casecall,NULL);
  pcre2_match_data *md=current->pairs?pcre2_match_data_create(current->pairs,NULL):NULL;
  PCRE2_UCHAR out[256];for(int j=0;j<256;j++)out[j]=85;size_t length=current->cap;
  int rc=pcre2_substitute(code,current->s,current->sn,current->start,current->flags,md,mc,current->r,current->rn,out,&length);
  printf("{\"id\":\"%s\",\"width\":%d,\"event\":\"result\",\"returnCode\":%d,\"reportedLength\":",current->id,PCRE2_CODE_UNIT_WIDTH,rc);
  if(length==PCRE2_UNSET)printf("-1");else printf("%zu",length);
  printf(",\"output\":");units(out,rc>=0?length:0);printf(",\"subCalls\":%u,\"caseCalls\":%u}\n",subs,cases);
  if(md)pcre2_match_data_free(md);pcre2_match_context_free(mc);pcre2_code_free(code);
 }
 return 0;
}
