using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

public class PluginEU
{
    static readonly object Sync=new object();
    static FileSystemWatcher _watcher; static System.Threading.Timer _timer; static bool _started; static int _processing;
    static DateTime _lastEvent=DateTime.MinValue,_lastSeenWrite=DateTime.MinValue;
    static readonly string Dir=@"C:\ImporterEU",Pdf=@"C:\ImporterEU\Faktura.pdf",Log=@"C:\ImporterEU\FakturyAuto.log",Sent=@"C:\ImporterEU\FakturyAuto_sent_v12.txt";
    public static string WersjaEU{get{StartOnce();return "3.44.0";}}
    [DisplayName("Faktury Allegro AUTO - status v12")]
    public static string[] T_FakturyAuto_status(object z){StartOnce();MessageBox.Show("FakturyAuto v12 działa.\r\nToken jest weryfikowany na właściwym zamówieniu przed wysłaniem PDF.\r\nLog: "+Log,"ELEKTROMET - FakturyAuto v12");return new[]{"UPDATE TRANSAKCJE SET ID=ID WHERE 1=0"};}
    [DisplayName("Faktury Allegro AUTO - test v12")]
    public static string[] T_FakturyAuto_test(object z){StartOnce();ThreadPool.QueueUserWorkItem(_=>ProcessPdf("TEST RĘCZNY v12"));return new[]{"UPDATE TRANSAKCJE SET ID=ID WHERE 1=0"};}
    static void StartOnce(){lock(Sync){if(_started)return;_started=true;try{Directory.CreateDirectory(Dir);WriteLog("START v12 VERIFIED RAW PUT. BaseDir="+AppDomain.CurrentDomain.BaseDirectory);_watcher=new FileSystemWatcher(Dir,"Faktura.pdf");_watcher.NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.CreationTime|NotifyFilters.Size|NotifyFilters.FileName;_watcher.Changed+=OnPdf;_watcher.Created+=OnPdf;_watcher.Renamed+=OnPdfRenamed;_watcher.EnableRaisingEvents=true;_timer=new System.Threading.Timer(_=>Poll(),null,5000,5000);}catch(Exception ex){WriteLog("BŁĄD START: "+ex);}}}
    static void OnPdf(object s,FileSystemEventArgs e){_lastEvent=DateTime.Now;ThreadPool.QueueUserWorkItem(_=>{Thread.Sleep(2500);ProcessPdf("WATCHER "+e.ChangeType);});}
    static void OnPdfRenamed(object s,RenamedEventArgs e){_lastEvent=DateTime.Now;ThreadPool.QueueUserWorkItem(_=>{Thread.Sleep(2500);ProcessPdf("WATCHER Renamed");});}
    static void Poll(){try{if(!File.Exists(Pdf))return;var t=File.GetLastWriteTime(Pdf);if(t>_lastSeenWrite&&(DateTime.Now-t).TotalMinutes<15){_lastSeenWrite=t;if((DateTime.Now-_lastEvent).TotalSeconds>4)ProcessPdf("POLL v12");}}catch{}}
    static void ProcessPdf(string reason){if(Interlocked.Exchange(ref _processing,1)==1)return;try{if(!File.Exists(Pdf)||!WaitReady(Pdf,20))return;var fi=new FileInfo(Pdf);if(fi.Length<1000)return;if(fi.Length>3*1024*1024){WriteLog("STOP: PDF większy niż 3 MB");return;}var xmlPath=FindMatchingXml(fi.LastWriteTime);if(xmlPath==null){WriteLog("STOP: brak pasującego XML");return;}var xml=File.ReadAllText(xmlPath,Encoding.UTF8);string account=GetText(xml,"Konto"),checkout=GetText(xml,"AllFodId");byte[] pdf=File.ReadAllBytes(Pdf);string key=checkout+"|"+Hash(pdf);WriteLog("NOWY PDF v12: "+fi.Length+" B | XML="+Path.GetFileName(xmlPath)+" | checkout="+checkout+" | konto="+account+" | "+reason);if(String.IsNullOrWhiteSpace(checkout)||String.IsNullOrWhiteSpace(account)){WriteLog("STOP: brak checkout/konta");return;}if(AlreadySent(key)){WriteLog("POMINIĘTO duplikat: "+checkout);return;}Upload(account,checkout,pdf);MarkSent(key);WriteLog("SUKCES v12: PDF faktury wysłany do Allegro. checkout="+checkout);}catch(TargetInvocationException tie){WriteLog("BŁĄD v12: "+(tie.InnerException??tie));}catch(Exception ex){WriteLog("BŁĄD PROCESS v12: "+ex);}finally{Interlocked.Exchange(ref _processing,0);}}

