using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

public class PluginEU
{
    private static readonly object Sync = new object();
    private static FileSystemWatcher _watcher;
    private static Timer _timer;
    private static bool _started;
    private static DateTime _lastEvent = DateTime.MinValue;
    private static readonly string Dir = @"C:\ImporterEU";
    private static readonly string Pdf = @"C:\ImporterEU\Faktura.pdf";
    private static readonly string Log = @"C:\ImporterEU\FakturyAuto.log";
    private static readonly string Sent = @"C:\ImporterEU\FakturyAuto_sent.txt";

    public static string WersjaEU
    {
        get
        {
            StartOnce();
            return "3.44.0";
        }
    }

    [DisplayName("Faktury Allegro AUTO - status")]
    public static string[] T_FakturyAuto_status(object zazn)
    {
        StartOnce();
        string msg = "ELEKTROMET FakturyAuto działa w tle.\r\n\r\nObserwowany plik: " + Pdf +
                     "\r\nLog: " + Log +
                     "\r\n\r\nPo utworzeniu nowej faktury przez ImporterEU automat spróbuje dołączyć PDF do zamówienia Allegro.";
        MessageBox.Show(msg, "ELEKTROMET - FakturyAuto", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return new[] { "UPDATE TRANSAKCJE SET ID=ID WHERE 1=0" };
    }

    [DisplayName("Faktury Allegro AUTO - testuj ostatnią fakturę")]
    public static string[] T_FakturyAuto_test(object zazn)
    {
        StartOnce();
        ThreadPool.QueueUserWorkItem(_ => ProcessPdf("TEST RĘCZNY"));
        MessageBox.Show("Test uruchomiony. Wynik będzie w:\r\n" + Log, "ELEKTROMET - FakturyAuto", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return new[] { "UPDATE TRANSAKCJE SET ID=ID WHERE 1=0" };
    }

    private static void StartOnce()
    {
        lock (Sync)
        {
            if (_started) return;
            _started = true;
            try
            {
                Directory.CreateDirectory(Dir);
                WriteLog("START automatu. BaseDir=" + AppDomain.CurrentDomain.BaseDirectory);
                _watcher = new FileSystemWatcher(Dir, "Faktura.pdf");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.FileName;
                _watcher.Changed += OnPdf;
                _watcher.Created += OnPdf;
                _watcher.Renamed += OnPdfRenamed;
                _watcher.EnableRaisingEvents = true;
                _timer = new Timer(_ => Poll(), null, 5000, 5000);
            }
            catch (Exception ex) { WriteLog("BŁĄD START: " + ex); }
        }
    }

    private static void OnPdf(object sender, FileSystemEventArgs e)
    {
        _lastEvent = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); ProcessPdf("WATCHER " + e.ChangeType); });
    }

    private static void OnPdfRenamed(object sender, RenamedEventArgs e)
    {
        _lastEvent = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); ProcessPdf("WATCHER RENAME"); });
    }

    private static DateTime _lastSeenWrite = DateTime.MinValue;
    private static void Poll()
    {
        try
        {
            if (!File.Exists(Pdf)) return;
            var t = File.GetLastWriteTime(Pdf);
            if (t > _lastSeenWrite && (DateTime.Now - t).TotalMinutes < 15)
            {
                _lastSeenWrite = t;
                if ((DateTime.Now - _lastEvent).TotalSeconds > 4)
                    ProcessPdf("POLL");
            }
        }
        catch { }
    }

    private static int _processing;
    private static void ProcessPdf(string reason)
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1) return;
        try
        {
            if (!File.Exists(Pdf)) return;
            if (!WaitReady(Pdf, 20)) { WriteLog("PDF nadal zablokowany: " + Pdf); return; }

            var pdfInfo = new FileInfo(Pdf);
            if (pdfInfo.Length < 1000) { WriteLog("PDF zbyt mały: " + pdfInfo.Length); return; }

            string xmlPath = FindMatchingXml(pdfInfo.LastWriteTime);
            if (xmlPath == null) { WriteLog("Nie znaleziono pasującego XML dla PDF. Powód=" + reason); return; }

            string xml = File.ReadAllText(xmlPath, Encoding.UTF8);
            int idTrans = ExtractTransactionId(xml);
            string checkoutId = ExtractCheckoutId(xml);
            string account = ExtractAccount(xml);

            // Jeśli UUID nie był bezpośrednio w XML, próbujemy odczytać dane transakcji z EasyUploader.
            object dt = null;
            if (idTrans > 0)
            {
                dt = LoadTransaction(idTrans);
                if (dt != null)
                {
                    if (String.IsNullOrWhiteSpace(checkoutId)) checkoutId = FindGuidInObject(dt);
                    if (String.IsNullOrWhiteSpace(account)) account = FindAccountInObject(dt);
                }
            }

            WriteLog("NOWY PDF: " + pdfInfo.Length + " B | XML=" + Path.GetFileName(xmlPath) + " | ID_TRANS=" + idTrans + " | checkout=" + checkoutId + " | konto=" + account + " | " + reason);

            if (String.IsNullOrWhiteSpace(checkoutId))
            {
                WriteLog("STOP: nie udało się ustalić checkoutForm.id zamówienia Allegro.");
                return;
            }

            byte[] bytes = ReadStable(Pdf);
            string hash = Hash(bytes);
            string key = checkoutId + "|" + hash;
            if (AlreadySent(key)) { WriteLog("POMINIĘTO duplikat: " + checkoutId); return; }

            string token = FindAllegroAccessToken(account);
            if (String.IsNullOrWhiteSpace(token))
            {
                WriteLog("STOP: nie udało się pobrać access tokenu Allegro z EasyUploader dla konta '" + account + "'.");
                DumpAllegroClientMetadata(account);
                return;
            }

            string invoiceNo = "FS-AUTO-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string invId = UploadToAllegro(token, checkoutId, invoiceNo, bytes).GetAwaiter().GetResult();
            MarkSent(key + "|" + invoiceNo + "|" + invId);
            WriteLog("SUKCES: faktura dołączona do Allegro. checkout=" + checkoutId + " invoiceId=" + invId + " numer=" + invoiceNo);
        }
        catch (Exception ex)
        {
            WriteLog("BŁĄD PROCESS: " + ex);
        }
        finally { Interlocked.Exchange(ref _processing, 0); }
    }

    private static bool WaitReady(string path, int seconds)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            try { using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { if (f.Length > 0) return true; } }
            catch { Thread.Sleep(500); }
        }
        return false;
    }

    private static byte[] ReadStable(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try { return File.ReadAllBytes(path); } catch { Thread.Sleep(500); }
        }
        return File.ReadAllBytes(path);
    }

    private static string FindMatchingXml(DateTime pdfTime)
    {
        try
        {
            var files = Directory.GetFiles(Dir, "*.xml")
                .Select(p => new FileInfo(p))
                .Where(f => f.LastWriteTime <= pdfTime.AddMinutes(2) && f.LastWriteTime >= pdfTime.AddMinutes(-30))
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            return files.Count == 0 ? null : files[0].FullName;
        }
        catch { return null; }
    }

    private static int ExtractTransactionId(string xml)
    {
        try
        {
            var d = new XmlDocument(); d.LoadXml(xml);
            var n = d.SelectSingleNode("/ArrayOfDaneTransStruct/DaneTransStruct/ID") ?? d.SelectSingleNode("//DaneTransStruct/ID") ?? d.SelectSingleNode("//ID");
            int x; if (n != null && Int32.TryParse(n.InnerText.Trim(), out x)) return x;
        }
        catch { }
        return 0;
    }

    private static string ExtractCheckoutId(string text)
    {
        var matches = Regex.Matches(text ?? "", @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b");
        if (matches.Count == 0) return null;
        // checkoutForm Allegro jest UUID; w eksporcie zwykle to jedyny UUID.
        return matches[0].Value.ToLowerInvariant();
    }

    private static string ExtractAccount(string xml)
    {
        try
        {
            var d = new XmlDocument(); d.LoadXml(xml);
            foreach (XmlNode n in d.SelectNodes("//*"))
            {
                string name = (n.Name ?? "").ToLowerInvariant();
                if ((name.Contains("konto") || name.Contains("account") || name.Contains("sprzed") || name.Contains("seller")) && n.ChildNodes.Count == 1)
                {
                    string v = (n.InnerText ?? "").Trim();
                    if (v.Length > 1 && v.Length < 100) return v;
                }
            }
        }
        catch { }
        return null;
    }

    private static Assembly EuAsm()
    {
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name.Equals("EasyUploader", StringComparison.OrdinalIgnoreCase));
    }

    private static object LoadTransaction(int id)
    {
        try
        {
            var a = EuAsm(); if (a == null) return null;
            var t = a.GetType("EasyUploader.Features.Transakcje.TransakcjeRepository");
            var m = t == null ? null : t.GetMethod("WczytajTransakcjeOrderBy", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) return null;
            var list = m.Invoke(null, new object[] { id.ToString() }) as IEnumerable;
            if (list != null) foreach (var x in list) return x;
        }
        catch (Exception ex) { WriteLog("LoadTransaction: " + ex.GetBaseException().Message); }
        return null;
    }

    private static string FindGuidInObject(object o)
    {
        if (o == null) return null;
        try
        {
            var vals = ReadNamedStrings(o, 2);
            foreach (var kv in vals.OrderByDescending(k => GuidNameScore(k.Key)))
            {
                var m = Regex.Match(kv.Value ?? "", @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b");
                if (m.Success) return m.Value.ToLowerInvariant();
            }
        }
        catch { }
        return null;
    }

    private static int GuidNameScore(string n)
    {
        n = (n ?? "").ToLowerInvariant(); int s = 0;
        if (n.Contains("checkout")) s += 100; if (n.Contains("order")) s += 50; if (n.Contains("allegro")) s += 30; if (n.Contains("id")) s += 10;
        return s;
    }

    private static string FindAccountInObject(object o)
    {
        try
        {
            foreach (var kv in ReadNamedStrings(o, 2).OrderByDescending(k => AccountNameScore(k.Key)))
                if (AccountNameScore(kv.Key) > 0 && !String.IsNullOrWhiteSpace(kv.Value) && kv.Value.Length < 100) return kv.Value;
        }
        catch { }
        return null;
    }

    private static int AccountNameScore(string n)
    {
        n = (n ?? "").ToLowerInvariant(); int s = 0;
        if (n.Contains("konto")) s += 100; if (n.Contains("account")) s += 90; if (n.Contains("seller")) s += 60; if (n.Contains("sprzed")) s += 60; if (n.Contains("allegro")) s += 20;
        return s;
    }

    private static Dictionary<string,string> ReadNamedStrings(object root, int depth)
    {
        var r = new Dictionary<string,string>(); var seen = new HashSet<object>(ReferenceComparer.Instance);
        Walk(root, root == null ? "" : root.GetType().Name, depth, r, seen); return r;
    }

    private static void Walk(object o, string path, int depth, Dictionary<string,string> r, HashSet<object> seen)
    {
        if (o == null || depth < 0) return;
        if (o is string) { r[path] = (string)o; return; }
        var t = o.GetType(); if (t.IsPrimitive || t.IsEnum || t == typeof(DateTime) || t == typeof(decimal)) return;
        if (!t.IsValueType) { if (seen.Contains(o)) return; seen.Add(o); }
        foreach (var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            try { var v = p.GetValue(o, null); if (v is string) r[path+"."+p.Name]=(string)v; else if (depth>0 && v!=null && !p.PropertyType.Namespace.StartsWith("System")) Walk(v,path+"."+p.Name,depth-1,r,seen); } catch { }
        }
        foreach (var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
        {
            try { var v=f.GetValue(o); if(v is string) r[path+"."+f.Name]=(string)v; else if(depth>0 && v!=null && !(f.FieldType.Namespace??"").StartsWith("System")) Walk(v,path+"."+f.Name,depth-1,r,seen); } catch { }
        }
    }

    private static string FindAllegroAccessToken(string account)
    {
        try
        {
            var a = EuAsm(); if (a == null) return null;
            var clientType = a.GetType("EasyUploader.Features.Markety.Clients.AllegroClient");
            var candidates = new List<Tuple<int,string>>();

            if (clientType != null)
            {
                object client = TryCreate(clientType, account);
                if (client != null) CollectTokens(client, clientType.Name, 3, candidates, new HashSet<object>(ReferenceComparer.Instance));
            }

            // Szukamy także statycznych repozytoriów/ustawień Allegro już załadowanych przez EU.
            foreach (var t in SafeTypes(a).Where(x => x.FullName.IndexOf("Allegro", StringComparison.OrdinalIgnoreCase)>=0 || x.FullName.IndexOf("OAuth", StringComparison.OrdinalIgnoreCase)>=0))
            {
                foreach (var p in t.GetProperties(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
                {
                    if (p.GetIndexParameters().Length!=0) continue;
                    try { var v=p.GetValue(null,null); if(v!=null) CollectTokens(v,t.FullName+"."+p.Name,2,candidates,new HashSet<object>(ReferenceComparer.Instance)); } catch { }
                }
                foreach (var f in t.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))
                {
                    try { var v=f.GetValue(null); if(v!=null) CollectTokens(v,t.FullName+"."+f.Name,2,candidates,new HashSet<object>(ReferenceComparer.Instance)); } catch { }
                }
            }

            var best = candidates.OrderByDescending(x=>x.Item1).FirstOrDefault();
            if (best != null)
            {
                WriteLog("Token Allegro znaleziony: score="+best.Item1+" długość="+best.Item2.Length);
                return best.Item2;
            }
        }
        catch (Exception ex) { WriteLog("FindToken: "+ex.GetBaseException().Message); }
        return null;
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(x=>x!=null); }
    }

    private static object TryCreate(Type t, string account)
    {
        foreach (var c in t.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).OrderBy(x=>x.GetParameters().Length))
        {
            try
            {
                var ps=c.GetParameters(); var args=new object[ps.Length]; bool ok=true;
                for(int i=0;i<ps.Length;i++)
                {
                    var pt=ps[i].ParameterType; string pn=(ps[i].Name??"").ToLowerInvariant();
                    if(pt==typeof(string)) args[i]=(pn.Contains("konto")||pn.Contains("account")||pn.Contains("login")) ? (account??"") : "";
                    else if(pt==typeof(bool)) args[i]=false;
                    else if(pt.IsEnum) args[i]=Enum.GetValues(pt).GetValue(0);
                    else if(pt.IsValueType) args[i]=Activator.CreateInstance(pt);
                    else if(ps[i].IsOptional) args[i]=ps[i].DefaultValue;
                    else args[i]=null;
                }
                if(ok) return c.Invoke(args);
            }
            catch { }
        }
        return null;
    }

    private static void CollectTokens(object o,string path,int depth,List<Tuple<int,string>> result,HashSet<object> seen)
    {
        if(o==null||depth<0) return; if(o is string){ScoreToken(path,(string)o,result);return;}
        var t=o.GetType(); if(t.IsPrimitive||t.IsEnum||t==typeof(DateTime)||t==typeof(decimal))return;
        if(!t.IsValueType){if(seen.Contains(o))return;seen.Add(o);}
        foreach(var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
        {
            if(p.GetIndexParameters().Length!=0)continue;
            try{var v=p.GetValue(o,null); if(v is string)ScoreToken(path+"."+p.Name,(string)v,result); else if(depth>0&&v!=null)CollectTokens(v,path+"."+p.Name,depth-1,result,seen);}catch{}
        }
        foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
        {
            try{var v=f.GetValue(o);if(v is string)ScoreToken(path+"."+f.Name,(string)v,result);else if(depth>0&&v!=null)CollectTokens(v,path+"."+f.Name,depth-1,result,seen);}catch{}
        }
    }

    private static void ScoreToken(string name,string value,List<Tuple<int,string>> result)
    {
        if(String.IsNullOrWhiteSpace(value)||value.Length<40||value.Length>5000)return;
        string n=(name??"").ToLowerInvariant(); int s=0;
        if(n.Contains("access")&&n.Contains("token"))s+=300;
        else if(n.Contains("token"))s+=120;
        if(n.Contains("refresh"))s-=250;
        if(n.Contains("allegro"))s+=80;
        if(value.StartsWith("eyJ",StringComparison.Ordinal))s+=200;
        if(value.Count(c=>c=='.')==2)s+=50;
        if(s>50)result.Add(Tuple.Create(s,value.Trim()));
    }

    private static async Task<string> UploadToAllegro(string token,string checkout,string invoiceNo,byte[] pdf)
    {
        using(var h=new HttpClient())
        {
            h.Timeout=TimeSpan.FromSeconds(45);
            h.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
            h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.allegro.public.v1+json"));
            var js=new JavaScriptSerializer();
            string body=js.Serialize(new Dictionary<string,object>{{"invoiceNumber",invoiceNo}});
            var c=new StringContent(body,Encoding.UTF8,"application/vnd.allegro.public.v1+json");
            var r=await h.PostAsync("https://api.allegro.pl/order/checkout-forms/"+checkout+"/invoices",c).ConfigureAwait(false);
            string txt=await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            if(!r.IsSuccessStatusCode)throw new Exception("Allegro POST invoice HTTP "+(int)r.StatusCode+": "+txt);
            string id=null;
            try{var d=js.DeserializeObject(txt) as Dictionary<string,object>; if(d!=null&&d.ContainsKey("id"))id=Convert.ToString(d["id"]);}catch{}
            if(String.IsNullOrWhiteSpace(id))
            {
                var m=Regex.Match(txt,"\\\"id\\\"\\s*:\\s*\\\"([^\\\"]+)\\\""); if(m.Success)id=m.Groups[1].Value;
            }
            if(String.IsNullOrWhiteSpace(id))throw new Exception("Allegro nie zwróciło invoiceId: "+txt);
            using(var b=new ByteArrayContent(pdf))
            {
                b.Headers.ContentType=new MediaTypeHeaderValue("application/pdf");
                var put=await h.PutAsync("https://api.allegro.pl/order/checkout-forms/"+checkout+"/invoices/"+id+"/file",b).ConfigureAwait(false);
                string ptxt=await put.Content.ReadAsStringAsync().ConfigureAwait(false);
                if(!put.IsSuccessStatusCode)throw new Exception("Allegro PUT PDF HTTP "+(int)put.StatusCode+": "+ptxt);
            }
            return id;
        }
    }

    private static void DumpAllegroClientMetadata(string account)
    {
        try
        {
            var a=EuAsm(); if(a==null)return; var sb=new StringBuilder();
            foreach(string tn in new[]{"EasyUploader.Features.Markety.Clients.AllegroClient","EasyUploader.Features.OAuth2.Credentials.AllegroCredentials"})
            {
                var t=a.GetType(tn); if(t==null)continue; sb.AppendLine("TYPE "+t.FullName);
                foreach(var c in t.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))sb.AppendLine("CTOR "+c);
                foreach(var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))sb.AppendLine("PROP "+p.PropertyType+" "+p.Name);
                foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic))sb.AppendLine("FIELD "+f.FieldType+" "+f.Name);
                foreach(var m in t.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))sb.AppendLine("METHOD "+m);
            }
            File.WriteAllText(Path.Combine(Dir,"FakturyAuto_AllegroClient_DIAG.txt"),sb.ToString(),Encoding.UTF8);
        }catch{}
    }

    private static string Hash(byte[] b){using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(b)).Replace("-","");}
    private static bool AlreadySent(string key){try{return File.Exists(Sent)&&File.ReadAllLines(Sent).Any(x=>x.StartsWith(key+"|",StringComparison.Ordinal)||x.Equals(key,StringComparison.Ordinal));}catch{return false;}}
    private static void MarkSent(string line){try{File.AppendAllText(Sent,DateTime.Now.ToString("s")+"|"+line+Environment.NewLine,Encoding.UTF8);}catch{}}
    private static void WriteLog(string s){try{Directory.CreateDirectory(Dir);File.AppendAllText(Log,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" | "+s+Environment.NewLine,Encoding.UTF8);}catch{}}

    private sealed class ReferenceComparer:IEqualityComparer<object>{public static readonly ReferenceComparer Instance=new ReferenceComparer();public new bool Equals(object x,object y){return Object.ReferenceEquals(x,y);}public int GetHashCode(object obj){return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);}}
}
