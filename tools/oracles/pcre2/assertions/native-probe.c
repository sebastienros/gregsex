/* Independent bounded PCRE2 10.46 assertion probes. Development oracle only.
   Pinned source/library identities; never linked into GregsEx.dll. */
#include <stdio.h>
#include <string.h>
#include "pcre2.h"
typedef struct {const char *id,*pattern,*subject; unsigned compile,match; int start;} probe;
static const probe cases[]={
 {"ahead","(?=(a))a","a",0,0,0},
 {"negative","(?!(a))b","b",0,0,0},
 {"atomic-ahead","(?=(a{1,2}))\\1a\\z","aa",0,0,0},
 {"nonatomic-ahead","(?*(a{1,2}))\\1a\\z","aa",0,0,0},
 {"atomic-behind","(?<=(a{1,2}))\\1\\z","aaa",0,0,2},
 {"nonatomic-behind","(?<*(a{1,2}))\\1\\z","aaa",0,0,2},
 {"variable-behind","(?<=(a{1,2}))b","aab",0,0,2},
 {"fixed-branches","(?<=(a)|(aa))b","aab",0,0,2},
 {"negative-behind","(?<!(a{1,2})x)b","aab",0,0,2},
 {"nested-ahead","(?<=a(?=b))b","ab",0,0,1},
 {"behind-true-end","(?<=a\\z)b","ab",0,0,1},
 {"scan-capture","(.{1,2})(*scs:(1)(a)b\\z)","ab",0,0,0},
 {"scan-no-bump","(.{1,2})(*scs:(1)(a)b\\z)","ba",0,0,0},
 {"scan-retry","(aa)(*scs:(1)(a{1,2}))\\2\\z","aaa",0,0,0},
 {"scan-end","a(b)(*scs:(1)(?<=a)b$\\z)c","abc",0,PCRE2_NOTEOL,0},
 {"scan-beginning","a(b)(*scs:(1)\\Ab)","ab",0,0,0},
 {"scan-overwrite","(aa)(*scs:(1)(a{1,2}))(*scs:(2)a\\z)\\2","aaa",0,0,0},
 {"scan-nesting","(ab)(*scs:(1)(a)(*scs:(2)a\\z)b\\z)c\\z","abc",0,0,0},
 {"scan-noteol-restore","(a)(*scs:(1)a$)$","a",0,PCRE2_NOTEOL,0},
 {"duplicate-first","(?<n>a)?(?<n>b)\\k<n>","aba",PCRE2_DUPNAMES,0,0},
 {"duplicate-not-second","(?<n>a)?(?<n>b)\\k<n>","abb",PCRE2_DUPNAMES,0,0},
 {"unset-default","(a)?\\1b","b",0,0,0},
 {"unset-enabled","(a)?\\1b","b",PCRE2_MATCH_UNSET_BACKREF,0,0},
 {"open-prior-capture","(a(?:\\1)?){2}","aaa",0,0,0},
 {"partial-prefix","(ab)\\1","aba",0,PCRE2_PARTIAL_HARD,0},
 {"partial-mismatch","(ab)\\1","abx",0,PCRE2_PARTIAL_HARD,0},
 {"negative-predicate","(?(?!(a))b|\\1)","a",0,0,0},
 {"reset-positive","(?=(a\\K))a","a",0,0,0},
 {"reset-positive-predicate","(?(?=(a\\K))\\1|b)","a",0,0,0},
 {"reset-negative-predicate","(?(?!(a\\K))b|\\1)","a",0,0,0},
 {"last-positive-predicate","(?(?=(a))\\1|b)(?C)","a",0,0,0},
 {"last-negative-predicate","(?(?!(a))b|\\1)(?C)","a",0,0,0},
 {"last-positive-ahead","(?=(a))a(?C)","a",0,0,0},
 {"last-scan","(a)(*scs:(1)(a))(?C)","a",0,0,0}
};
static int callout(pcre2_callout_block *block,void *data){ *(int *)data=(int)block->capture_last; return 0; }
static int observe(const char *id, PCRE2_SPTR pat, PCRE2_SIZE pn, PCRE2_SPTR subject, PCRE2_SIZE sn,
 unsigned flags,unsigned match,int start,int tables){
 int error; PCRE2_SIZE offset; pcre2_compile_context *cc=pcre2_compile_context_create(NULL);
 if(strncmp(id,"reset-",6)==0) pcre2_set_compile_extra_options(cc,PCRE2_EXTRA_ALLOW_LOOKAROUND_BSK);
 unsigned char custom[1088];
 if(tables){ const unsigned char *original=pcre2_maketables(NULL); memcpy(custom,original,1088); pcre2_maketables_free(NULL,original); custom['a']=custom['b']=1; pcre2_set_character_tables(cc,custom); }
 pcre2_code *code=pcre2_compile(pat,pn,flags,&error,&offset,cc); pcre2_compile_context_free(cc);
 if(!code){fprintf(stderr,"%s compile error %d at%zu\n",id,error,offset);return 1;}
 pcre2_match_data *md=pcre2_match_data_create(16,NULL);
 int last=-1; pcre2_match_context *mc=pcre2_match_context_create(NULL);pcre2_set_callout(mc,callout,&last);
 int rc=pcre2_match(code,subject,sn,start,match|PCRE2_ANCHORED,md,mc);
 uint32_t count; pcre2_pattern_info(code,PCRE2_INFO_CAPTURECOUNT,&count);
 printf("{\"id\":\"%s\",\"width\":%d,\"rc\":%d,\"pairs\":[",id,PCRE2_CODE_UNIT_WIDTH,rc);
 if(rc>=0||rc==PCRE2_ERROR_PARTIAL){PCRE2_SIZE *v=pcre2_get_ovector_pointer(md); unsigned last=rc==PCRE2_ERROR_PARTIAL?0:count;
 for(unsigned g=0;g<=last;g++){if(g)putchar(',');if(v[2*g]==PCRE2_UNSET)printf("null");else printf("[%zu,%zu]",v[2*g],v[2*g+1]);}}
 printf("]");if(strncmp(id,"last-",5)==0)printf(",\"captureLast\":%d",last);puts("}");pcre2_match_context_free(mc); pcre2_match_data_free(md);pcre2_code_free(code);return 0;
}
static int unicode(const char *id,unsigned utf,unsigned ucp){
 const char *pattern="(\\x{23a})(?i:\\1)"; PCRE2_UCHAR p[64],s[8]; int pn=0,sn=0;
 while(pattern[pn]){p[pn]=(PCRE2_UCHAR)pattern[pn];pn++;}
 #if PCRE2_CODE_UNIT_WIDTH==8
 if(!utf)return 0;
 s[sn++]=0xc8;s[sn++]=0xba;s[sn++]=0xe2;s[sn++]=0xb1;s[sn++]=0xa5;
 #else
 s[sn++]=0x23a;s[sn++]=0x2c65;
 #endif
 return observe(id,p,pn,s,sn,(utf?PCRE2_UTF:0)|(ucp?PCRE2_UCP:0),0,0,0);
}
int main(int argc,char **argv){
 for(unsigned i=0;i<sizeof(cases)/sizeof(cases[0]);i++){
 const probe *c=cases+i;PCRE2_UCHAR p[160],s[160];int pn=0,sn=0;
 if(argc>1&&strcmp(argv[1],"baseline")==0){if(strncmp(c->id,"reset-",6)==0||strncmp(c->id,"last-",5)==0)continue;}
 else if(argc>1&&strncmp(c->id,argv[1],strlen(argv[1]))!=0)continue;
 while(c->pattern[pn]){p[pn]=(PCRE2_UCHAR)c->pattern[pn];pn++;}
 while(c->subject[sn]){s[sn]=(PCRE2_UCHAR)c->subject[sn];sn++;}
 if(observe(c->id,p,pn,s,sn,c->compile,c->match,c->start,0))return 1;
 }
 if(argc>1&&strcmp(argv[1],"baseline")!=0)return 0;
 if(unicode("case-utf",1,0)||unicode("case-raw",0,0)||unicode("case-ucp",0,1))return 1;
 for(int ucp=0;ucp<2;ucp++){
 const char *pat="(a)(?i:\\1)";PCRE2_UCHAR p[32],s[2]={'a','b'};int n=0;while(pat[n]){p[n]=(PCRE2_UCHAR)pat[n];n++;}
 if(observe(ucp?"table-ucp":"table-raw",p,n,s,2,ucp?PCRE2_UCP:0,0,0,1))return 1;
 }
 return 0;
}
