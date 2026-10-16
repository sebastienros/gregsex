/* Development-only native oracle. Build twice with the pinned static libraries.
 * Protocol: compiled immutable numeric inputs -> bounded JSONL event records.
 * No matcher/parser implementation, dynamic loader, or GregsEx dependency. */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <locale.h>
#include "pcre2.h"
typedef struct {
 const char *id; int op;
 const PCRE2_UCHAR *pattern,*subject,*replacement,*name;
 size_t pn,sn,rn; uint32_t compile,extra,match,substitute,newline,bsr,varlb,pairs,capacity,start;
 int optimize[3], optimizer;
 int64_t work,depth,heap,offset;
 const uint8_t *tables;
 const int *callret,*subret; size_t calln,subn;
 const PCRE2_UCHAR *caseout; size_t casen; int casemode;
 const size_t *initial; size_t initialn;
} Case;
#include "cases.h"
static const Case *current;
static unsigned callindex,subindex,caseindex;
static void begin(const char *event){printf("{\"id\":\"%s\",\"width\":%d,\"event\":\"%s\"",current->id,PCRE2_CODE_UNIT_WIDTH,event);}
static void units(const PCRE2_UCHAR *s,size_t n){putchar('[');for(size_t i=0;i<n;i++)printf("%s%u",i?",":"",s[i]);putchar(']');}
static size_t zlen(const PCRE2_UCHAR *s){size_t n=0;if(s)while(s[n])n++;return n;}
static void mark(PCRE2_SPTR p){if(p)units(p,p[-1]);else printf("null");}
static void number(size_t v){if(v==PCRE2_UNSET)printf("null");else printf("%zu",v);}
static void pairs(const size_t *v,size_t n){putchar('[');for(size_t i=0;i<n;i++){if(i)putchar(',');putchar('[');number(v[2*i]);putchar(',');number(v[2*i+1]);putchar(']');}putchar(']');}
static int matchcall(pcre2_callout_block *c,void *user){
 (void)user;int ret=callindex<current->calln?current->callret[callindex]:0;callindex++;
 if(callindex>100000){fprintf(stderr,"Bounded observer trace exhausted\n");exit(3);}
 begin("callout");printf(",\"version\":%u,\"number\":%u,\"isString\":%s,\"subjectLength\":%zu,\"captureTop\":%u,\"captureLast\":%u,\"startMatch\":%zu,\"currentPosition\":%zu,\"patternPosition\":%zu,\"nextItemLength\":%zu,\"stringOffset\":%zu,\"flags\":%u,\"returnCode\":%d,\"subjectUnits\":",c->version,c->callout_number,c->callout_string?"true":"false",c->subject_length,c->capture_top,c->capture_last,c->start_match,c->current_position,c->pattern_position,c->next_item_length,c->callout_string_offset,c->callout_flags,ret);
 units(c->subject,c->subject_length);printf(",\"stringUnits\":");units(c->callout_string,c->callout_string_length);
 printf(",\"markUnits\":");mark(c->mark);printf(",\"captures\":");
 /* Group0 is explicitly undefined during matching; never read it. */
 printf("[null");for(unsigned i=1;i<c->capture_top;i++){printf(",[");number(c->offset_vector[2*i]);putchar(',');number(c->offset_vector[2*i+1]);putchar(']');}printf("]}\n");return ret;
}
static int subcall(pcre2_substitute_callout_block *c,void *user){
 (void)user;int ret=subindex<current->subn?current->subret[subindex]:0;subindex++;
 begin("substituteCallout");printf(",\"version\":%u,\"count\":%u,\"outputStart\":%zu,\"outputEnd\":%zu,\"proposedOutput\":",c->version,c->subscount,c->output_offsets[0],c->output_offsets[1]);units(c->output,c->output_offsets[1]);printf(",\"captures\":");pairs(c->ovector,c->oveccount);printf(",\"returnCode\":%d}\n",ret);return ret;
}
static size_t casecall(PCRE2_SPTR in,size_t n,PCRE2_UCHAR *out,size_t cap,int mode,void *user){
 (void)user;caseindex++;begin("caseCallout");printf(",\"mode\":%d,\"inputUnits\":",mode);units(in,n);
 size_t need=current->casemode==2?PCRE2_SIZE_MAX:(current->casemode==1?current->casen:n);
 /* Capture input before writing because upstream can pass overlapping buffers. */
 size_t write=need==PCRE2_SIZE_MAX?0:(need<cap?need:cap);
 if(current->casemode==1){if(write)memmove(out,current->caseout,write*sizeof(*out));}
 else if(current->casemode==0&&write)memmove(out,in,write*sizeof(*out));
 printf(",\"outputUnits\":");units(out,write);printf(",\"outputCapacity\":%zu,\"requiredUnits\":",cap);number(need);
 printf(",\"overlap\":%s}\n", ((uintptr_t)out<(uintptr_t)(in+n)&&(uintptr_t)in<(uintptr_t)(out+cap))?"true":"false");return need;
}
static int enumerate(pcre2_callout_enumerate_block *c,void *u){
 (void)u;begin("calloutSite");printf(",\"version\":%u,\"number\":%u,\"isString\":%s,\"patternPosition\":%zu,\"nextItemLength\":%zu,\"stringOffset\":%zu,\"stringUnits\":",c->version,c->callout_number,c->callout_string?"true":"false",c->pattern_position,c->next_item_length,c->callout_string_offset);units(c->callout_string,c->callout_string_length);printf("}\n");return 0;
}
static void metadata(pcre2_code *code){
 const unsigned ids[]={0,1,2,3,4,5,6,8,9,11,12,13,14,15,16,17,20,21,23,25,26};
 begin("info");printf(",\"values\":[");for(size_t i=0;i<sizeof(ids)/sizeof(*ids);i++){uint32_t value=0;int rc=pcre2_pattern_info(code,ids[i],&value);printf("%s{\"query\":%u,\"returnCode\":%d,\"value\":",i?",":"",ids[i],rc);if(!rc)printf("%u",value);else printf("null");printf("}");}printf("]}\n");
 uint32_t count=0,stride=0;pcre2_pattern_info(code,PCRE2_INFO_NAMECOUNT,&count);pcre2_pattern_info(code,PCRE2_INFO_NAMEENTRYSIZE,&stride);PCRE2_SPTR table=NULL;pcre2_pattern_info(code,PCRE2_INFO_NAMETABLE,&table);
 begin("names");printf(",\"entries\":[");for(uint32_t i=0;i<count;i++){PCRE2_SPTR t=table+i*stride;
#if PCRE2_CODE_UNIT_WIDTH==8
 unsigned group=(t[0]<<8)|t[1];t+=2;
#else
 unsigned group=t[0];t++;
#endif
 printf("%s{\"number\":%u,\"units\":",i?",":"",group);units(t,zlen(t));printf("}");}printf("]");
 printf(",\"nameQuery\":");units(current->name,zlen(current->name));printf(",\"nameNumberReturn\":%d",pcre2_substring_number_from_name(code,current->name));PCRE2_SPTR first=NULL,last=NULL;int rc=pcre2_substring_nametable_scan(code,current->name,&first,&last);printf(",\"scanReturn\":%d,\"nameNumbers\":[",rc);if(rc>0){int comma=0;for(PCRE2_SPTR t=first;t<=last;t+=rc){
#if PCRE2_CODE_UNIT_WIDTH==8
 unsigned n=(t[0]<<8)|t[1];
#else
 unsigned n=t[0];
#endif
 printf("%s%u",comma?",":"",n);comma=1;}}printf("]}\n");pcre2_callout_enumerate(code,enumerate,NULL);
}
static void substring(pcre2_match_data *md,unsigned groups){
 for(unsigned g=0;g<groups+2;g++){
  PCRE2_SIZE n=0;int rc=pcre2_substring_length_bynumber(md,g,&n);
  begin("substring");printf(",\"group\":%u,\"lengthReturn\":%d,\"length\":",g,rc);if(rc)printf("null");else printf("%zu",n);
  PCRE2_UCHAR out[1024];size_t cap=current->capacity<1024?current->capacity:1024;for(size_t i=0;i<1024;i++)out[i]=0x55;
  int crc=pcre2_substring_copy_bynumber(md,g,out,&cap);printf(",\"copyReturn\":%d,\"reportedLength\":%zu,\"outputUnits\":",crc,cap);if(!crc)units(out,cap);else printf("[]");printf("}\n");
 }
 size_t n=0;int rc=pcre2_substring_length_byname(md,current->name,&n);PCRE2_UCHAR *s=NULL;size_t len=0;int gr=pcre2_substring_get_byname(md,current->name,&s,&len);
 begin("substringName");printf(",\"lengthReturn\":%d,\"length\":",rc);if(rc)printf("null");else printf("%zu",n);printf(",\"getReturn\":%d,\"units\":",gr);units(s,gr==0?len:0);printf("}\n");if(!gr)pcre2_substring_free(s);
}
static void result(pcre2_match_data *md,int rc,unsigned groups,unsigned iteration){
 size_t *v=pcre2_get_ovector_pointer(md);unsigned cap=pcre2_get_ovector_count(md);
 begin("match");printf(",\"iteration\":%u,\"returnCode\":%d,\"pairs\":",iteration,rc);
 if(rc>=0)pairs(v,groups+1<cap?groups+1:cap);else if(rc==PCRE2_ERROR_PARTIAL)pairs(v,1);else printf("[]");
 printf(",\"startChar\":");if(rc>=0||rc==-2||(rc<=-3&&rc>=-26))number(pcre2_get_startchar(md));else printf("null");printf(",\"errorOffset\":");if(rc<=-3&&rc>=-26)number(pcre2_get_startchar(md));else printf("null");printf(",\"markDefined\":%s,\"markUnits\":",(rc>=0||rc==-1||rc==-2)?"true":"false");if(rc>=0||rc==-1||rc==-2)mark(pcre2_get_mark(md));else printf("null");
 printf(",\"callerVector\":");if(rc==-2)printf("null");else pairs(v,cap);printf("}\n");
}
static void execute(const Case *c){
 current=c;callindex=subindex=caseindex=0;
 if(c->op==5){begin("boundary");printf(",\"reason\":\"Managed capability boundary has no native operation equivalence\"}\n");return;}
 pcre2_compile_context *cc=pcre2_compile_context_create(NULL);pcre2_set_newline(cc,c->newline);pcre2_set_bsr(cc,c->bsr);pcre2_set_compile_extra_options(cc,c->extra);pcre2_set_max_varlookbehind(cc,c->varlb);
 if(c->tables)pcre2_set_character_tables(cc,c->tables);
 const uint32_t opton[]={PCRE2_AUTO_POSSESS,PCRE2_DOTSTAR_ANCHOR,PCRE2_START_OPTIMIZE};
 const uint32_t optoff[]={PCRE2_AUTO_POSSESS_OFF,PCRE2_DOTSTAR_ANCHOR_OFF,PCRE2_START_OPTIMIZE_OFF};
 for(int i=0;i<3;i++)pcre2_set_optimize(cc,c->optimize[i]?opton[i]:optoff[i]);if(c->optimizer>=0)pcre2_set_optimize(cc,c->optimizer);
 int err=0;size_t offset=0;pcre2_code *code=pcre2_compile(c->pattern,c->pn,c->compile,&err,&offset,cc);
 begin("compile");printf(",\"accepted\":%s,\"error\":%d,\"nativeOffset\":%zu}\n",code?"true":"false",code?0:err,offset);pcre2_compile_context_free(cc);if(!code)return;
 metadata(code);if(c->op==1){pcre2_code_free(code);return;}
 uint32_t groups=0,options=0,newline=0;pcre2_pattern_info(code,PCRE2_INFO_CAPTURECOUNT,&groups);pcre2_pattern_info(code,PCRE2_INFO_ALLOPTIONS,&options);pcre2_pattern_info(code,PCRE2_INFO_NEWLINE,&newline);
 pcre2_match_context *mc=pcre2_match_context_create(NULL);if(c->work>=0)pcre2_set_match_limit(mc,(uint32_t)c->work);if(c->depth>=0)pcre2_set_depth_limit(mc,(uint32_t)c->depth);if(c->heap>=0)pcre2_set_heap_limit(mc,(uint32_t)c->heap);if(c->offset>=0)pcre2_set_offset_limit(mc,(size_t)c->offset);
 if(c->calln!=SIZE_MAX)pcre2_set_callout(mc,matchcall,NULL);if(c->subn!=SIZE_MAX)pcre2_set_substitute_callout(mc,subcall,NULL);if(c->casemode>=0)pcre2_set_substitute_case_callout(mc,casecall,NULL);
 pcre2_match_data *md=pcre2_match_data_create(c->pairs,NULL);if(!md)exit(4);size_t *v=pcre2_get_ovector_pointer(md);for(unsigned i=0;i<c->pairs*2;i++)v[i]=1234567;
 for(size_t i=0;i<c->initialn;i++)v[i]=c->initial[i];
 if(c->op==3){
  if(c->substitute&PCRE2_SUBSTITUTE_MATCHED){int rc=pcre2_match(code,c->subject,c->sn,c->start,c->match,md,mc);result(md,rc,groups,0);}
  PCRE2_UCHAR *out=malloc((c->capacity+1)*sizeof(*out));for(unsigned i=0;i<=c->capacity;i++)out[i]=0x55;size_t cap=c->capacity;
  int rc=pcre2_substitute(code,c->subject,c->sn,c->start,c->match|c->substitute,md,mc,c->replacement,c->rn,out,&cap);
  begin("substitute");printf(",\"returnCode\":%d,\"reportedLength\":",rc);number(cap);printf(",\"outputUnits\":");if(rc>=0)units(out,cap);else printf("[]");printf(",\"bufferUnits\":");units(out,c->capacity);printf("}\n");free(out);
 }else{
  size_t start=c->start;uint32_t flags=c->match;unsigned it=0;
  while(it<1024){int rc=pcre2_match(code,c->subject,c->sn,start,flags,md,mc);result(md,rc,groups,it);if(c->op!=2){if(rc>=0)substring(md,groups+1);break;}
   if(rc<0){if(rc!=-1||flags==c->match||start==c->sn)break;size_t next=start+1;
    if((newline==PCRE2_NEWLINE_CRLF||newline==PCRE2_NEWLINE_ANY||newline==PCRE2_NEWLINE_ANYCRLF)&&c->subject[start]==13&&next<c->sn&&c->subject[next]==10)next++;
    if(options&PCRE2_UTF){
#if PCRE2_CODE_UNIT_WIDTH==8
     while(next<c->sn&&(c->subject[next]&0xC0)==0x80)next++;
#else
     if(c->subject[start]>=0xD800&&c->subject[start]<=0xDBFF&&next<c->sn&&c->subject[next]>=0xDC00&&c->subject[next]<=0xDFFF)next++;
#endif
    }start=next;flags=c->match;continue;
   }
   size_t end=v[1];if(end<=start||v[0]>=end){flags=c->match|PCRE2_NOTEMPTY_ATSTART|PCRE2_ANCHORED;if(start==c->sn)break;}else{start=end;flags=c->match;}it++;
  }
  if(it==1024)exit(5);
 }
 pcre2_match_data_free(md);pcre2_match_context_free(mc);pcre2_code_free(code);
}
int main(void){setlocale(LC_ALL,"C");
 const uint8_t *t=pcre2_maketables(NULL);char name[32];snprintf(name,sizeof(name),"tables-%d.bin",PCRE2_CODE_UNIT_WIDTH);FILE *f=fopen(name,"wb");if(!f)return 2;fwrite(t,1,1088,f);fclose(f);pcre2_maketables_free(NULL,t);
 for(size_t i=0;i<sizeof(cases)/sizeof(*cases);i++)execute(&cases[i]);return 0;
}
