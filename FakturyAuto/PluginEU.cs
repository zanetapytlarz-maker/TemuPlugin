using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

public class PluginEU
{
    private static readonly object Sync = new object();
    private static FileSystemWatcher _watcher;
    private static Timer _timer;
    private static bool _started;
    private static DateTime _lastEvent = DateTime.MinValue;
    private static DateTime _lastSeenWrite = DateTime.MinValue;
    private static int _processing;
    private static readonly string Dir = @"C:\ImporterEU";
    private static readonly string Pdf = @"C:\ImporterEU\Faktura.pdf";
    private static readonly string Log = @"C:\ImporterEU\FakturyAuto.log";
    private static readonly string Sent = @"C:\ImporterEU\FakturyAuto_sent.txt";

    public static string WersjaEU { get { StartOnce(); return "3.44.0"; } }

    [DisplayName("Faktury Allegro AUTO - status")]
    public static string[] T_FakturyAuto_status(object zazn)
    {
        StartOnce();
        MessageBox.Show("FakturyAuto v4 działa w tle.\r\nUżywa teraz wewnętrznego mechanizmu EasyUploader do wysyłania faktur na Allegro.\r\n\r\nLog: " + Log, "ELEKTROMET - FakturyAuto v4", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return new[] { "UPDATE TRANSAKCJE SET ID=ID WHERE 1=0" };
    }

    [DisplayName("Faktury Allegro AUTO - testuj ostatnią fakturę")]
    public static string[] T_FakturyAuto_test(object zazn)
    {
        StartOnce();
        ThreadPool.QueueUserWorkItem(_ => ProcessPdf("TEST RĘCZNY"));
        return new[] { "UPDATE TRANSAKCJE SET ID=ID WHERE 1=0" };
    }

    private static void StartOnce()
    {
        lock (Sync)
        {
            if (_started) return; _started = true;
            try
            {
                Directory.CreateDirectory(Dir);
                WriteLog("START v4 NATIVE. BaseDir=" + AppDomain.CurrentDomain.BaseDirectory);
                _watcher = new FileSystemWatcher(Dir, "Faktura.pdf");
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.FileName;
                _watcher.Changed += (s,e) => { _lastEvent = DateTime.Now; ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); ProcessPdf("WATCHER " + e.ChangeType); }); };
                _watcher.Created += (s,e) => { _lastEvent = DateTime.Now; ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); ProcessPdf("WATCHER Created"); }); };
                _watcher.Renamed += (s,e) => { _lastEvent = DateTime.Now; ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); ProcessPdf("WATCHER Renamed"); }); };
                _watcher.EnableRaisingEvents = true;
                _timer = new Timer(_ => Poll(), null, 5000, 5000);
            }
            catch (Exception ex) { WriteLog("BŁĄD START: " + ex); }
        }
    }

    private static void Poll()
    {
        try
        {
            if (!File.Exists(Pdf)) return;
            var t = File.GetLastWriteTime(Pdf);
            if (t > _lastSeenWrite && (DateTime.Now - t).TotalMinutes < 15)
            {
                _lastSeenWrite = t;
                if ((DateTime.Now - _lastEvent).TotalSeconds > 4) ProcessPdf("POLL");
            }
        }
        catch { }
    }

    private static void ProcessPdf(string reason)
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1) return;
        try
        {
            if (!File.Exists(Pdf) || !WaitReady(Pdf, 20)) return;
            var fi = new FileInfo(Pdf); if (fi.Length < 1000) return;
            string xmlPath = FindMatchingXml(fi.LastWriteTime);
            if (xmlPath == null) { WriteLog("STOP: brak pasującego XML."); return; }
            string xml = File.ReadAllText(xmlPath, Encoding.UTF8);
            int idTrans = GetInt(xml, "ID");
            string account = GetText(xml, "Konto");
            string marketCountry = GetText(xml, "MarketKraj");
            string checkout = GetText(xml, "AllFodId");
            if (String.IsNullOrWhiteSpace(checkout)) checkout = ExtractGuid(xml);
            byte[] pdf = File.ReadAllBytes(Pdf);
            string key = checkout + "|" + Hash(pdf);
            WriteLog("NOWY PDF v4: " + fi.Length + " B | XML=" + Path.GetFileName(xmlPath) + " | ID_TRANS=" + idTrans + " | checkout=" + checkout + " | konto=" + account + " | kraj=" + marketCountry + " | " + reason);
            if (AlreadySent(key)) { WriteLog("POMINIĘTO duplikat: " + checkout); return; }
            if (idTrans <= 0 || String.IsNullOrWhiteSpace(account)) { WriteLog("STOP: brak ID transakcji lub konta."); return; }

            NativeSend(account, marketCountry, idTrans, pdf);
            MarkSent(key + "|NATIVE");
            WriteLog("SUKCES NATIVE: EasyUploader przyjął fakturę do wysyłki. checkout=" + checkout + " ID_TRANS=" + idTrans);
        }
        catch (TargetInvocationException tie) { WriteLog("BŁĄD NATIVE: " + (tie.InnerException ?? tie)); }
        catch (Exception ex) { WriteLog("BŁĄD PROCESS: " + ex); }
        finally { Interlocked.Exchange(ref _processing, 0); }
    }

    private static void NativeSend(string account, string marketCountry, int idTrans, byte[] pdf)
    {
        Assembly a = EuAsm(); if (a == null) throw new Exception("Brak załadowanego EasyUploader.exe");
        object trans = LoadTransaction(a, idTrans); if (trans == null) throw new Exception("Nie udało się wczytać DaneTransStruct dla ID=" + idTrans);
        Type transType = trans.GetType();
        Type docType = a.GetType("EasyUploader.Features.Fakturowanie.FakturaDokument");
        Type fwType = a.GetType("EasyUploader.Features.Fakturowanie.FakturowanieWystawienie");
        if (docType == null || fwType == null) throw new Exception("Brak klas Fakturowanie EasyUploader");

        object doc = CreateAny(docType); if (doc == null) throw new Exception("Nie udało się utworzyć FakturaDokument");
        string invoiceNo = FindInvoiceNumberFromPdf(pdf) ?? ("FS-AUTO-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        FillDocument(doc, invoiceNo, account, marketCountry, idTrans, pdf, Pdf);
        WriteLog("NATIVE dokument: numer=" + invoiceNo + " | typ=" + docType.FullName);
        DumpMembers(doc, "FakturaDokument");

        object transList = CreateGenericList(transType); transList.GetType().GetMethod("Add").Invoke(transList, new[] { trans });
        object docList = CreateGenericList(docType); docList.GetType().GetMethod("Add").Invoke(docList, new[] { doc });
        object fw = CreateAny(fwType); if (fw == null) throw new Exception("Nie udało się utworzyć FakturowanieWystawienie");

        MethodInfo create = fwType.GetMethod("FakturowanieServiceCreate", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if (create != null) create.Invoke(fw, new object[] { account });

        MethodInfo send = fwType.GetMethod("DokumentWyslijMarketplace", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        if (send == null) throw new Exception("Brak metody DokumentWyslijMarketplace");
        var ps = send.GetParameters();
        object postep = null;
        if (ps.Length >= 2 && ps[1].ParameterType != typeof(object)) postep = CreateAny(ps[1].ParameterType);
        send.Invoke(fw, new object[] { transList, postep, docList });
    }

    private static void FillDocument(object doc, string invoiceNo, string account, string country, int idTrans, byte[] pdf, string path)
    {
        Type t = doc.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length != 0) continue;
            object v; if (TryValue(p.Name, p.PropertyType, invoiceNo, account, country, idTrans, pdf, path, out v)) try { p.SetValue(doc, v, null); } catch { }
        }
        foreach (var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
        {
            object v; if (TryValue(f.Name, f.FieldType, invoiceNo, account, country, idTrans, pdf, path, out v)) try { f.SetValue(doc, v); } catch { }
        }
    }

    private static bool TryValue(string name, Type type, string nr, string account, string country, int id, byte[] pdf, string path, out object value)
    {
        value = null; string n = (name ?? "").ToLowerInvariant();
        if (type == typeof(byte[]) && (n.Contains("pdf") || n.Contains("plik") || n.Contains("dane") || n.Contains("content"))) { value = pdf; return true; }
        if (type == typeof(int) && (n.Contains("idtrans") || n.Contains("transid") || (n.Contains("trans") && n.Contains("id")))) { value = id; return true; }
        if (type == typeof(string))
        {
            if ((n.Contains("nr") || n.Contains("numer")) && (n.Contains("fv") || n.Contains("faktur"))) { value = nr; return true; }
            if (n.Contains("konto") || n.Contains("account")) { value = account; return true; }
            if (n.Contains("marketkraj") || (n.Contains("kraj") && !n.Contains("adres"))) { value = country; return true; }
            if (n.Contains("sciez") || n.Contains("path") || (n.Contains("plik") && !n.Contains("nazwa"))) { value = path; return true; }
            if (n.Contains("market") || n.Contains("serwis")) { value = "Allegro"; return true; }
        }
        return false;
    }

    private static object CreateAny(Type t)
    {
        try { return Activator.CreateInstance(t, true); } catch { }
        foreach (var c in t.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).OrderBy(x=>x.GetParameters().Length))
        {
            try
            {
                var ps=c.GetParameters(); var args=new object[ps.Length];
                for(int i=0;i<ps.Length;i++) args[i]=ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                return c.Invoke(args);
            } catch { }
        }
        return null;
    }

    private static object CreateGenericList(Type itemType) { return Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType)); }

    private static object LoadTransaction(Assembly a, int id)
    {
        Type t = a.GetType("EasyUploader.Features.Transakcje.TransakcjeRepository");
        MethodInfo m = t == null ? null : t.GetMethod("WczytajTransakcjeOrderBy", BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
        if (m == null) return null;
        var x = m.Invoke(null, new object[] { id.ToString() }) as IEnumerable;
        if (x != null) foreach (var item in x) return item;
        return null;
    }

    private static Assembly EuAsm() { return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => String.Equals(x.GetName().Name, "EasyUploader", StringComparison.OrdinalIgnoreCase)); }

    private static void DumpMembers(object o, string label)
    {
        try
        {
            var sb=new StringBuilder(); Type t=o.GetType(); sb.AppendLine("TYPE " + t.FullName);
            foreach(var c in t.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)) sb.AppendLine("CTOR " + c);
            foreach(var p in t.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)) { string val=""; try { var v=p.GetValue(o,null); if(v!=null) val=" = "+Safe(v); } catch{} sb.AppendLine("PROP " + p.PropertyType.FullName + " " + p.Name + val); }
            foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)) { string val=""; try { var v=f.GetValue(o); if(v!=null) val=" = "+Safe(v); } catch{} sb.AppendLine("FIELD " + f.FieldType.FullName + " " + f.Name + val); }
            File.WriteAllText(Path.Combine(Dir, "FakturyAuto_" + label + "_DIAG.txt"), sb.ToString(), Encoding.UTF8);
        } catch { }
    }
    private static string Safe(object v) { if (v is byte[]) return "byte[" + ((byte[])v).Length + "]"; string s=Convert.ToString(v); return s.Length>120?s.Substring(0,120):s; }

    private static string FindInvoiceNumberFromPdf(byte[] pdf)
    {
        try
        {
            string s = Encoding.GetEncoding(1252).GetString(pdf);
            var m = Regex.Match(s, @"(?:FS|FV)[\s:/-]*[0-9A-Za-z./_-]{3,40}", RegexOptions.IgnoreCase);
            if (m.Success) return Regex.Replace(m.Value, @"\s+", " ").Trim();
        } catch { }
        return null;
    }

    private static string FindMatchingXml(DateTime pdfTime)
    {
        try { return Directory.GetFiles(Dir, "*.xml").Select(p=>new FileInfo(p)).Where(f=>f.LastWriteTime<=pdfTime.AddMinutes(2)&&f.LastWriteTime>=pdfTime.AddMinutes(-30)).OrderByDescending(f=>f.LastWriteTime).Select(f=>f.FullName).FirstOrDefault(); } catch { return null; }
    }
    private static string GetText(string xml, string node) { try { var d=new XmlDocument(); d.LoadXml(xml); var n=d.SelectSingleNode("//DaneTransStruct/"+node) ?? d.SelectSingleNode("//"+node); return n==null?null:(n.InnerText??"").Trim(); } catch { return null; } }
    private static int GetInt(string xml, string node) { int x; return Int32.TryParse(GetText(xml,node), out x)?x:0; }
    private static string ExtractGuid(string s) { var m=Regex.Match(s??"",@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b"); return m.Success?m.Value.ToLowerInvariant():null; }
    private static bool WaitReady(string p,int sec){for(int i=0;i<sec*2;i++){try{using(var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)){if(f.Length>0)return true;}}catch{Thread.Sleep(500);}}return false;}
    private static string Hash(byte[] b){using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(b)).Replace("-","");}
    private static bool AlreadySent(string key){try{return File.Exists(Sent)&&File.ReadAllLines(Sent).Any(x=>x.Contains("|"+key+"|NATIVE")||x.EndsWith("|"+key+"|NATIVE")||x.Contains(key));}catch{return false;}}
    private static void MarkSent(string line){try{File.AppendAllText(Sent,DateTime.Now.ToString("s")+"|"+line+Environment.NewLine,Encoding.UTF8);}catch{}}
    private static void WriteLog(string s){try{Directory.CreateDirectory(Dir);File.AppendAllText(Log,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" | "+s+Environment.NewLine,Encoding.UTF8);}catch{}}
}
