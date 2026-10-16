#define PCRE2_CODE_UNIT_WIDTH 8
#include "pcre2.h"
#include <stdio.h>
int main(void) {
{ PCRE2_UCHAR p[]={92,88}, s[]={97,204,129,98}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,4,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-combine-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={240,159,145,169,204,129,226,128,141,240,159,146,187,88}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,14,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-zwj-positive-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={97,226,128,141,226,152,128}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,7,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-zwj-negative-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={240,159,135,166,240,159,135,167,240,159,135,168}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,12,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-ri-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={225,132,128,225,133,161,225,134,168,88}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,10,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-hangul-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={0,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-nul-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,1,0,PCRE2_ANCHORED|PCRE2_PARTIAL_HARD,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-hard-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={97,204,129}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-positive-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={97,206,177}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-negative-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={205,184}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-unknown-one-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={205,184,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-unknown-two-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={227,131,188,227,129,130}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,6,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-extensions-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={227,131,188,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,4,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-extensions-negative-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={230,188,162,234,176,128,227,129,130}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,9,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-han-hangul-hira-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={234,176,128,230,188,162,227,129,130}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,9,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-hangul-han-hira-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={217,161,219,178}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,4,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-digits-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={240,157,159,142,240,157,159,152}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,8,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-math-digits-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={240,144,144,128,240,144,144,168}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,8,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-wide-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,97,92,75,92,120,123,51,98,49,125,41}, s[]={97,206,177}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-k-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,40,97,123,49,44,50,125,41,41,97,92,122}, s[]={97,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-retry-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,97,115,114,58,40,97,123,49,44,50,125,41,41,97,92,122}, s[]={97,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-atomic-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={13,10,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-raw-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={97,0}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-raw-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={240,159,135,166,240,159,135,167,240,159,135,168}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,12,4,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-ri-start1-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={240,159,135,166,240,159,135,167,240,159,135,168}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,12,8,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-ri-start2-8 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
return 0;}
