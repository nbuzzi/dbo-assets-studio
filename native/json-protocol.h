// Small bounded JSON protocol parser shared by the native helpers (world-host).
#pragma once
#include <string>
#include <map>
#include <vector>
#include <stdexcept>
#include <cmath>
#include <cstdlib>
#include <sstream>
#include <iomanip>
namespace labjson {
struct Value {
    enum Type {Null,Bool,Number,String,Array,Object} type=Null;
    bool boolean=false;double number=0;std::string string;
    std::vector<Value> array;std::map<std::string,Value> object;
    Value(){} Value(const char* s):type(String),string(s){} Value(std::string s):type(String),string(s){}
    Value(double n):type(Number),number(n){} Value(int n):type(Number),number(n){} Value(bool b):type(Bool),boolean(b){}
    static Value obj(){Value v;v.type=Object;return v;}static Value arr(){Value v;v.type=Array;return v;}
    Value& operator[](const std::string& key){if(type!=Object)throw std::runtime_error("JSON object required");return object[key];}
    const Value& at(const std::string& key)const{auto it=object.find(key);if(type!=Object||it==object.end())throw std::runtime_error("Missing JSON field: "+key);return it->second;}
    std::string text()const{if(type!=String)throw std::runtime_error("JSON string required");return string;}
    std::string text(const std::string& key,std::string fallback="")const{auto it=object.find(key);return it==object.end()?fallback:it->second.text();}
    double num(const std::string& key,double fallback=0)const{auto it=object.find(key);if(it==object.end())return fallback;if(it->second.type!=Number||!std::isfinite(it->second.number))throw std::runtime_error("Finite JSON number required");return it->second.number;}
    bool flag(const std::string& key,bool fallback=false)const{auto it=object.find(key);if(it==object.end())return fallback;if(it->second.type!=Bool)throw std::runtime_error("JSON boolean required");return it->second.boolean;}
    bool has(const std::string& key)const{return object.count(key)>0;}
    static std::string quote(const std::string& s){std::string out="\"";const char* hex="0123456789abcdef";for(unsigned char c:s){if(c=='"'||c=='\\'){out+='\\';out+=(char)c;}else if(c<32){out+="\\u00";out+=hex[c>>4];out+=hex[c&15];}else out+=(char)c;}return out+'"';}
    std::string dump()const{switch(type){case Null:return "null";case Bool:return boolean?"true":"false";case Number:{if(!std::isfinite(number))return "null";std::ostringstream s;s<<std::setprecision(15)<<number;return s.str();}case String:return quote(string);case Array:{std::string s="[";for(size_t i=0;i<array.size();++i){if(i)s+=',';s+=array[i].dump();}return s+"]";}case Object:{std::string s="{";bool first=true;for(const auto& item:object){if(!first)s+=',';first=false;s+=quote(item.first)+":"+item.second.dump();}return s+"}";}}return "null";}
};
class Parser {
    const std::string& s;size_t p=0,nodes=0;
    void ws(){while(p<s.size()&&(s[p]==' '||s[p]=='\r'||s[p]=='\n'||s[p]=='\t'))++p;}
    char next(){if(p>=s.size())throw std::runtime_error("Truncated JSON");return s[p++];}
    static int hex(char c){if(c>='0'&&c<='9')return c-'0';if(c>='a'&&c<='f')return c-'a'+10;if(c>='A'&&c<='F')return c-'A'+10;throw std::runtime_error("Invalid JSON Unicode escape");}
    std::string str(){if(next()!='"')throw std::runtime_error("JSON string expected");std::string out;for(;;){char c=next();if(c=='"')return out;if((unsigned char)c<32)throw std::runtime_error("JSON control byte");if(c=='\\'){c=next();switch(c){case '"':case '\\':case '/':out+=c;break;case 'b':out+='\b';break;case 'f':out+='\f';break;case 'n':out+='\n';break;case 'r':out+='\r';break;case 't':out+='\t';break;case 'u':{unsigned code=0;for(int i=0;i<4;++i)code=(code<<4)|hex(next());if(code>=0xd800&&code<=0xdfff)throw std::runtime_error("Surrogate paths unsupported in native preview");if(code<128)out+=(char)code;else if(code<2048){out+=(char)(0xc0|(code>>6));out+=(char)(0x80|(code&63));}else{out+=(char)(0xe0|(code>>12));out+=(char)(0x80|((code>>6)&63));out+=(char)(0x80|(code&63));}break;}default:throw std::runtime_error("Invalid JSON escape");}}else out+=c;if(out.size()>65536)throw std::runtime_error("JSON string bound");}}
    Value value(unsigned depth){ws();if(depth>32||++nodes>100000)throw std::runtime_error("JSON nesting/count bound");if(p>=s.size())throw std::runtime_error("JSON value expected");char c=s[p];if(c=='"')return Value(str());if(c=='{'||c=='['){++p;Value v=c=='{'?Value::obj():Value::arr();ws();if(p<s.size()&&s[p]==(c=='{'?'}':']')){++p;return v;}for(;;){if(c=='{'){ws();std::string key=str();ws();if(next()!=':'||v.object.count(key))throw std::runtime_error("JSON colon or duplicate key");v.object.emplace(key,value(depth+1));}else v.array.push_back(value(depth+1));ws();char close=next();if(close==(c=='{'?'}':']'))return v;if(close!=',')throw std::runtime_error("JSON comma expected");}}
        for(auto literal:{std::string("true"),std::string("false"),std::string("null")})if(s.compare(p,literal.size(),literal)==0){p+=literal.size();return literal=="null"?Value():Value(literal=="true");}
        size_t begin=p;if(c=='-')++p;if(p>=s.size()||s[p]<'0'||s[p]>'9')throw std::runtime_error("JSON number expected");if(s[p]=='0')++p;else while(p<s.size()&&s[p]>='0'&&s[p]<='9')++p;if(p<s.size()&&s[p]=='.'){++p;size_t digits=p;while(p<s.size()&&s[p]>='0'&&s[p]<='9')++p;if(p==digits)throw std::runtime_error("JSON fraction expected");}if(p<s.size()&&(s[p]=='e'||s[p]=='E')){++p;if(p<s.size()&&(s[p]=='+'||s[p]=='-'))++p;size_t digits=p;while(p<s.size()&&s[p]>='0'&&s[p]<='9')++p;if(p==digits)throw std::runtime_error("JSON exponent expected");}double n=std::strtod(s.substr(begin,p-begin).c_str(),nullptr);if(!std::isfinite(n))throw std::runtime_error("JSON non-finite number");return Value(n);
    }
public:explicit Parser(const std::string& text):s(text){}Value parse(){if(s.size()>8388608)throw std::runtime_error("JSON byte bound");Value v=value(0);ws();if(p!=s.size())throw std::runtime_error("Trailing JSON data");return v;}
};
inline Value parse(const std::string& s){return Parser(s).parse();}
}
