/* Original GregsEx development helper, MIT. Never linked into GregsEx. */
#define _POSIX_C_SOURCE 200809L
#include <regex.h>
#include <locale.h>
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <unistd.h>
#include <sys/resource.h>
#ifndef ORACLE_ID
#define ORACLE_ID "unidentified"
#endif
static const char *code(int e) {
 switch(e) {
 case 0:return "None"; case REG_NOMATCH:return "NoMatch";
 case REG_BADPAT:return "BadPattern"; case REG_ECOLLATE:return "InvalidCollatingElement";
 case REG_ECTYPE:return "InvalidCharacterClass"; case REG_EESCAPE:return "TrailingEscape";
 case REG_ESUBREG:return "InvalidBackreference"; case REG_EBRACK:return "UnbalancedBracket";
 case REG_EPAREN:return "UnbalancedParenthesis"; case REG_EBRACE:return "UnbalancedBrace";
 case REG_BADBR:return "BadInterval"; case REG_ERANGE:return "InvalidRange";
 case REG_ESPACE:return "OutOfMemory"; case REG_BADRPT:return "BadRepeat";
 default:return "OtherNativeError";
 }
}
static int hexval(char c) { return c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:-1; }
static size_t decode(const char *p, unsigned char *out, size_t cap) {
 if (!strcmp(p,"-")) {out[0]=0;return 0;}
 size_t n=strlen(p); if(n%2||n/2>=cap)exit(65);
 for(size_t i=0;i<n/2;i++){int a=hexval(p[2*i]),b=hexval(p[2*i+1]);if(a<0||b<0)exit(65);out[i]=(a<<4)|b;if(!out[i])exit(65);}
 out[n/2]=0;return n/2;
}
static void table(void) {
 printf("{\"kind\":\"locale\",\"rows\":[");
 for(int b=0;b<256;b++) {
 unsigned mask=(isalnum(b)?1:0)|(isalpha(b)?2:0)|(isblank(b)?4:0)|(iscntrl(b)?8:0)|
 (isdigit(b)?16:0)|(isgraph(b)?32:0)|(islower(b)?64:0)|(isprint(b)?128:0)|
 (ispunct(b)?256:0)|(isspace(b)?512:0)|(isupper(b)?1024:0)|(isxdigit(b)?2048:0);
 printf("%s[%d,%u,%d]",b?",":"",b,mask,tolower(b));
 } puts("]}");
}
int main(void) {
 if(!setlocale(LC_ALL,"C")) return 66;
 struct rlimit mem={268435456,268435456}; if(setrlimit(RLIMIT_AS,&mem))return 67;
 printf("{\"kind\":\"identity\",\"oracle\":\"%s\",\"locale\":\"%s\",\"regExtended\":%d,\"regIcase\":%d,\"regNewline\":%d,\"regNosub\":%d,\"regNotbol\":%d,\"regNoteol\":%d}\n",ORACLE_ID,setlocale(LC_ALL,NULL),REG_EXTENDED,REG_ICASE,REG_NEWLINE,REG_NOSUB,REG_NOTBOL,REG_NOTEOL);
 table();
 char line[32768],id[128],phex[8193],shex[16385],op[16]; unsigned char p[4097],s[8193];
 while(fgets(line,sizeof line,stdin)) {
 int ere,cf,ef,nmatch;
 if(sscanf(line,"%127s %d %d %d %d %15s %8192s %16384s",id,&ere,&cf,&ef,&nmatch,op,phex,shex)!=8||
 (ere!=0&&ere!=1)||cf<0||cf>7||ef<0||ef>3||nmatch<0||nmatch>64) return 65;
 decode(phex,p,sizeof p); decode(shex,s,sizeof s);
 alarm(2); regex_t re; memset(&re,0,sizeof re);
 int flags=(ere?REG_EXTENDED:0)|((cf&1)?REG_ICASE:0)|((cf&2)?REG_NEWLINE:0)|((cf&4)?REG_NOSUB:0);
 int c=regcomp(&re,(char*)p,flags); int x=-1;regmatch_t m[64];
 for(int i=0;i<64;i++){m[i].rm_so=-777;m[i].rm_eo=-888;}
 size_t groups=c?0:re.re_nsub+1;
 if(!c&&!strcmp(op,"Exec")) x=regexec(&re,(char*)s,(size_t)nmatch,m,((ef&1)?REG_NOTBOL:0)|((ef&2)?REG_NOTEOL:0));
 char msg[256];memset(msg,165,sizeof msg);size_t required=regerror(c?c:(x>0?x:REG_BADPAT),c?NULL:&re,msg,sizeof msg);
 /* Probe truncation/termination independently of unspecified English wording. */
 char shortmsg[3]={-91,-91,-91};size_t shortrequired=regerror(c?c:(x>0?x:REG_BADPAT),c?NULL:&re,shortmsg,2);
 printf("{\"kind\":\"result\",\"id\":\"%s\",\"compile\":\"%s\",\"groupCount\":%zu,\"status\":\"%s\",\"matches\":[",id,code(c),groups,c?"CompileError":x==-1?"ParseSuccess":x==0?"Match":x==REG_NOMATCH?"NoMatch":"ExecutionError");
 for(int i=0;i<nmatch;i++)printf("%s[%jd,%jd]",i?",":"",(intmax_t)m[i].rm_so,(intmax_t)m[i].rm_eo);
 printf("],\"errorCode\":\"%s\",\"regerrorRequired\":%zu,\"regerrorShortRequired\":%zu,\"regerrorTerminated\":%s,\"regerrorTailUntouched\":%s}\n",code(c?c:(x>0?x:0)),required,shortrequired,shortmsg[1]==0?"true":"false",(unsigned char)shortmsg[2]==165?"true":"false");
 if(!c) { regfree(&re); }
 alarm(0);fflush(stdout);
 } return ferror(stdin)?68:0;
}
