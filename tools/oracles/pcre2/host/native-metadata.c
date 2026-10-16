/* Finite metadata provenance probes using the retained pinned native libraries. */
#include <stdio.h>
#include "pcre2.h"
#include "metadata-cases.h"
int main(void){
 const unsigned queries[]={0,1,2,4,5,6,8,9,11,12,13,14,15,16,17,21,23,25,26};
 for(unsigned i=0;i<sizeof(inputs)/sizeof(*inputs);i++){
  int error;size_t offset; pcre2_code *code=pcre2_compile(inputs[i].pattern,inputs[i].length,inputs[i].flags,&error,&offset,NULL);
  if(!code){fprintf(stderr,"metadata%d error%d offset%zu\n",i,error,offset);return 2;}
  for(unsigned j=0;j<sizeof(queries)/sizeof(*queries);j++){
   unsigned value=0;int rc=pcre2_pattern_info(code,queries[j],&value);
   printf("{\"id\":\"%s\",\"width\":%d,\"key\":%u,\"returnCode\":%d,\"value\":%u}\n",inputs[i].id,PCRE2_CODE_UNIT_WIDTH,queries[j],rc,value);
  }
  pcre2_code_free(code);
 }
 return 0;
}
