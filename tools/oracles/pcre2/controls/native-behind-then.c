#include <stdio.h>
#include <string.h>
#include "pcre2.h"
int main(void)
{
  const char *pattern = "(?<=a(*THEN)b|ac)x";
  PCRE2_UCHAR units[64], subject[] = {97,99,120};
  size_t length = strlen(pattern); int error; PCRE2_SIZE offset;
  for (size_t i=0;i<length;i++) units[i]=(PCRE2_UCHAR)pattern[i];
  pcre2_code *code=pcre2_compile(units,length,PCRE2_NO_START_OPTIMIZE|PCRE2_NO_AUTO_POSSESS,&error,&offset,NULL);
  if (!code) { printf("{\"compileError\":%d,\"offset\":%zu}\n",error,offset);return 1; }
  pcre2_match_data *data=pcre2_match_data_create_from_pattern(code,NULL);
  int rc=pcre2_match(code,subject,3,0,0,data,NULL);
  printf("{\"width\":%d,\"returnCode\":%d",PCRE2_CODE_UNIT_WIDTH,rc);
  if(rc>=0){PCRE2_SIZE *v=pcre2_get_ovector_pointer(data);printf(",\"start\":%zu,\"end\":%zu",v[0],v[1]);}
  printf("}\n");pcre2_match_data_free(data);pcre2_code_free(code);return 0;
}