    static void Upload(string account,string checkout,byte[] pdf){
        Assembly a=EuAsm(); if(a==null)throw new Exception("Brak EasyUploader assembly");
        Type ct=a.GetType("EasyUploader.Features.Markety.Clients.AllegroClient"); if(ct==null)throw new Exception("Brak AllegroClient");
        object client=Activator.CreateInstance(ct,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{account,false},null);
        WriteLog("AllegroClient utworzony dla konta "+account);
        var validate=ct.GetMethod("ValidateConnection",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic); if(validate!=null)validate.Invoke(client,null);
        string api=GetStringProp(client,"ApiUrl"); if(String.IsNullOrWhiteSpace(api))api="https://api.allegro.pl"; api=api.TrimEnd('/');

        // Nie ufamy już żadnemu przypadkowemu stringowi. Kandydat musi być JWT i musi otrzymać 200 dla TEGO zamówienia.
        string token=FindAndVerifyJwt(client,api,checkout);
        if(String.IsNullOrWhiteSpace(token))throw new Exception("Nie znaleziono tokenu EasyUploader, który przechodzi autoryzowany GET dla tego zamówienia");
        WriteLog("TOKEN ZWERYFIKOWANY na checkout: długość="+token.Length+" segmenty=3");

        // POST metadanych nadal przez natywny AllegroClient — ten etap działał już w v8-v10.
        MethodInfo send=ct.GetMethod("CreateAndSendRequest",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic); if(send==null)throw new Exception("Brak CreateAndSendRequest");
        string path="/order/checkout-forms/"+checkout+"/invoices";
        var body=new Dictionary<string,object>{{"file",new Dictionary<string,object>{{"name","Faktura.pdf"}}}};
        WriteLog("POST v12 metadata -> "+path+" | file.name=Faktura.pdf");
        object resp=send.Invoke(client,new object[]{"POST",path,body,null,"application/vnd.allegro.public.v1+json"});
        string txt=ResponseText(resp); WriteLog("POST response: "+Short(txt));
        string invId=ExtractIdFromText(txt); if(String.IsNullOrWhiteSpace(invId))throw new Exception("Brak invoiceId po POST: "+Short(txt));
        WriteLog("invoiceId="+invId);

        // Dokładnie wg aktualnej dokumentacji Allegro: PUT raw binary, Content-Type application/pdf, Accept public.v1+json.
        string fp=api+"/order/checkout-forms/"+checkout+"/invoices/"+invId+"/file";
        RawPut(fp,token,pdf);
    }

