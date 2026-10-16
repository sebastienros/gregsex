#include <stdio.h>
#include "pcre2.h"
#include "native-cases.h"
int main(void) {
 const unsigned keys[]={0,1,2,4,5,6,11,12,13,14,16,21,25};
 for(unsigned i=0;i<sizeof(cases)/sizeof(*cases);i++) {
  int error;PCRE2_SIZE off;pcre2_code *re=pcre2_compile(cases[i].pattern, cases[i].length,cases[i].flags,&error,&off,NULL);
  if(!re){fprintf(stderr,"case%u error%d@%zu\n",i,error,off);return 2;}
  printf("{\"id\":%u,\"domain\":%u,\"info\":[",i,cases[i].domain);
  for(unsigned j=0;j<sizeof(keys)/sizeof(*keys);j++){unsigned v=0;int rc=pcre2_pattern_info(re,keys[j],&v);printf("%s[%u,%d,%u]",j?",":"",keys[j],rc,v);}
  pcre2_match_data *data=pcre2_match_data_create_from_pattern(re,NULL);
  int rc=pcre2_match(re,cases[i].subject,cases[i].subject_length,0,0,data,NULL);
  printf("],\"returnCode\":%d,\"pairs\":[",rc);
  if(rc>0){PCRE2_SIZE *pairs=pcre2_get_ovector_pointer(data);unsigned n=pcre2_get_ovector_count(data);
   for(unsigned j=0;j<n;j++){if(j)printf(",");if(pairs[j*2]==PCRE2_UNSET)printf("null");else printf("[%zu,%zu]",pairs[j*2],pairs[j*2+1]);}}
  printf("]}\n");pcre2_match_data_free(data);pcre2_code_free(re);
 }
 return 0;
}
