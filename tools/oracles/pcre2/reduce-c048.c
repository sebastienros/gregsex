/* Exact native-only frozen C048 invocation reduction. */
#include <stdio.h>
#include "pcre2.h"
int main(void){
 const PCRE2_UCHAR pattern[]={40,97,41},subject[]={97},replacement[]={36,49,36,49};
 int error;size_t offset;pcre2_code *code=pcre2_compile(pattern,3,0,&error,&offset,NULL);if(!code)return 1;
 for(int count=0;count<=2;count++){
  pcre2_match_data *md=count?pcre2_match_data_create(count,NULL):NULL;
  PCRE2_UCHAR output[128];for(int i=0;i<128;i++)output[i]=85;size_t length=128;
  int rc=pcre2_substitute(code,subject,1,0,0,md,NULL,replacement,4,output,&length);
  printf("{\"width\":%d,\"matchData\":\"%s\",\"pairCapacity\":%d,\"returnCode\":%d,\"reportedLength\":%zu,\"outputUnits\":[",PCRE2_CODE_UNIT_WIDTH,count?"explicit":"NULL-auto-full",count,rc,length);
  if(rc>=0)for(size_t i=0;i<length;i++)printf("%s%u",i?",":"",output[i]);printf("]}\n");
  if(md)pcre2_match_data_free(md);
 }
 pcre2_code_free(code);return 0;
}
