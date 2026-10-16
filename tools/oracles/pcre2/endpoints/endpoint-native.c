/* Focused independent PCRE2 10.46 endpoint probe. Pinned retained libraries.
   Development oracle only; never linked to GregsEx. */
#include <stdio.h>
#include "pcre2.h"
int main(void) {
  const char *patterns[] = {"a", "a\\z", "a\\Z", "a$", "(a)(*scs:(1)a\\z)"};
  for (int k=0;k<5;k++) {
    PCRE2_UCHAR pattern[40],subject[3];
    int n=0; while(patterns[k][n]) { pattern[n]=(PCRE2_UCHAR)patterns[k][n]; n++; }
    subject[0]='a'; subject[1]=k==4?'b':PCRE2_CODE_UNIT_WIDTH==8?0xff:0xd800; subject[2]='b';
    int error; PCRE2_SIZE offset;
    pcre2_code *code=pcre2_compile(pattern,n,PCRE2_MATCH_INVALID_UTF,&error,&offset,NULL);
    if(!code) { fprintf(stderr,"compile error %d\n",error); return 1; }
    pcre2_match_data *md=pcre2_match_data_create(4,NULL);
    int rc=pcre2_match(code,subject,k==4?2:3,0,k==0?PCRE2_ENDANCHORED:0,md,NULL);
    printf("{\"width\":%d,\"case\":%d,\"rc\":%d",PCRE2_CODE_UNIT_WIDTH,k,rc);
    if(rc>=0) { PCRE2_SIZE *v=pcre2_get_ovector_pointer(md); printf(",\"start\":%zu,\"end\":%zu,\"startChar\":%zu",v[0],v[1],pcre2_get_startchar(md)); }
    puts("}"); pcre2_match_data_free(md); pcre2_code_free(code);
  }
  return 0;
}
