#define PCRE2_CODE_UNIT_WIDTH 16
#include "pcre2.h"
#include <stdio.h>
int main(void) {
{ PCRE2_UCHAR p[]={92,88}, s[]={97,769,98}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-combine-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={55357,56425,769,8205,55357,56507,88}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,7,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-zwj-positive-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={97,8205,9728}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-zwj-negative-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={55356,56806,55356,56807,55356,56808}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,6,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-ri-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={4352,4449,4520,88}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,4,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-hangul-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={0,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-nul-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,1,0,PCRE2_ANCHORED|PCRE2_PARTIAL_HARD,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-hard-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={97,769}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-positive-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={97,945}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-negative-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={888}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,1,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-unknown-one-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={888,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-unknown-two-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={12540,12354}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-extensions-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={12540,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-extensions-negative-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={28450,44032,12354}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-han-hangul-hira-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={44032,28450,12354}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-hangul-han-hira-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={1633,1778}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-digits-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={55349,57294,55349,57304}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,4,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-math-digits-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={55297,56320,55297,56360}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,4,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-wide-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,97,92,75,92,120,123,51,98,49,125,41}, s[]={97,945}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-k-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,40,97,123,49,44,50,125,41,41,97,92,122}, s[]={97,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-retry-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,97,115,114,58,40,97,123,49,44,50,125,41,41,97,92,122}, s[]={97,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-atomic-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={13,10,97}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,3,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-raw-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={97,0}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-raw-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={40,42,115,114,58,46,42,41,92,122}, s[]={97,55296}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,2,0,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("s-raw-unknown-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={55356,56806,55356,56807,55356,56808}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,6,2,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-ri-start1-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
{ PCRE2_UCHAR p[]={92,88}, s[]={55356,56806,55356,56807,55356,56808}; int err; PCRE2_SIZE off;
pcre2_code *r=pcre2_compile(p,sizeof(p)/sizeof(*p),PCRE2_DOTALL|PCRE2_NO_AUTO_POSSESS|PCRE2_UTF,&err,&off,NULL); if(!r) return 2;
pcre2_match_data *d=pcre2_match_data_create_from_pattern(r,NULL);int rc=pcre2_match(r,s,6,4,PCRE2_ANCHORED,d,NULL);PCRE2_SIZE *v=pcre2_get_ovector_pointer(d);
printf("g-ri-start2-16 %d",rc);if(rc>0||rc==PCRE2_ERROR_PARTIAL)for(int i=0;i<(rc>0?rc:1)*2;i++)printf(" %zu",v[i]);printf("\n");pcre2_match_data_free(d);pcre2_code_free(r);}
return 0;}