    static string FindAndVerifyJwt(object client,string api,string checkout){
        var seen=new HashSet<object>(new RefCmp()); var candidates=new List<string>(); CollectStrings(client,0,seen,candidates);
        var unique=new List<string>();
        foreach(var raw in candidates){string s=NormalizeToken(raw); if(IsJwt(s)&&!unique.Contains(s))unique.Add(s);}
        WriteLog("Kandydaci JWT znalezieni: "+unique.Count);
        int i=0; foreach(var token in unique){i++; int code=VerifyToken(api,checkout,token); WriteLog("Weryfikacja JWT #"+i+": HTTP "+code); if(code==200)return token;}
        DumpOAuthShape(client); return null;
    }
    static int VerifyToken(string api,string checkout,string token){
        string url=api+"/order/checkout-forms/"+checkout; var req=(HttpWebRequest)WebRequest.Create(url); req.Method="GET"; req.Accept="application/vnd.allegro.public.v1+json"; req.Headers[HttpRequestHeader.Authorization]="Bearer "+token; req.Timeout=15000; req.ReadWriteTimeout=15000;
        try{using(var r=(HttpWebResponse)req.GetResponse()){return (int)r.StatusCode;}}catch(WebException we){var r=we.Response as HttpWebResponse; return r==null?0:(int)r.StatusCode;}
    }
    static string NormalizeToken(string s){s=(s??"").Trim();if(s.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))s=s.Substring(7).Trim();int nl=s.IndexOfAny(new[]{'\r','\n',',',' '});if(nl>0)s=s.Substring(0,nl).Trim();return s;}
    static void CollectStrings(object o,int depth,HashSet<object> seen,List<string> found){if(o==null||depth>5)return;if(o is string){found.Add((string)o);return;}Type t=o.GetType();if(t.IsPrimitive||t.IsEnum||t==typeof(DateTime)||t==typeof(decimal))return;if(!t.IsValueType){if(seen.Contains(o))return;seen.Add(o);}foreach(var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)){if(p.GetIndexParameters().Length>0)continue;string n=p.Name.ToLowerInvariant();if(depth==0&&!(n.Contains("oauth")||n.Contains("token")||n.Contains("dane")||n.Contains("konto")))continue;try{var v=p.GetValue(o,null);if(v!=null)CollectStrings(v,depth+1,seen,found);}catch{}}foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)){string n=f.Name.ToLowerInvariant();if(depth==0&&!(n.Contains("oauth")||n.Contains("token")||n.Contains("dane")||n.Contains("konto")))continue;try{var v=f.GetValue(o);if(v!=null)CollectStrings(v,depth+1,seen,found);}catch{}}}
    static bool IsJwt(string s){if(String.IsNullOrWhiteSpace(s)||s.Length<80)return false;var a=s.Split('.');return a.Length==3&&a[0].StartsWith("eyJ",StringComparison.Ordinal)&&a[1].Length>10&&a[2].Length>10;}
    static void DumpOAuthShape(object client){try{var sb=new StringBuilder();sb.AppendLine("DIAGNOSTYKA v12 - bez wartości tokenów");DumpShape(client,"AllegroClient",sb,0,new HashSet<object>(new RefCmp()));File.WriteAllText(Path.Combine(Dir,"FakturyAuto_v12_OAuth_DIAG.txt"),sb.ToString(),Encoding.UTF8);}catch{}}
    static void DumpShape(object o,string path,StringBuilder sb,int depth,HashSet<object> seen){if(o==null||depth>3)return;Type t=o.GetType();if(!t.IsValueType){if(seen.Contains(o))return;seen.Add(o);}sb.AppendLine(path+" TYPE="+t.FullName);foreach(var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)){if(p.GetIndexParameters().Length>0)continue;try{var v=p.GetValue(o,null);string n=p.Name.ToLowerInvariant();if(v is string){string s=(string)v;sb.AppendLine(path+".PROP "+p.Name+" string len="+s.Length+" jwt="+IsJwt(NormalizeToken(s)));}else if(v!=null&&(n.Contains("oauth")||n.Contains("token")))DumpShape(v,path+"."+p.Name,sb,depth+1,seen);}catch{}}foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)){try{var v=f.GetValue(o);string n=f.Name.ToLowerInvariant();if(v is string){string s=(string)v;sb.AppendLine(path+".FIELD "+f.Name+" string len="+s.Length+" jwt="+IsJwt(NormalizeToken(s)));}else if(v!=null&&(n.Contains("oauth")||n.Contains("token")))DumpShape(v,path+"."+f.Name,sb,depth+1,seen);}catch{}}}

    static void RawPut(string url,string token,byte[] pdf){WriteLog("PUT v12 RAW -> "+url+" | bytes="+pdf.Length+" | Content-Type=application/pdf | Accept=public.v1+json");var req=(HttpWebRequest)WebRequest.Create(url);req.Method="PUT";req.ContentType="application/pdf";req.Accept="application/vnd.allegro.public.v1+json";req.Headers[HttpRequestHeader.Authorization]="Bearer "+token;req.ContentLength=pdf.Length;req.Timeout=30000;req.ReadWriteTimeout=30000;using(var s=req.GetRequestStream())s.Write(pdf,0,pdf.Length);try{using(var r=(HttpWebResponse)req.GetResponse()){string b="";var rs=r.GetResponseStream();if(rs!=null)using(var sr=new StreamReader(rs))b=sr.ReadToEnd();WriteLog("PUT RAW HTTP "+(int)r.StatusCode+" "+r.StatusCode+" | "+Short(b));if((int)r.StatusCode<200||(int)r.StatusCode>=300)throw new Exception("PUT HTTP "+(int)r.StatusCode+": "+b);}}catch(WebException we){string b="";var rr=we.Response as HttpWebResponse;if(rr!=null){try{using(var sr=new StreamReader(rr.GetResponseStream()))b=sr.ReadToEnd();}catch{}WriteLog("PUT RAW HTTP "+(int)rr.StatusCode+" "+rr.StatusCode+" | "+Short(b));throw new Exception("PUT RAW HTTP "+(int)rr.StatusCode+": "+b,we);}throw;}}

    static string GetStringProp(object o,string n){try{var p=o.GetType().GetProperty(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);return p==null?null:Convert.ToString(p.GetValue(o,null));}catch{return null;}}
    class RefCmp:IEqualityComparer<object>{public new bool Equals(object a,object b){return Object.ReferenceEquals(a,b);}public int GetHashCode(object o){return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);}}
    static string ResponseText(object o){if(o==null)return"";if(o is string)return(string)o;Type t=o.GetType();foreach(string n in new[]{"String","Object","Array","Content","Body","Response","Text","Data","Result"})try{var p=t.GetProperty(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);if(p!=null){var v=p.GetValue(o,null);if(v!=null){var s=Convert.ToString(v);if(!String.IsNullOrWhiteSpace(s))return s;}}}catch{}return Convert.ToString(o);}
    static string ExtractIdFromText(string s){if(String.IsNullOrEmpty(s))return null;int p=s.IndexOf("\"id\"",StringComparison.OrdinalIgnoreCase);if(p<0)return null;int colon=s.IndexOf(':',p),q1=s.IndexOf('"',colon+1),q2=q1<0?-1:s.IndexOf('"',q1+1);return q1>=0&&q2>q1?s.Substring(q1+1,q2-q1-1):null;}
    static string Short(string s){s=s??"";return s.Length>1200?s.Substring(0,1200):s;}static Assembly EuAsm(){return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x=>String.Equals(x.GetName().Name,"EasyUploader",StringComparison.OrdinalIgnoreCase));}
    static string FindMatchingXml(DateTime t){try{return Directory.GetFiles(Dir,"*.xml").Select(p=>new FileInfo(p)).Where(f=>f.LastWriteTime<=t.AddMinutes(2)&&f.LastWriteTime>=t.AddMinutes(-30)).OrderByDescending(f=>f.LastWriteTime).Select(f=>f.FullName).FirstOrDefault();}catch{return null;}}
    static string GetText(string xml,string node){try{var d=new XmlDocument();d.LoadXml(xml);var n=d.SelectSingleNode("//DaneTransStruct/"+node)??d.SelectSingleNode("//"+node);return n==null?null:(n.InnerText??"").Trim();}catch{return null;}}
    static bool WaitReady(string p,int sec){for(int i=0;i<sec*2;i++){try{using(var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)){if(f.Length>0)return true;}}catch{Thread.Sleep(500);}}return false;}
    static string Hash(byte[] b){using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(b)).Replace("-","");}static bool AlreadySent(string k){try{return File.Exists(Sent)&&File.ReadAllLines(Sent).Any(x=>x.Contains(k));}catch{return false;}}static void MarkSent(string s){try{File.AppendAllText(Sent,DateTime.Now.ToString("s")+"|"+s+Environment.NewLine,Encoding.UTF8);}catch{}}static void WriteLog(string s){try{Directory.CreateDirectory(Dir);File.AppendAllText(Log,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" | "+s+Environment.NewLine,Encoding.UTF8);}catch{}}
}
